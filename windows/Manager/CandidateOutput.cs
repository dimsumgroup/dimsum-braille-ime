using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json.Serialization;
using System.Windows.Forms.Automation;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Media.SpeechSynthesis;

namespace Dimsum;

[JsonConverter(typeof(JsonStringEnumConverter<CandidateOutput>))]
enum CandidateOutput { Off, Nvda, Jaws, Narrator, WindowsVoice }

readonly record struct ScreenReaders(bool Nvda, bool Jaws, bool Narrator)
{
    public static ScreenReaders Detect() => new(NvdaSession.Find() is not null,
        FindWindow("JFWUI2", null) != 0 || InCurrentSession("jfw"), InCurrentSession("Narrator"));

    public CandidateOutput Select(CandidateOutput? preference) => preference ?? (Nvda ? CandidateOutput.Nvda :
        Jaws ? CandidateOutput.Jaws : Narrator ? CandidateOutput.Narrator : CandidateOutput.Off);

    // UI Automation notifications are broadcast. Avoid sending one while
    // another detected screen reader could announce it as well.
    public bool CanNotifyNarrator => !Nvda && !Jaws;

    static bool InCurrentSession(string name)
    {
        using var current = Process.GetCurrentProcess();
        var processes = Process.GetProcessesByName(name);
        try { return processes.Any(process => IsInSession(process, current.SessionId)); }
        finally { foreach (var process in processes) process.Dispose(); }
    }

    static bool IsInSession(Process process, int session)
    {
        try { return process.SessionId == session && !process.HasExited; }
        catch (InvalidOperationException) { return false; }
        catch (System.ComponentModel.Win32Exception) { return false; }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern nint FindWindow(string className, string? windowName);
}

sealed class JawsSpeech : IDisposable
{
    object? api;

    public void Speak(string text)
    {
        if (api is null)
        {
            var type = Type.GetTypeFromProgID("FreedomSci.JAWSAPI", throwOnError: true)!;
            api = Activator.CreateInstance(type);
        }

        try
        {
            // Flush a preceding report so fast typing does not leave a speech queue.
            if (!((dynamic)api!).SayString(text, true))
                Dispose();
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (api is not null) Marshal.FinalReleaseComObject(api);
        api = null;
    }
}

sealed class NarratorSpeech : Form
{
    public NarratorSpeech()
    {
        Text = "點心粵語點字輸入法";
        ShowInTaskbar = false;
        FormBorderStyle = FormBorderStyle.FixedToolWindow;
        StartPosition = FormStartPosition.Manual;
        var desktop = SystemInformation.VirtualScreen;
        Bounds = new Rectangle(desktop.Right + 10, desktop.Bottom + 10, 120, 60);
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            parameters.ExStyle |= 0x08000080; // WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW
            return parameters;
        }
    }

    public void Speak(string text)
    {
        if (!Visible) Show();
        AccessibilityObject.RaiseAutomationNotification(AutomationNotificationKind.Other,
            AutomationNotificationProcessing.ImportantMostRecent, text);
    }
}

sealed class WindowsVoiceSpeech : IDisposable
{
    readonly MediaPlayer player = new();
    SpeechSynthesisStream? audio;
    MediaSource? source;
    CancellationTokenSource? pending;

    public static bool IsCantonese(string language) =>
        language.Equals("zh-HK", StringComparison.OrdinalIgnoreCase) ||
        language.StartsWith("yue", StringComparison.OrdinalIgnoreCase);

    public static VoiceInformation[] Voices()
    {
        try
        {
            return [.. SpeechSynthesizer.AllVoices.OrderByDescending(voice => IsCantonese(voice.Language))
                .ThenBy(voice => voice.DisplayName)];
        }
        catch (COMException) { return []; }
        catch (TypeLoadException) { return []; }
    }

    public async Task SpeakAsync(string text, string? voiceId, Func<bool> isCurrent)
    {
        Stop();
        var voices = Voices();
        var voice = voiceId is null ? voices.FirstOrDefault(item => IsCantonese(item.Language)) :
            voices.FirstOrDefault(item => item.Id == voiceId);
        if (voice is null) return;

        using var request = new CancellationTokenSource();
        pending = request;
        using var synth = new SpeechSynthesizer { Voice = voice };
        try
        {
            var result = await synth.SynthesizeTextToStreamAsync(text).AsTask(request.Token);
            if (request.IsCancellationRequested || !isCurrent())
            {
                result.Dispose();
                return;
            }
            audio = result;
            source = MediaSource.CreateFromStream(audio, audio.ContentType);
            player.Source = source;
            player.Play();
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        finally { if (pending == request) pending = null; }
    }

    public void Stop()
    {
        pending?.Cancel();
        if (audio is null) return;
        player.Pause();
        player.Source = null;
        source?.Dispose();
        audio.Dispose();
        source = null;
        audio = null;
    }

    public void Dispose()
    {
        Stop();
        player.Dispose();
    }
}

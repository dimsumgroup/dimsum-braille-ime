using System.Runtime.InteropServices;
using System.Text.Json;

namespace Dimsum;

sealed class CandidateSpeech : IDisposable
{
    readonly string data, settingsPath;
    readonly CandidateDescriptions descriptions;
    readonly SpeechLookup lookup;
    readonly JawsSpeech jaws = new();
    NarratorSpeech? narrator;
    WindowsVoiceSpeech? tts, lookupTts;
    Preferences settings = new();
    DateTime? settingsStamp;
    DateTime nextDetection;
    ScreenReaders readers;
    CandidateOutput? output;
    nint foreground;
    DateTime since = DateTime.UtcNow;
    (string Session, long Sequence)? observed, delivered;
    bool disposed;

    public CandidateSpeech(string data)
    {
        this.data = data;
        settingsPath = Path.Combine(data, "settings.json");
        descriptions = new(data);
        lookup = new(LookupCopiedSpeechAsync);
    }

    public async Task PollAsync()
    {
        if (disposed) return;
        var now = DateTime.UtcNow;
        LoadSettings();
        if (now >= nextDetection)
        {
            readers = ScreenReaders.Detect();
            nextDetection = now.AddMilliseconds(250);
        }
        lookup.Update(settings, readers);
        if (lookupTts is not null && SpeechLookup.SelectReader(settings, readers) != CandidateOutput.WindowsVoice)
        {
            lookupTts.Dispose();
            lookupTts = null;
        }

        var route = readers.Select(settings.CandidateSpeechOutput);
        var window = GetForegroundWindow();
        if (window != foreground || route != output)
        {
            foreground = window;
            output = route;
            since = now.AddMilliseconds(-45);
            observed = delivered = null;
            tts?.Stop();
            if (route != CandidateOutput.Jaws) jaws.Dispose();
            if (route != CandidateOutput.Narrator) narrator?.Hide();
        }
        if (!settings.Initialized || route is CandidateOutput.Off or CandidateOutput.Nvda || window == 0)
            return;

        if (route == CandidateOutput.Narrator && !readers.CanNotifyNarrator)
        {
            narrator?.Hide();
            return;
        }

        string prefix = settings.CandidateSpeechOutput is null ? "candidate-report-" : "external-candidate-report-";
        if (CandidateReport.ReadLatest(data, prefix, since) is not { } snapshot)
        {
            if (observed is not null)
            {
                observed = delivered = null;
                tts?.Stop();
            }
            return;
        }

        var (stamp, report) = snapshot;
        var id = (report.Session, report.Sequence);
        if (id != observed)
        {
            observed = id;
            tts?.Stop();
        }
        if (delivered == id || now - stamp > TimeSpan.FromSeconds(1) ||
            (report.Event is "page" or "tone" or "same" && now - stamp < TimeSpan.FromMilliseconds(80)))
            return;
        delivered = id;
        string text = report.Format(!settings.OmitCandidateNumbers,
            settings.PronounceCandidates ? descriptions.Describe : (word, _) => word);
        if (text.Length == 0 || GetForegroundWindow() != window) return;

        // Check again just before delivery, so a screen reader starting during
        // the polling interval cannot share this notification with Narrator.
        if (route != CandidateOutput.WindowsVoice)
        {
            readers = ScreenReaders.Detect();
            if (readers.Select(settings.CandidateSpeechOutput) != route ||
                route == CandidateOutput.Narrator && !readers.CanNotifyNarrator)
                return;
        }

        lookupTts?.Stop();
        switch (route)
        {
            case CandidateOutput.Jaws:
            case CandidateOutput.Narrator:
                SpeakWithReader(route, text);
                break;
            case CandidateOutput.WindowsVoice:
                tts ??= new();
                await tts.SpeakAsync(text, settings.CandidateVoiceId,
                    () => !disposed && observed == id && output == route && GetForegroundWindow() == window);
                break;
        }
    }

    async Task LookupCopiedSpeechAsync(string gesture)
    {
        LoadSettings();
        if (settings.LookupGesture != gesture ||
            SpeechLookup.SelectReader(settings, ScreenReaders.Detect()) is not { } reader) return;

        string reply;
        try
        {
            // Only read what the user explicitly copied and requested. Leave it
            // in the clipboard so another lookup can use the same original.
            string text = Clipboard.GetText(TextDataFormat.UnicodeText).Trim();
            reply = text.Length switch
            {
                0 => reader == CandidateOutput.WindowsVoice ? "請先複製要查嘅字詞。" : "請先從讀屏軟件複製要查嘅報讀。",
                > 1024 => "剪貼簿文字太長。請只複製要查嘅一句。",
                _ => descriptions.Lookup(text) ?? "呢段文字喺點心配詞表入面搵唔到配詞。"
            };
        }
        catch (ExternalException)
        {
            reply = "未能讀取剪貼簿。請重新複製要查嘅文字。";
        }

        try
        {
            tts?.Stop();
            if (reader == CandidateOutput.WindowsVoice)
            {
                lookupTts ??= new();
                await lookupTts.SpeakAsync(reply, settings.LookupVoiceId,
                    () => !disposed && settings.LookupGesture == gesture &&
                        SpeechLookup.SelectReader(settings, ScreenReaders.Detect()) == CandidateOutput.WindowsVoice);
            }
            else SpeakWithReader(reader, reply);
        }
        catch (Exception error) when (error is COMException or System.Reflection.TargetInvocationException or
            Microsoft.CSharp.RuntimeBinder.RuntimeBinderException or InvalidOperationException or TypeLoadException or FileNotFoundException)
        {
            // A speech API failure must not terminate the login helper.
        }
    }

    void SpeakWithReader(CandidateOutput reader, string text)
    {
        if (reader == CandidateOutput.Jaws) jaws.Speak(text);
        else
        {
            narrator ??= new();
            narrator.Speak(text);
        }
    }

    void LoadSettings()
    {
        try
        {
            DateTime stamp = File.GetLastWriteTimeUtc(settingsPath);
            if (stamp == settingsStamp) return;
            settings = Preferences.Read(File.ReadAllBytes(settingsPath));
            settingsStamp = stamp;
            // In particular cancel speech from the voice the user just changed.
            tts?.Stop();
            lookupTts?.Stop();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { }
    }

    public void Dispose()
    {
        disposed = true;
        lookup.Dispose();
        tts?.Dispose();
        lookupTts?.Dispose();
        narrator?.Dispose();
        jaws.Dispose();
    }

    [DllImport("user32.dll")]
    static extern nint GetForegroundWindow();
}

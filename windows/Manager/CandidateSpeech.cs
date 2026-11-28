using System.Runtime.InteropServices;
using System.Text.Json;

namespace Dimsum;

sealed class CandidateSpeech(string data) : IDisposable
{
    readonly string settingsPath = Path.Combine(data, "settings.json");
    readonly CandidateDescriptions descriptions = new(data);
    readonly JawsSpeech jaws = new();
    NarratorSpeech? narrator;
    WindowsVoiceSpeech? tts;
    Preferences settings = new();
    DateTime? settingsStamp;
    DateTime nextDetection;
    ScreenReaders readers;
    CandidateOutput? output;
    nint foreground;
    DateTime since = DateTime.UtcNow;
    (string Session, long Sequence)? observed, delivered;
    bool disposed;

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

        switch (route)
        {
            case CandidateOutput.Jaws:
                jaws.Speak(text);
                break;
            case CandidateOutput.Narrator:
                narrator ??= new();
                narrator.Speak(text);
                break;
            case CandidateOutput.WindowsVoice:
                tts ??= new();
                await tts.SpeakAsync(text, settings.CandidateVoiceId,
                    () => !disposed && observed == id && output == route && GetForegroundWindow() == window);
                break;
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
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { }
    }

    public void Dispose()
    {
        disposed = true;
        tts?.Dispose();
        narrator?.Dispose();
        jaws.Dispose();
    }

    [DllImport("user32.dll")]
    static extern nint GetForegroundWindow();
}

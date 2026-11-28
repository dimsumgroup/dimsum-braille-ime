using System.Media;

namespace Dimsum;

static class SingletonSound
{
    public const string Flag = "singleton-sound-enabled";
    public const string Event = "singleton-sound-event";
    public const string Service = "singleton-sound-service";

    public static void WaitForStop()
    {
        using var mutex = new Mutex(false, Program.SessionMutexName("DimsumSound"));
        bool acquired;
        try { acquired = mutex.WaitOne(TimeSpan.FromSeconds(3)); }
        catch (AbandonedMutexException) { acquired = true; }
        if (acquired) mutex.ReleaseMutex();
    }

    public static void Run(string directory)
    {
        string flag = Path.Combine(directory, Flag);
        string service = Path.Combine(directory, Service);
        string notification = Path.Combine(directory, Event);
        using var sound = new SoundPlayer(Path.Combine(AppContext.BaseDirectory, "assets", "only.wav"));
        try { sound.Load(); }
        catch (Exception error) when (error is IOException or InvalidOperationException or TimeoutException) { }
        string? last = null;
        var started = DateTime.UtcNow;
        using var speech = new CandidateSpeech(directory);
        using var timer = new System.Windows.Forms.Timer { Interval = 35 };
        timer.Tick += async (_, _) =>
        {
            if (!File.Exists(service))
            {
                timer.Stop();
                Application.ExitThread();
                return;
            }

            if (!File.Exists(flag)) started = DateTime.UtcNow;
            else
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(notification) >= started)
                    {
                        string value = File.ReadAllText(notification);
                        if (value.EndsWith('\n') && value != last)
                        {
                            last = value;
                            sound.Play();
                        }
                    }
                }
                catch (Exception error) when (error is IOException or InvalidOperationException or UnauthorizedAccessException) { }
            }

            try { await speech.PollAsync(); }
            catch (Exception error) when (error is System.Runtime.InteropServices.COMException or
                System.Reflection.TargetInvocationException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException or
                InvalidOperationException or TypeLoadException or FileNotFoundException) { }
        };
        if (!File.Exists(service)) return;
        timer.Start();
        Application.Run();
    }
}

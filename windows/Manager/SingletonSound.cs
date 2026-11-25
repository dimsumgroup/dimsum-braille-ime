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
        sound.Load();
        string? last = null;
        var started = DateTime.UtcNow;
        while (File.Exists(service))
        {
            if (!File.Exists(flag))
            {
                started = DateTime.UtcNow;
                Thread.Sleep(250);
                continue;
            }
            try
            {
                if (File.Exists(notification) && File.GetLastWriteTimeUtc(notification) >= started)
                {
                    string value = File.ReadAllText(notification);
                    if (value.EndsWith('\n') && value != last)
                    {
                        last = value;
                        sound.Play();
                    }
                }
            }
            catch (IOException) { }
            Thread.Sleep(35);
        }
    }
}

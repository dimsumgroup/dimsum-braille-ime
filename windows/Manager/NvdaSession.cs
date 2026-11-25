using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace Dimsum;

sealed record NvdaSession(int Id, string? Directory, DateTime? StartedUtc)
{
    internal sealed record Profile(string Language, string ConfigDirectory);

    public static NvdaSession? Find()
    {
        // NVDA uses this top-level window to find its existing instance.
        nint window = FindWindow("wxWindowClassNR", "NVDA");
        if (window != 0 && GetWindowThreadProcessId(window, out uint id) != 0 &&
            Describe(checked((int)id)) is { } active)
            return active;

        using var current = Process.GetCurrentProcess();
        int sessionId = current.SessionId;
        var processes = Process.GetProcessesByName("nvda");
        try
        {
            foreach (var process in processes)
            {
                try
                {
                    if (process.SessionId == sessionId && !process.HasExited && Describe(process.Id) is { } found)
                        return found;
                }
                catch (InvalidOperationException) { }
                catch (System.ComponentModel.Win32Exception) { }
            }
        }
        finally { foreach (var process in processes) process.Dispose(); }
        return null;
    }

    static NvdaSession? Describe(int id)
    {
        // NVDA can run with UIAccess. Its executable path can be queried using
        // limited rights even when reading its process modules is denied.
        using var process = OpenProcess(0x1000, false, id);
        if (process.IsInvalid) return new(id, null, null);
        uint size = 32768;
        var buffer = new StringBuilder(checked((int)size));
        string? directory = null;
        if (QueryFullProcessImageName(process, 0, buffer, ref size))
        {
            string path = buffer.ToString();
            if (!string.Equals(Path.GetFileName(path), "nvda.exe", StringComparison.OrdinalIgnoreCase))
                return null;
            directory = Path.GetDirectoryName(path);
        }
        DateTime? started = GetProcessTimes(process, out long creation, out _, out _, out _)
            ? DateTime.FromFileTimeUtc(creation) : null;
        return new(id, directory, started);
    }

    public Profile? ReadProfile(string data)
    {
        if (StartedUtc is not { } started) return null;
        string path = Path.Combine(data, $"nvda-running-{Id}.json");
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > 8192 || info.LastWriteTimeUtc < started) return null;
            var profile = JsonSerializer.Deserialize<Profile>(File.ReadAllBytes(path));
            return profile is not null && !string.IsNullOrWhiteSpace(profile.Language) &&
                !string.IsNullOrWhiteSpace(profile.ConfigDirectory) && Path.IsPathFullyQualified(profile.ConfigDirectory)
                ? profile : null;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
        catch (JsonException) { return null; }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern nint FindWindow(string className, string windowName);

    [DllImport("user32.dll")]
    static extern uint GetWindowThreadProcessId(nint window, out uint processId);

    [DllImport("kernel32.dll")]
    static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int id);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, StringBuilder image, ref uint size);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool GetProcessTimes(SafeProcessHandle process, out long creation, out long exit, out long kernel, out long user);
}

using System.Runtime.InteropServices;

namespace Dimsum;

sealed class SpeechLookup(Func<string, Task> lookup) : NativeWindow, IDisposable
{
    const int HotkeyId = 1;
    const int ProbeId = 2;
    const int HotkeyMessage = 0x0312;
    const uint NoRepeat = 0x4000;
    string? registered;
    DateTime retryAt;

    public static CandidateOutput? SelectReader(Preferences settings, ScreenReaders readers)
    {
        if (!settings.Initialized || settings.LookupGesture.Length == 0 || readers.Nvda) return null;
        if (settings.UseJawsSpeechLookup && readers.Jaws) return CandidateOutput.Jaws;
        if (settings.UseNarratorSpeechLookup && readers.Narrator && readers.CanNotifyNarrator)
            return CandidateOutput.Narrator;
        return settings.UseWindowsVoiceLookup ? CandidateOutput.WindowsVoice : null;
    }

    public void Update(Preferences settings, ScreenReaders readers)
    {
        string? desired = SelectReader(settings, readers) is null ? null : settings.LookupGesture;
        if (registered == desired) return;
        Unregister();
        if (desired is null || DateTime.UtcNow < retryAt || Parse(desired) is not { } key) return;
        if (Handle == 0) CreateHandle(new CreateParams { Parent = new nint(-3) }); // HWND_MESSAGE
        if (RegisterHotKey(Handle, HotkeyId, key.Modifiers | NoRepeat, key.VirtualKey))
            registered = desired;
        else
            retryAt = DateTime.UtcNow.AddSeconds(2);
    }

    public static void CheckAvailability(string gesture)
    {
        if (gesture.Length == 0) return;
        if (Parse(gesture) is not { } key || key.VirtualKey == (uint)Keys.F12 ||
            !RegisterHotKey(0, ProbeId, key.Modifiers | NoRepeat, key.VirtualKey))
            throw new IOException("呢組快捷鍵唔能夠用作點心查配詞。請揀另一組鍵。");
        UnregisterHotKey(0, ProbeId);
    }

    internal static (uint Modifiers, uint VirtualKey)? Parse(string gesture)
    {
        string[] parts = gesture.Split('+');
        if (parts.Length is < 2 or > 4) return null;
        uint modifiers = 0;
        foreach (string part in parts[..^1])
        {
            uint flag = part.ToUpperInvariant() switch
            {
                "ALT" => 1,
                "CONTROL" => 2,
                "SHIFT" => 4,
                "WINDOWS" => 8,
                _ => 0
            };
            if (flag == 0 || (modifiers & flag) != 0) return null;
            modifiers |= flag;
        }

        string key = parts[^1].ToUpperInvariant();
        if (key.Length == 1 && key[0] is >= 'A' and <= 'Z' or >= '0' and <= '9')
            return (modifiers, key[0]);
        if (key.StartsWith('F') && uint.TryParse(key.AsSpan(1), out uint number) && number is >= 1 and <= 12)
            return (modifiers, (uint)Keys.F1 + number - 1);
        return null;
    }

    protected override void WndProc(ref Message message)
    {
        if (message.Msg == HotkeyMessage && message.WParam == HotkeyId && registered is not null)
        {
            var key = Parse(registered)!.Value;
            if ((message.LParam.ToInt64() & 0xFFFF000F) == (key.VirtualKey << 16 | key.Modifiers))
                _ = lookup(registered);
            return;
        }
        base.WndProc(ref message);
    }

    void Unregister()
    {
        if (registered is not null) UnregisterHotKey(Handle, HotkeyId);
        registered = null;
    }

    public void Dispose()
    {
        Unregister();
        if (Handle != 0) DestroyHandle();
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool RegisterHotKey(nint window, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool UnregisterHotKey(nint window, int id);
}

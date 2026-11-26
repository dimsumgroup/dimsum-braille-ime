using System.Runtime.InteropServices;
using System.Windows.Forms.Automation;

namespace Dimsum;

sealed class ShortcutCapture : Form
{
    const string Instructions = "請按住 Ctrl、Alt、Shift 或 Windows，再按英文字母、數字或 F1 至 F12。按 Esc 可以取消。";
    readonly Label hint = new() { AutoSize = true, Text = Instructions, MaximumSize = new Size(440, 0) };
    public string Shortcut { get; private set; } = "";

    public ShortcutCapture()
    {
        Text = "按下查配詞快捷鍵";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        ShowInTaskbar = false;
        MaximizeBox = MinimizeBox = false;
        AutoScaleMode = AutoScaleMode.Font;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        AutoSize = true;

        var layout = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, Padding = new Padding(14), WrapContents = false };
        var cancel = new Button { Text = "取消", AutoSize = true, DialogResult = DialogResult.Cancel, AccessibleDescription = Instructions };
        layout.Controls.Add(hint);
        layout.Controls.Add(cancel);
        Controls.Add(layout);
        CancelButton = cancel;
        ActiveControl = cancel;
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (KeyName(keyData & Keys.KeyCode) is null)
            return base.ProcessCmdKey(ref msg, keyData);

        string? shortcut = FormatShortcut(keyData, (GetKeyState(Keys.LWin) & 0x8000) != 0 ||
            (GetKeyState(Keys.RWin) & 0x8000) != 0);
        if (shortcut is null)
        {
            hint.Text = "請用一至三個修飾鍵，再按英文字母、數字或 F1 至 F12。";
            hint.AccessibilityObject.RaiseAutomationNotification(AutomationNotificationKind.Other,
                AutomationNotificationProcessing.MostRecent, hint.Text);
            return true;
        }

        Shortcut = shortcut;
        DialogResult = DialogResult.OK;
        return true;
    }

    internal static bool ValidShortcut(string shortcut) => shortcut.Length == 0 || System.Text.RegularExpressions.Regex.IsMatch(shortcut,
        @"\A(?:(?:control|alt|shift|windows)\+){1,3}(?:[a-z0-9]|f(?:[1-9]|1[0-2]))\z",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    internal static string? FormatShortcut(Keys keyData, bool windows)
    {
        if (KeyName(keyData & Keys.KeyCode) is not { } name) return null;
        var modifiers = new List<string>(3);
        if ((keyData & Keys.Control) != 0) modifiers.Add("Control");
        if ((keyData & Keys.Alt) != 0) modifiers.Add("Alt");
        if ((keyData & Keys.Shift) != 0) modifiers.Add("Shift");
        if (windows) modifiers.Add("Windows");
        if (modifiers.Count is 0 or > 3) return null;
        modifiers.Add(name);
        return string.Join("+", modifiers);
    }

    static string? KeyName(Keys key) => key switch
    {
        >= Keys.A and <= Keys.Z => key.ToString(),
        >= Keys.D0 and <= Keys.D9 => ((char)('0' + (key - Keys.D0))).ToString(),
        >= Keys.F1 and <= Keys.F12 => key.ToString(),
        _ => null
    };

    [DllImport("user32.dll")]
    static extern short GetKeyState(Keys key);
}

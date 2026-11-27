using System.Runtime.InteropServices;
using System.Windows.Forms.Automation;

namespace Dimsum;

sealed class CopyDialog : Form
{
    CopyDialog(string title, string contents, bool report)
    {
        Text = title;
        AutoScaleMode = AutoScaleMode.Font;
        Size = new Size(650, report ? 470 : 350);
        MinimumSize = new Size(450, 265);
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        MinimizeBox = false;

        var details = new TextBox
        {
            AccessibleName = report ? "記錄內容" : "訊息內容",
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            Text = contents.ReplaceLineEndings()
        };
        var copy = new Button { Text = report ? "複製回報資料" : "複製訊息", AutoSize = true };
        var close = new Button { Text = "關閉", AutoSize = true, DialogResult = DialogResult.OK };
        var feedback = new Label { Dock = DockStyle.Fill, AutoSize = true };
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = false };
        buttons.Controls.AddRange([copy, close]);

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), ColumnCount = 1, RowCount = 3 };
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(details, 0, 0);
        layout.Controls.Add(buttons, 0, 1);
        layout.Controls.Add(feedback, 0, 2);
        Controls.Add(layout);

        details.TabIndex = 0;
        buttons.TabIndex = 1;
        copy.TabIndex = 0;
        close.TabIndex = 1;
        CancelButton = close;
        ActiveControl = details;
        details.Select(0, 0);
        copy.Click += (_, _) =>
        {
            string message;
            try
            {
                Clipboard.SetDataObject(details.Text, copy: true, retryTimes: 5, retryDelay: 100);
                message = report ? "已複製回報資料。" : "已複製訊息。";
            }
            catch (ExternalException) { message = "暫時未能複製。請再試。"; }
            feedback.Text = message;
            feedback.AccessibilityObject.RaiseAutomationNotification(AutomationNotificationKind.Other,
                AutomationNotificationProcessing.MostRecent, message);
        };
    }

    public static void Show(IWin32Window? owner, string title, string contents, bool report = false)
    {
        using var dialog = new CopyDialog(title, contents, report);
        if (owner is null)
        {
            dialog.StartPosition = FormStartPosition.CenterScreen;
            dialog.ShowDialog();
        }
        else dialog.ShowDialog(owner);
    }
}

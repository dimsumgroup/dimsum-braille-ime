using System.Windows.Forms.Automation;

namespace Dimsum;

sealed class ManagerForm : Form
{
    readonly Workspace workspace;
    readonly TableLayoutPanel form = new() { Dock = DockStyle.Fill, AutoScroll = true, ColumnCount = 1, Padding = new Padding(14) };
    readonly TextBox status = new() { Multiline = true, ReadOnly = true, Dock = DockStyle.Fill, ScrollBars = ScrollBars.Vertical, Height = 170, AccessibleName = "操作結果/版本" };
    readonly TextBox gesture = new() { MaxLength = 55, AccessibleName = "查配詞快捷鍵（可留空）", Width = 270 };
    Dictionary<string, string>? dataIndex;
    int dataCheck;
    string operationStatus = "";
    string announcedUpdates = "";

    public ManagerForm(Workspace workspace, bool firstLaunch)
    {
        this.workspace = workspace;
        Text = Program.Title;
        AutoScaleMode = AutoScaleMode.Font;
        MinimumSize = new Size(510, 550);
        Size = new Size(660, 725);
        form.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        Controls.Add(form);

        ushort targetLanguage = WindowsSetup.PreferredLanguage();
        var migration = new LegacyUpgrade(workspace);
        AddButton("01 安裝 Rime 輸入引擎（未安裝時）(&E)", async () =>
        {
            if (!await WindowsSetup.InstallEngineAsync()) return "已經裝有輸入引擎。可以揀步驟 02。";
            return "已安裝輸入引擎。請揀步驟 02。";
        });
        var defaultIme = new CheckBox { AutoSize = true, Text = "將點心設為目前預設輸入法" };
        Add(defaultIme);
        AddButton("02 設定並部署點心(&T)", async () =>
            await migration.RunAsync(this, targetLanguage, RefreshStatus) ?? await workspace.CompleteSetupAsync(targetLanguage, defaultIme.Checked));
        AddButton("安裝 NVDA 附加元件（NVDA使用者專用）(&N)", () =>
        {
            NvdaAddon.Install();
            return Task.FromResult("已開啟 NVDA 附加元件安裝檔。");
        });

        AddButton("檢查程式更新(&C)", async () =>
        {
            var found = await Workspace.NewReleaseAsync();
            if (found is not { } release) return "目前已經係最新 Windows 版本。";
            if (Confirm($"有 Windows 新版 {release.Version.ToString(3)}。開啟官方下載頁？"))
                Program.Open(release.Page);
            return $"有 Windows 版 {release.Version.ToString(3)}。";
        });

        AddButton("更新及部署 Rime 字庫(&D)", async () =>
        {
            await workspace.DownloadAsync(false);
            await workspace.DeployAsync(applyPending: true);
            return "已核對 Rime 字庫同部署結果。";
        }, recheckData: true);

        AddButton("重新部署或套用已下載字庫(&R)", async () =>
        {
            await workspace.DeployAsync(applyPending: true);
            return "已重新部署點心。";
        });

        AddButton("匯入舊點寫易自訂詞庫(&I)", async () =>
        {
            using var dialog = new OpenFileDialog
            {
                Title = "選擇舊點寫易自訂詞庫",
                FileName = "cb.dic",
                Filter = "詞庫 (*.dic)|*.dic|所有檔案 (*.*)|*.*",
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.CommonDocuments)
            };
            if (dialog.ShowDialog(this) != DialogResult.OK) return "";
            await workspace.DeployAsync(importFile: dialog.FileName);
            return "已合併並部署舊點寫易詞庫。";
        });

        AddButton("更新點心報讀配詞(&P)", async () =>
        {
            await workspace.DownloadAsync(true);
            if (workspace.Settings.AutoCopyPronunciations)
            {
                var copy = workspace.CopyToNvda();
                return copy.Copied ? $"已複製新配詞到 NVDA 用家設定（{copy.Scope}）。重啟 NVDA 後生效。" :
                    $"NVDA 用家設定（{copy.Scope}）已有同一份配詞。";
            }
            return "點心配詞已更新。NVDA 附加元件會讀取新版本。";
        }, recheckData: true);
        AddButton("立即複製配詞到 NVDA(&Y)", () =>
        {
            if (!Confirm("會更新 NVDA 用家設定入面嘅點心專用配詞。繼續嗎？")) return Task.FromResult("");
            var copy = workspace.CopyToNvda();
            return Task.FromResult(copy.Copied ? $"已複製點心配詞到 NVDA 用家設定（{copy.Scope}）。重啟 NVDA 後生效。" :
                $"NVDA 用家設定（{copy.Scope}）已有同一份配詞。");
        });
        AddButton("還原 NVDA 原有配詞檔", async () =>
        {
            if (!Confirm("會用舊版點心嘅備份，還原辨認到曾被覆寫嘅 NVDA 安裝配詞檔。繼續嗎？")) return "";
            await WindowsSetup.RunAdminAsync("--restore-nvda-dictionaries");
            return "";
        });

        AddPreference("配詞更新後自動複製到 NVDA(&F)", workspace.Settings.AutoCopyPronunciations,
            value => workspace.Settings.AutoCopyPronunciations = value);
        var shift = new CheckBox { AutoSize = true, Text = "單按 Shift 切換中英文", Checked = workspace.Settings.ShiftSwitch };
        var sound = new CheckBox { AutoSize = true, Text = "只有一個候選時播放提示音", Checked = workspace.SoundEnabled };
        Add(shift);
        Add(sound);
        AddButton("儲存打字設定", async () =>
        {
            bool previousShift = workspace.Settings.ShiftSwitch;
            bool? previousSound = workspace.Settings.SingleCandidateSound;
            workspace.Settings.ShiftSwitch = shift.Checked;
            workspace.Settings.SingleCandidateSound = sound.Checked;
            try { await workspace.DeployAsync(reporting: true); }
            catch
            {
                workspace.Settings.ShiftSwitch = previousShift;
                workspace.Settings.SingleCandidateSound = previousSound;
                throw;
            }
            return "已儲存打字設定。";
        });

        var modes = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, AccessibleName = "NVDA 候選報讀模式", Width = 500 };
        modes.Items.AddRange(["關閉點心候選報讀（使用原有 NVDA 設定）",
            "補讀模式（韻母和聲調第一個候選一樣時使用點心報讀方式）",
            "標準報讀模式（使用點心報讀方式）", "精簡報讀模式（不讀數字）"]);
        string[] choices = ["off", "tone", "standard", "compact"];
        modes.SelectedIndex = Math.Max(0, Array.IndexOf(choices, workspace.Settings.ReportingMode));
        Add(new Label { Text = "NVDA 候選報讀模式", AutoSize = true });
        Add(modes);
        var pronunciations = new CheckBox { AutoSize = true, Text = "候選用配詞報讀(&W)", Checked = workspace.Settings.PronounceCandidates };
        Add(pronunciations);
        Add(new Label { Text = "最近報讀查配詞快捷鍵（可留空）", AutoSize = true });
        gesture.Text = workspace.Settings.LookupGesture;
        Add(gesture);
        AddButton("按下查配詞快捷鍵(&K)", () =>
        {
            using var capture = new ShortcutCapture();
            if (capture.ShowDialog(this) != DialogResult.OK) return Task.FromResult("");
            gesture.Text = capture.Shortcut;
            return Task.FromResult($"已記下 {capture.Shortcut}。按「儲存報讀設定」生效。");
        });
        AddButton("清除查配詞快捷鍵", () =>
        {
            gesture.Clear();
            return Task.FromResult("快捷鍵已留空。按「儲存報讀設定」生效。");
        });
        AddButton("儲存報讀設定(&S)", async () =>
        {
            string shortcut = gesture.Text.Trim();
            if (shortcut.Length > 0 && !System.Text.RegularExpressions.Regex.IsMatch(shortcut,
                @"\A(?:(?:control|alt|shift|windows)\+){1,3}(?:[a-z0-9]|f(?:[1-9]|1[0-2]))\z",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                throw new IOException("快捷鍵請用 Control、Alt、Shift 或 Windows 加英文字母、數字或 F1 至 F12。");
            string mode = choices[modes.SelectedIndex];
            if (mode != "off" && (workspace.Settings.ReportingMode == "off" || workspace.ReportingNeedsDeployment))
                await workspace.DeployAsync(reporting: true);
            workspace.Settings.ReportingMode = mode;
            workspace.Settings.PronounceCandidates = pronunciations.Checked;
            workspace.Settings.LookupGesture = shortcut;
            workspace.Save();
            return "已儲存報讀設定。";
        });

        Add(new Label { Text = "操作結果/版本", AutoSize = true });
        Add(status);
        AddButton("開啟官方網站", () =>
        {
            Program.Open(Program.Website);
            return Task.FromResult("");
        });
        Add(new Label { Text = "移除點心粵語點字輸入法", AutoSize = true });
        AddButton("只移除點心，保留輸入引擎", () => BeginRemoval(removeEngine: false));
        AddButton("一併移除點心及輸入引擎", () => BeginRemoval(removeEngine: true));
        RefreshStatus(Workspace.FindDeployer() is null ? "首次使用請揀步驟 01，再揀步驟 02。" :
            workspace.SetupComplete ? "點心設定已完成。" : "已有輸入引擎。首次使用或升級後，請揀步驟 02。");
        Shown += async (_, _) => await CheckDataUpdatesAsync();
        if (firstLaunch)
            Shown += async (_, _) =>
            {
                try
                {
                    if (migration.NeedsUpgrade())
                        await RunOperationAsync(async () => await migration.RunAsync(this, targetLanguage, RefreshStatus) ?? "");
                }
                catch (Exception error) { RefreshStatus(error.Message); }
            };
    }

    Task<string> BeginRemoval(bool removeEngine)
    {
        string message = removeEngine
            ? "移除輸入引擎會影響其他使用同一引擎嘅輸入方案。會保留你嘅學習記錄同自訂詞庫。要繼續嗎？"
            : "移除點心後，其他使用同一引擎嘅輸入方案仍可繼續使用。會保留你嘅學習記錄同自訂詞庫。要繼續嗎？";
        if (Confirm(message))
        {
            WindowsSetup.LaunchUninstaller(removeEngine);
            BeginInvoke(Close);
        }
        return Task.FromResult("");
    }

    void AddPreference(string label, bool initial, Action<bool> apply)
    {
        var option = new CheckBox { AutoSize = true, Text = label, Checked = initial };
        option.CheckedChanged += (_, _) =>
        {
            try
            {
                apply(option.Checked);
                workspace.Save();
                RefreshStatus("");
            }
            catch (Exception error) { RefreshStatus(error.Message); }
        };
        Add(option);
    }

    void Add(Control control)
    {
        int row = form.RowCount++;
        form.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        form.Controls.Add(control, 0, row);
    }

    bool Confirm(string message) => MessageBox.Show(this, message, Text,
        MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) == DialogResult.Yes;

    void AddButton(string title, Func<Task<string>> action, bool recheckData = false)
    {
        var button = new Button { Text = title, AutoSize = true, MinimumSize = new Size(300, 29) };
        button.Click += async (_, _) => await RunOperationAsync(action, recheckData);
        Add(button);
    }

    async Task RunOperationAsync(Func<Task<string>> action, bool recheckData = false)
    {
        form.Enabled = false;
        try
        {
            string message = await action();
            RefreshStatus(message);
        }
        catch (Exception error) { RefreshStatus(error.Message); }
        finally
        {
            form.Enabled = true;
            status.Focus();
            status.Select(0, 0);
            if (recheckData) _ = CheckDataUpdatesAsync();
        }
    }

    async Task CheckDataUpdatesAsync()
    {
        int check = ++dataCheck;
        var index = await Workspace.CheckDataIndexAsync();
        if (IsDisposed || Disposing || check != dataCheck) return;
        dataIndex = index;
        RefreshStatus(operationStatus);
    }

    void RefreshStatus(string message)
    {
        operationStatus = message;
        string updates = "正在檢查 Rime 字庫同報讀配詞更新……";
        string announcement = "";
        if (dataIndex is not null)
        {
            var available = workspace.DataUpdates(dataIndex);
            string rime = available.Dictionary switch
            {
                DataUpdate.Available => "Rime 字庫有新版。按「更新及部署 Rime 字庫」更新。",
                DataUpdate.Downloaded => "新版 Rime 字庫已下載。按「重新部署或套用已下載字庫」生效。",
                DataUpdate.Current => "Rime 字庫已經係最新版本。",
                _ => "暫時未能檢查 Rime 字庫更新。"
            };
            string pronunciations = available.Pronunciations switch
            {
                DataUpdate.Available => "報讀配詞有新版。按「更新點心報讀配詞」更新。",
                DataUpdate.Current => "報讀配詞已經係最新版本。",
                _ => "暫時未能檢查報讀配詞更新。"
            };
            updates = rime + Environment.NewLine + pronunciations;
            if (available.Dictionary is DataUpdate.Available or DataUpdate.Downloaded) announcement = rime;
            if (available.Pronunciations == DataUpdate.Available)
                announcement = (announcement.Length == 0 ? "" : announcement + " ") + pronunciations;
        }
        status.Text = (message.Length == 0 ? "" : message + Environment.NewLine) + updates + Environment.NewLine + workspace.Status();
        if (announcement != announcedUpdates && form.Enabled && ActiveForm == this)
        {
            announcedUpdates = announcement;
            if (announcement.Length > 0)
                status.AccessibilityObject.RaiseAutomationNotification(AutomationNotificationKind.Other,
                    AutomationNotificationProcessing.CurrentThenMostRecent, announcement);
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!form.Enabled && e.CloseReason == CloseReason.UserClosing) e.Cancel = true;
        base.OnFormClosing(e);
    }
}

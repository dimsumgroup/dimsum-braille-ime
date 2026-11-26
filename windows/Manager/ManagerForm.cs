using System.Windows.Forms.Automation;

namespace Dimsum;

sealed class ManagerForm : Form
{
    const string ReviewDescription = "用 NVDA 原有快捷鍵連按兩下，讀出檢閱游標所在字嘅點心配詞。關閉後會用返 NVDA 原有解釋。";
    const string LookupLabel = "查最近報讀配詞快捷鍵（可留空）";
    const string LookupDescription = "有設定快捷鍵，就可以查 NVDA 最近讀過嘅字詞或句子。呢個快捷鍵同上面嘅開關各自生效。即係話，就算上面關閉咗，呢度你設定咗快捷鍵，你就可以好快咁對比兩套字表嘅解釋。";
    readonly Workspace workspace;
    readonly TableLayoutPanel form = new() { Dock = DockStyle.Fill, AutoScroll = true, ColumnCount = 1, Padding = new Padding(14) };
    readonly TextBox status = new() { Multiline = true, ReadOnly = true, Dock = DockStyle.Fill, ScrollBars = ScrollBars.Vertical, Height = 170, AccessibleName = "操作結果/版本" };
    readonly TextBox gesture = new() { MaxLength = 55, AccessibleName = LookupLabel, AccessibleDescription = LookupDescription, Width = 270 };
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
            return "點心配詞已更新。NVDA 附加元件會讀取新版本。";
        }, recheckData: true);
        AddButton("另存配詞表供 NVDA 獨立使用(&Y)", () => Task.FromResult(ExportPronunciations()));
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
        Add(new Label { Text = "NVDA 查配詞", AutoSize = true });
        var review = new CheckBox { Text = "使用點心字詞解釋", AccessibleDescription = ReviewDescription,
            AutoSize = true, Checked = workspace.Settings.UseDimsumReviewDescription };
        Add(review);
        AddExplanation(ReviewDescription);
        Add(new Label { Text = LookupLabel, AutoSize = true });
        gesture.Text = workspace.Settings.LookupGesture;
        Add(gesture);
        AddExplanation(LookupDescription);
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
            workspace.Settings.UseDimsumReviewDescription = review.Checked;
            workspace.Settings.LookupGesture = shortcut;
            workspace.Save();
            string mode = choices[modes.SelectedIndex];
            if (workspace.Settings.Initialized && mode != "off" && workspace.ReportingNeedsDeployment)
                await workspace.DeployAsync(reporting: true);
            workspace.Settings.ReportingMode = mode;
            workspace.Settings.PronounceCandidates = pronunciations.Checked;
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
                    else if (workspace.SetupComplete && workspace.Settings.Initialized &&
                             workspace.Settings.ReportingMode != "off" && workspace.ReportingNeedsDeployment)
                        await RunOperationAsync(async () =>
                        {
                            await workspace.DeployAsync(reporting: true);
                            return "已更新點心候選報讀。";
                        });
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

    string ExportPronunciations()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "選擇存放三份點心配詞檔嘅資料夾",
            UseDescriptionForTitle = true,
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return "";
        string destination = dialog.SelectedPath;
        try
        {
            NvdaDictionaries.CheckExportDestination(destination);
            bool overwrite = NvdaDictionaries.Names.Any(name => File.Exists(Path.Combine(destination, name)));
            if (overwrite && !Confirm("呢個資料夾已有同名配詞檔。要取代嗎？")) return "";
            bool backup = Confirm("要一併備份 NVDA 現時使用嘅配詞檔嗎？備份會放喺你揀嘅位置入面嘅「NVDA舊配詞表備份」資料夾。");
            workspace.ExportPronunciations(destination, overwrite);
            string saved = $"已將三份點心配詞檔另存到「{destination}」。";
            if (!backup) return saved;
            try
            {
                if (NvdaDictionaries.BackupCurrent(workspace.DataDirectory, destination) is not { } result)
                    return saved + Environment.NewLine + "未能確認 NVDA 現時使用嘅配詞檔位置。";
                if (result.Missing.Length > 0)
                    return saved + Environment.NewLine + $"NVDA 備份未齊，以下檔案未能備份：{string.Join("、", result.Missing)}。";
                return saved + Environment.NewLine + $"已將 NVDA 現用配詞檔備份到「{result.Location}」。";
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                return saved + Environment.NewLine + $"未能備份 NVDA 現用配詞檔。{error.Message}";
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.DecoderFallbackException)
        {
            return $"未能另存點心配詞檔。{error.Message}";
        }
    }

    void AddExplanation(string description) => Add(new Label
    {
        Text = description, AutoSize = true, Dock = DockStyle.Fill, TabStop = false
    });

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

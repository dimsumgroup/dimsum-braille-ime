using System.Globalization;
using System.Windows.Forms.Automation;

namespace Dimsum;

sealed class ManagerForm : Form
{
    const string ReviewDescription = "用 NVDA 原有快捷鍵連按兩下，讀出檢閱游標所在字嘅點心配詞。關閉後會用返 NVDA 原有解釋。";
    const string LookupLabel = "查最近報讀配詞快捷鍵（可留空）";
    const string LookupDescription = "有設定快捷鍵，就可以查 NVDA 最近讀過嘅字詞或句子。呢個快捷鍵同上面嘅開關各自生效。即係話，就算上面關閉咗，呢度你設定咗快捷鍵，你就可以好快咁對比兩套字表嘅解釋。";
    const string ShortcutFormatError = "快捷鍵請用 Control、Alt、Shift 或 Windows 加英文字母、數字或 F1 至 F12。";
    readonly Workspace workspace;
    readonly SemaphoreSlim settingChanges = new(1, 1);
    readonly Button setupAction = new() { AutoSize = true, MinimumSize = new Size(300, 29) };
    readonly Button repair = new() { AutoSize = true, MinimumSize = new Size(300, 29), Text = "修復點心(&F)" };
    readonly CheckBox defaultIme = new() { AutoSize = true, Text = "將點心設為目前預設輸入法" };
    readonly ComboBox inputLanguage = new() { DropDownStyle = ComboBoxStyle.DropDownList, AccessibleName = "點心輸入法語言", Width = 360 };
    readonly Label shortcutError = new() { AutoSize = true, Visible = false, TabStop = false };
    bool updatingSettings;
    bool choseInputLanguage;
    bool setupReady;
    readonly LegacyUpgrade migration;
    readonly TableLayoutPanel form = new() { Dock = DockStyle.Fill, AutoScroll = true, ColumnCount = 1, Padding = new Padding(14) };
    readonly TextBox status = new() { Multiline = true, ReadOnly = true, Dock = DockStyle.Fill, ScrollBars = ScrollBars.Vertical, Height = 170, AccessibleName = "操作結果/版本" };
    readonly TextBox gesture = new() { MaxLength = 55, AccessibleName = LookupLabel, AccessibleDescription = LookupDescription, Width = 270 };
    Dictionary<string, string>? dataIndex;
    int dataCheck;
    string operationStatus = "";
    string announcedUpdates = "";

    public ManagerForm(Workspace workspace, bool firstLaunch, ushort installerLanguage)
    {
        this.workspace = workspace;
        Text = Program.Title;
        AutoScaleMode = AutoScaleMode.Font;
        MinimumSize = new Size(510, 550);
        Size = new Size(660, 725);
        form.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        Controls.Add(form);

        migration = new LegacyUpgrade(workspace);
        setupReady = !firstLaunch && workspace.SetupComplete && Workspace.FindDeployer() is not null;
        Add(new Label { AutoSize = true, Text = "點心輸入法語言" });
        var initialLanguages = WindowsSetup.LanguageChoices([new(CultureInfo.CurrentUICulture.Name, []),
            new(InputLanguage.CurrentInputLanguage.Culture.Name, [])]);
        inputLanguage.Items.AddRange(initialLanguages);
        inputLanguage.SelectedItem = initialLanguages.FirstOrDefault(choice => WindowsSetup.SameLanguage(choice.Tag, workspace.Settings.InputLanguageTag)) ??
            initialLanguages.FirstOrDefault(choice => choice.Id == WindowsSetup.PreferredLanguage()) ?? initialLanguages[0];
        inputLanguage.SelectionChangeCommitted += (_, _) => choseInputLanguage = true;
        inputLanguage.SelectedIndexChanged += (_, _) => UpdateSetupAction();
        defaultIme.CheckedChanged += (_, _) => UpdateSetupAction();
        Add(inputLanguage);
        Add(defaultIme);
        setupAction.Click += async (_, _) => await RunOperationAsync(async () =>
        {
            var language = (InputLanguageChoice)inputLanguage.SelectedItem!;
            if (!setupReady) return await SetupAsync(language, repair: false);
            string result = await workspace.ApplyInputLanguageAsync(language, defaultIme.Checked);
            defaultIme.Checked = false;
            UpdateSetupAction();
            return result;
        });
        Add(setupAction);
        repair.Click += async (_, _) => await RunOperationAsync(() => SetupAsync((InputLanguageChoice)inputLanguage.SelectedItem!, repair: true));
        Add(repair);
        UpdateSetupAction();
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
        BindSetting(shift, () => workspace.Settings.ShiftSwitch, value => workspace.Settings.ShiftSwitch = value,
            "已套用 Shift 切換設定。", async () =>
            {
                if (workspace.Settings.Initialized) await workspace.DeployAsync(reporting: true, settingsOnly: true);
                else workspace.Save();
            });
        BindSetting(sound, () => workspace.SoundEnabled, value => workspace.Settings.SingleCandidateSound = value,
            "已套用單一候選提示音設定。");

        var modes = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, AccessibleName = "NVDA 候選報讀模式", Width = 500 };
        modes.Items.AddRange(["關閉點心候選報讀（使用原有 NVDA 設定）",
            "補讀模式（韻母和聲調第一個候選一樣時使用點心報讀方式）",
            "標準報讀模式（使用點心報讀方式）", "精簡報讀模式（不讀數字）"]);
        string[] choices = ["off", "tone", "standard", "compact"];
        modes.SelectedIndex = Math.Max(0, Array.IndexOf(choices, workspace.Settings.ReportingMode));
        Add(new Label { Text = "NVDA 候選報讀模式", AutoSize = true });
        Add(modes);
        modes.SelectionChangeCommitted += async (_, _) =>
        {
            string mode = choices[modes.SelectedIndex];
            await ApplySettingAsync(mode, () => workspace.Settings.ReportingMode, value => workspace.Settings.ReportingMode = value,
                previous => { if (choices[modes.SelectedIndex] == mode) modes.SelectedIndex = Math.Max(0, Array.IndexOf(choices, previous)); },
                "已套用候選報讀模式。", async () =>
                {
                    if (mode != "off" && workspace.Settings.Initialized && workspace.ReportingNeedsDeployment)
                        await workspace.DeployAsync(reporting: true, settingsOnly: true);
                    else workspace.Save();
                });
        };
        var pronunciations = new CheckBox { AutoSize = true, Text = "候選用配詞報讀(&W)", Checked = workspace.Settings.PronounceCandidates };
        Add(pronunciations);
        BindSetting(pronunciations, () => workspace.Settings.PronounceCandidates,
            value => workspace.Settings.PronounceCandidates = value, "已套用候選配詞設定。");
        Add(new Label { Text = "NVDA 查配詞", AutoSize = true });
        var review = new CheckBox { Text = "使用點心字詞解釋", AccessibleDescription = ReviewDescription,
            AutoSize = true, Checked = workspace.Settings.UseDimsumReviewDescription };
        Add(review);
        BindSetting(review, () => workspace.Settings.UseDimsumReviewDescription,
            value => workspace.Settings.UseDimsumReviewDescription = value, "已套用點心字詞解釋設定。");
        AddExplanation(ReviewDescription);
        Add(new Label { Text = LookupLabel, AutoSize = true });
        gesture.Text = workspace.Settings.LookupGesture;
        Add(gesture);
        AddExplanation(LookupDescription);
        Add(shortcutError);
        gesture.TextChanged += (_, _) => shortcutError.Visible = false;
        gesture.Leave += async (_, _) => { if (gesture.Modified) await SaveShortcutAsync(); };
        gesture.KeyDown += async (_, e) =>
        {
            if (e.KeyCode != Keys.Enter || e.Modifiers != Keys.None) return;
            e.SuppressKeyPress = true;
            await SaveShortcutAsync();
        };
        AddAction("按下查配詞快捷鍵(&K)", async () =>
        {
            using var capture = new ShortcutCapture();
            if (capture.ShowDialog(this) != DialogResult.OK) return;
            gesture.Text = capture.Shortcut;
            await SaveShortcutAsync();
        });
        AddAction("清除查配詞快捷鍵", async () =>
        {
            gesture.Clear();
            await SaveShortcutAsync();
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
        SetSetupControlsEnabled(!firstLaunch);
        RefreshStatus(firstLaunch ? "正在確認安裝時嘅 Windows 輸入語言。" : SetupStatus());
        Shown += async (_, _) =>
        {
            _ = CheckDataUpdatesAsync();
            ushort preferred = WindowsSetup.PreferredLanguage();
            UserInputLanguage[] configured = [];
            string? detectionError = null;
            try { configured = await WindowsSetup.GetUserInputLanguagesAsync(); }
            catch (Exception error) when (error is IOException or System.ComponentModel.Win32Exception)
            {
                detectionError = error.Message;
            }
            if (IsDisposed || Disposing) return;
            string? existing = WindowsSetup.ExistingDimsumLanguage(configured);
            var languages = WindowsSetup.LanguageChoices(configured);
            var previousChoice = inputLanguage.SelectedItem as InputLanguageChoice;
            var detected = firstLaunch ? WindowsSetup.DetectSetupLanguage(installerLanguage, configured) : null;
            string? saved = workspace.Settings.InputLanguageTag ?? existing;
            inputLanguage.Items.Clear();
            inputLanguage.Items.AddRange(languages);
            inputLanguage.SelectedItem = detected ??
                (choseInputLanguage ? languages.FirstOrDefault(choice => choice.Tag == previousChoice?.Tag) : null) ??
                languages.FirstOrDefault(choice => WindowsSetup.SameLanguage(choice.Tag, saved)) ??
                WindowsSetup.DetectSetupLanguage(preferred, configured) ??
                languages.FirstOrDefault(choice => choice.Id == preferred) ?? languages[0];
            if (!firstLaunch)
            {
                if (setupReady && detectionError is null &&
                    !languages.Any(choice => WindowsSetup.SameLanguage(choice.Tag, saved) && WindowsSetup.IsDimsumEnabled(configured, choice)))
                    setupReady = false;
                UpdateSetupAction();
                RefreshStatus(detectionError ?? SetupStatus());
                return;
            }

            SetSetupControlsEnabled(true);
            if (detected is not null)
                await RunOperationAsync(() => SetupAsync(detected, repair: false));
            else
                await RunOperationAsync(() =>
                {
                    workspace.MarkSetupPending();
                    return Task.FromResult("未能確認安裝時嘅 Windows 輸入語言。請揀語言，再按「繼續設定點心」。" +
                        (detectionError is null ? "" : Environment.NewLine + detectionError));
                });
        };
    }

    void SetSetupControlsEnabled(bool enabled)
    {
        inputLanguage.Enabled = enabled;
        defaultIme.Enabled = enabled;
        setupAction.Enabled = enabled;
        repair.Enabled = enabled;
    }

    void UpdateSetupAction()
    {
        setupAction.Text = setupReady ? "套用輸入法語言(&T)" : "繼續設定點心(&T)";
        setupAction.Visible = !setupReady || defaultIme.Checked ||
            (inputLanguage.SelectedItem is InputLanguageChoice language &&
             !WindowsSetup.SameLanguage(language.Tag, workspace.Settings.InputLanguageTag));
    }

    string SetupStatus()
    {
        if (!setupReady) return "點心設定尚未完成。可以按「繼續設定點心」。";
        return workspace.Settings.InputLanguageTag is { } tag && WindowsSetup.LanguageChoice(tag) is { } language
            ? $"點心設定已完成。目前輸入法語言係「{language.Label}」。"
            : "點心設定已完成。";
    }

    async Task<string> SetupAsync(InputLanguageChoice language, bool repair)
    {
        setupReady = false;
        UpdateSetupAction();
        workspace.MarkSetupPending();
        try
        {
            string? migrated = await migration.RunAsync(this, language, RefreshStatus, defaultIme.Checked);
            if (migrated == "") return "已暫停舊版遷移。準備好後可以按「繼續設定點心」。";
            string result;
            if (migrated is null)
            {
                if (Workspace.FindDeployer() is null)
                {
                    RefreshStatus("正在安裝 Rime 輸入引擎。");
                    try { await WindowsSetup.InstallEngineAsync(); }
                    catch (Exception error) { throw new IOException($"未能完成輸入引擎安裝。{Environment.NewLine}{error.Message}", error); }
                }
                result = await workspace.CompleteSetupAsync(language, defaultIme.Checked, progress: RefreshStatus, forceDeploy: repair);
            }
            else
                result = Workspace.LanguageAppliedMessage(language) + Environment.NewLine + migrated;
            setupReady = true;
            defaultIme.Checked = false;
            return result;
        }
        catch
        {
            workspace.MarkSetupPending();
            throw;
        }
        finally { UpdateSetupAction(); }
    }

    void BindSetting(CheckBox option, Func<bool> read, Action<bool> write, string message, Func<Task>? deploy = null)
    {
        option.CheckedChanged += async (_, _) =>
        {
            if (updatingSettings) return;
            bool requested = option.Checked;
            await ApplySettingAsync(requested, read, write, previous =>
            {
                if (option.Checked == requested) option.Checked = previous;
            }, message, deploy);
        };
    }

    async Task ApplySettingAsync<T>(T value, Func<T> read, Action<T> write, Action<T> restoreControl,
        string message, Func<Task>? deploy = null)
    {
        await settingChanges.WaitAsync();
        try
        {
            T previous = read();
            if (EqualityComparer<T>.Default.Equals(value, previous)) return;
            try
            {
                write(value);
                if (deploy is not null) await deploy();
                else workspace.Save();
            }
            catch (Exception error)
            {
                write(previous);
                updatingSettings = true;
                try { restoreControl(previous); }
                finally { updatingSettings = false; }
                string failure = $"未能套用設定。{Environment.NewLine}{error.Message}";
                RefreshStatus(failure);
                MessageBox.Show(this, failure, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            RefreshStatus(message);
            AnnounceSetting(message);
        }
        finally { settingChanges.Release(); }
    }

    Task SaveShortcutAsync()
    {
        string shortcut = gesture.Text.Trim();
        if (!ShortcutCapture.ValidShortcut(shortcut))
        {
            shortcutError.Text = ShortcutFormatError;
            shortcutError.Visible = true;
            RefreshStatus(ShortcutFormatError);
            AnnounceSetting(ShortcutFormatError);
            return Task.CompletedTask;
        }
        gesture.Text = shortcut;
        gesture.Modified = false;
        return ApplySettingAsync(shortcut, () => workspace.Settings.LookupGesture, value => workspace.Settings.LookupGesture = value,
            previous => { if (gesture.Text == shortcut) gesture.Text = previous; },
            shortcut.Length > 0 ? $"查配詞快捷鍵已設為 {shortcut}。" : "已清除查配詞快捷鍵。");
    }

    void AnnounceSetting(string message)
    {
        if (ActiveForm == this)
            status.AccessibilityObject.RaiseAutomationNotification(AutomationNotificationKind.Other,
                AutomationNotificationProcessing.CurrentThenMostRecent, message);
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

    void AddButton(string title, Func<Task<string>> action, bool recheckData = false) =>
        AddAction(title, () => RunOperationAsync(action, recheckData));

    void AddAction(string title, Func<Task> action)
    {
        var button = new Button { Text = title, AutoSize = true, MinimumSize = new Size(300, 29) };
        button.Click += async (_, _) => await action();
        Add(button);
    }

    async Task RunOperationAsync(Func<Task<string>> action, bool recheckData = false)
    {
        await settingChanges.WaitAsync();
        if (IsDisposed || Disposing || !form.Enabled)
        {
            settingChanges.Release();
            return;
        }
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
            settingChanges.Release();
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
        if ((!form.Enabled || settingChanges.CurrentCount == 0) && e.CloseReason == CloseReason.UserClosing) e.Cancel = true;
        else if (gesture.Modified && e.CloseReason == CloseReason.UserClosing) _ = SaveShortcutAsync();
        base.OnFormClosing(e);
    }
}

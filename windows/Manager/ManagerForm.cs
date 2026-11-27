using System.Windows.Forms.Automation;

namespace Dimsum;

sealed class ManagerForm : Form
{
    const string ReviewDescription = "用 NVDA 原有快捷鍵連按兩下，讀出檢閱游標所在字嘅點心配詞。關閉後會用返 NVDA 原有解釋。";
    const string LookupLabel = "查最近報讀配詞快捷鍵（可留空）";
    const string LookupDescription = "有設定快捷鍵，就可以查 NVDA 最近讀過嘅字詞或句子。呢個快捷鍵同上面嘅開關各自生效。即係話，就算上面關閉咗，呢度你設定咗快捷鍵，你就可以好快咁對比兩套字表嘅解釋。";
    readonly Workspace workspace;
    readonly OperationLog log;
    readonly TabControl tabs = new() { Dock = DockStyle.Fill };
    readonly Label status = new() { AutoSize = true, Dock = DockStyle.Fill, AccessibleName = "操作結果", Padding = new Padding(10, 5, 10, 8) };
    readonly Label shortcut = new() { AutoSize = true, TabStop = false };
    readonly CheckBox defaultIme = new() { AutoSize = true, Text = "將點心設為目前預設輸入法", Enabled = false };
    readonly ComboBox inputLanguage = new() { Width = 310, DropDownStyle = ComboBoxStyle.DropDownList, AccessibleName = "點心輸入法語言" };
    readonly System.Windows.Forms.Timer nvdaMonitor = new() { Interval = 3000 };
    readonly Button rimeUpdate, pronunciationUpdate, inputAddon, reviewAddon, continueSetup, applyLanguage, recordShortcut;
    (DataUpdate Dictionary, DataUpdate Pronunciations) dataUpdates;
    bool busy, refreshingControls, setupReady;

    public ManagerForm(Workspace workspace, bool setupProblem)
    {
        this.workspace = workspace;
        log = new OperationLog(workspace);
        setupReady = workspace.SetupComplete && Workspace.FindDeployer() is not null && !setupProblem && !new LegacyUpgrade(workspace).InProgress;
        Text = Program.Title;
        AutoScaleMode = AutoScaleMode.Font;
        MinimumSize = new Size(545, 485);
        Size = new Size(695, 575);
        StartPosition = FormStartPosition.CenterScreen;
        var layout = new TableLayoutPanel { ColumnCount = 1, RowCount = 2, Dock = DockStyle.Fill };
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(tabs, 0, 0);
        layout.Controls.Add(status, 0, 1);
        Controls.Add(layout);

        var input = AddPage("輸入設定");
        AddSetting(input, "聆聽候選時不報讀數字", () => workspace.Settings.OmitCandidateNumbers,
            value => workspace.Settings.OmitCandidateNumbers = value);
        AddSetting(input, "只有一個候選時不播放聲音", () => !workspace.SoundEnabled,
            value => workspace.Settings.SingleCandidateSound = !value);
        AddSetting(input, "輸入候選不讀配詞解釋", () => !workspace.Settings.PronounceCandidates,
            value => workspace.Settings.PronounceCandidates = !value);
        Add(input, defaultIme);
        defaultIme.CheckedChanged += async (_, _) =>
        {
            if (!refreshingControls) await RunOperationAsync(SetDefaultAsync);
        };
        AddSetting(input, "單按 Shift 切換中英文", () => workspace.Settings.ShiftSwitch,
            value => workspace.Settings.ShiftSwitch = value, save: SaveShiftAsync);
        AddButton(input, "匯入舊點寫易自訂詞庫", ImportAsync);
        rimeUpdate = AddButton(input, "更新輸入法字庫", () => UpdateDataAsync(rime: true, pronunciations: false));
        inputAddon = AddButton(input, "安裝 NVDA 附加組件", InstallAddonAsync);

        var review = AddPage("配詞解釋報讀設定");
        AddSetting(review, "在 NVDA 開啟點心配詞解釋", () => workspace.Settings.UseDimsumReviewDescription,
            value => workspace.Settings.UseDimsumReviewDescription = value, ReviewDescription);
        AddLabel(review, ReviewDescription);
        Add(review, shortcut);
        AddLabel(review, LookupDescription);
        recordShortcut = AddButton(review, "錄製查配詞快捷鍵", () =>
        {
            using var capture = new ShortcutCapture();
            return Task.FromResult(capture.ShowDialog(this) == DialogResult.OK ? SaveShortcut(capture.Shortcut) : "");
        });
        AddButton(review, "清除查配詞快捷鍵", () => Task.FromResult(SaveShortcut("")));
        AddButton(review, "另存配詞表供 NVDA 獨立使用", () => Task.FromResult(ExportPronunciations()));
        pronunciationUpdate = AddButton(review, "更新報讀配詞表", () => UpdateDataAsync(rime: false, pronunciations: true));
        reviewAddon = AddButton(review, "安裝 NVDA 附加組件", InstallAddonAsync);

        var support = AddPage("修復與支援");
        AddButton(support, "使用說明", () =>
        {
            string guide = Path.Combine(AppContext.BaseDirectory, "安裝及使用說明_Win.txt");
            if (!File.Exists(guide)) throw new FileNotFoundException("搵唔到使用說明檔案。請重新安裝點心管理器。");
            Program.Open(guide);
            return Task.FromResult("");
        });
        AddButton(support, "開啟官方網站", () =>
        {
            Program.Open(Program.Website);
            return Task.FromResult("");
        });
        continueSetup = AddButton(support, "繼續設定點心", () => SetupAsync(repair: false));
        AddButton(support, "修復點心", () => SetupAsync(repair: true));
        AddLabel(support, "點心輸入法語言（只顯示呢部電腦已驗證嘅語言）");
        Add(support, inputLanguage);
        applyLanguage = AddButton(support, "套用輸入法語言", ApplyLanguageAsync);
        inputLanguage.SelectedIndexChanged += (_, _) => RefreshLanguageControls();
        AddButton(support, "檢查程式更新", CheckProgramAsync);
        AddButton(support, "查看" + OperationLog.Title, () =>
        {
            CopyDialog.Show(this, OperationLog.Title, log.Report(defaultIme.Checked), report: true);
            return Task.FromResult("");
        });
        AddButton(support, "另存問題回報", ExportReportAsync);
        AddButton(support, "移除點心粵語點字輸入法", () =>
        {
            if (Confirm("要移除點心粵語點字輸入法嗎？"))
            {
                WindowsSetup.LaunchUninstaller();
                BeginInvoke(Close);
            }
            return Task.FromResult("");
        });
        RefreshShortcut();
        RefreshLanguageChoices();
        SetDataUpdateButtons();
        SetAddonButtons(false);
        if (!setupReady)
        {
            tabs.SelectedIndex = 2;
            string latest = log.Read().Split(Environment.NewLine).Last();
            SetStatus(setupProblem ? latest : "點心設定尚未完成。可以按「繼續設定點心」。");
        }

        nvdaMonitor.Tick += (_, _) => { if (!busy) SetAddonButtons(CanOfferAddon()); };
        FormClosed += (_, _) => nvdaMonitor.Dispose();
        Shown += async (_, _) =>
        {
            var dataCheck = Workspace.CheckDataIndexAsync();
            bool offer = CanOfferAddon();
            SetAddonButtons(offer);
            if (offer && Confirm("偵測到 NVDA 正在執行。要安裝點心 NVDA 附加組件嗎？"))
                await RunOperationAsync(InstallAddonAsync);
            if (IsDisposed || Disposing) return;
            nvdaMonitor.Start();
            try { await RefreshDefaultAsync(); }
            catch (Exception error) { log.Record(ErrorText("查看 Windows 預設輸入法", error)); }
            await CheckForDataUpdatesAsync(dataCheck);
        };
    }

    TableLayoutPanel AddPage(string name)
    {
        var tab = new TabPage(name);
        var page = new TableLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true, ColumnCount = 1, Padding = new Padding(14), TabStop = false };
        page.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        tab.Controls.Add(page);
        tabs.TabPages.Add(tab);
        return page;
    }

    static void Add(TableLayoutPanel page, Control control)
    {
        int row = page.RowCount++;
        page.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        page.Controls.Add(control, 0, row);
    }

    static void AddLabel(TableLayoutPanel page, string text) => Add(page,
        new Label { AutoSize = true, Dock = DockStyle.Fill, Text = text, TabStop = false });

    Button AddButton(TableLayoutPanel page, string title, Func<Task<string>> action)
    {
        var button = new Button { Text = title, AutoSize = true, MinimumSize = new Size(285, 31) };
        button.Click += async (_, _) => await RunOperationAsync(action);
        Add(page, button);
        return button;
    }

    void AddSetting(TableLayoutPanel page, string text, Func<bool> read, Action<bool> write,
        string? description = null, Func<Task>? save = null)
    {
        var option = new CheckBox { AutoSize = true, Text = text, AccessibleDescription = description, Checked = read() };
        option.CheckedChanged += async (_, _) =>
        {
            if (refreshingControls) return;
            await RunOperationAsync(async () =>
            {
                bool previous = read();
                write(option.Checked);
                try
                {
                    if (save is null) workspace.Save();
                    else await save();
                }
                catch
                {
                    write(previous);
                    RefreshControls(() => option.Checked = previous);
                    throw;
                }
                return $"{text}：{(option.Checked ? "已勾選" : "未勾選")}。";
            });
        };
        Add(page, option);
    }

    async Task SaveShiftAsync()
    {
        if (setupReady) await workspace.DeployAsync(reporting: true, settingsOnly: true);
        else workspace.Save();
    }

    void RefreshControls(Action action)
    {
        refreshingControls = true;
        try { action(); }
        finally { refreshingControls = false; }
    }

    void RefreshShortcut()
    {
        shortcut.Text = $"{LookupLabel}：{(workspace.Settings.LookupGesture is { Length: > 0 } value ? value : "未設定")}。";
        recordShortcut.AccessibleDescription = shortcut.Text + LookupDescription;
    }

    string SaveShortcut(string value)
    {
        string previous = workspace.Settings.LookupGesture;
        workspace.Settings.LookupGesture = value;
        try { workspace.Save(); }
        catch
        {
            workspace.Settings.LookupGesture = previous;
            throw;
        }
        RefreshShortcut();
        return value.Length > 0 ? $"查配詞快捷鍵已設為 {value}。" : "已清除查配詞快捷鍵。";
    }

    InputLanguageChoice SavedLanguage() => WindowsSetup.LanguageChoice(workspace.Settings.PendingInputLanguageTag ?? workspace.Settings.InputLanguageTag ?? "zh-HK")
        ?? WindowsSetup.LanguageChoice("zh-HK")!;

    void RefreshLanguageChoices()
    {
        var current = SavedLanguage();
        var verified = workspace.Settings.VerifiedInputLanguages
            .Select(WindowsSetup.LanguageChoice).OfType<InputLanguageChoice>()
            .Where(language => WindowsSetup.IsOurProfile(language.Id));
        var choices = new[] { current }.Concat(verified).DistinctBy(language => language.Id).ToArray();
        inputLanguage.Items.Clear();
        inputLanguage.Items.AddRange(choices);
        inputLanguage.SelectedIndex = 0;
        RefreshLanguageControls();
    }

    void RefreshLanguageControls()
    {
        continueSetup.Visible = !setupReady;
        inputLanguage.Enabled = setupReady && inputLanguage.Items.Count > 1;
        applyLanguage.Visible = setupReady && inputLanguage.SelectedItem is InputLanguageChoice choice &&
            !WindowsSetup.SameLanguage(choice.Tag, SavedLanguage().Tag);
    }

    async Task<string> ApplyLanguageAsync()
    {
        var choice = (InputLanguageChoice)inputLanguage.SelectedItem!;
        string result = await workspace.ApplyInputLanguageAsync(choice, defaultIme.Checked);
        RefreshLanguageChoices();
        await RefreshDefaultAsync();
        return result;
    }

    async Task<string> SetupAsync(bool repair)
    {
        var language = SavedLanguage();
        setupReady = false;
        defaultIme.Enabled = false;
        RefreshLanguageControls();
        string result = await SetupCoordinator.RunAsync(workspace, language, repair, SetStatus, this);
        setupReady = true;
        RefreshLanguageChoices();
        await RefreshDefaultAsync();
        return result;
    }

    async Task RefreshDefaultAsync()
    {
        string? current = await WindowsSetup.GetDefaultTipAsync();
        if (IsDisposed || Disposing) return;
        RefreshControls(() => defaultIme.Checked = current is not null && WindowsSetup.IsDimsumProfileTip(current));
        defaultIme.Enabled = setupReady;
    }

    async Task<string> SetDefaultAsync()
    {
        bool requested = defaultIme.Checked;
        var settings = workspace.Settings;
        string? saved = settings.PreviousDefaultInputTip;
        try
        {
            string? active = await WindowsSetup.GetDefaultTipAsync();
            if (requested)
            {
                if (active is null || !WindowsSetup.IsDimsumProfileTip(active))
                    settings.PreviousDefaultInputTip = active;
                await WindowsSetup.SetDimsumDefaultAsync(SavedLanguage());
            }
            else
            {
                await WindowsSetup.RestoreDefaultInputAsync(saved);
                settings.PreviousDefaultInputTip = null;
            }
            await RefreshDefaultAsync();
            if (defaultIme.Checked != requested) throw new IOException("Windows 未能確認預設輸入法。");
            workspace.Save();
            return requested ? "已將點心設為預設輸入法。" : "已解除點心嘅預設輸入法設定。";
        }
        catch
        {
            settings.PreviousDefaultInputTip = saved;
            try { await RefreshDefaultAsync(); }
            catch (Exception) { RefreshControls(() => defaultIme.Checked = !requested); }
            throw;
        }
    }

    async Task<string> ImportAsync()
    {
        if (!setupReady) throw new IOException("請先喺「修復與支援」完成點心設定。");
        using var dialog = new OpenFileDialog
        {
            Title = "選擇舊點寫易自訂詞庫",
            FileName = "cb.dic",
            Filter = "詞庫 (*.dic)|*.dic|所有檔案 (*.*)|*.*",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.CommonDocuments)
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return "";
        await workspace.DeployAsync(importFile: dialog.FileName);
        return "已合併及處理舊點寫易詞庫。";
    }

    async Task<string> CheckProgramAsync()
    {
        var found = await Workspace.NewReleaseAsync();
        if (found is not { } release) return "目前已經係最新 Windows 版本。";
        if (Confirm($"有 Windows 新版 {release.Version.ToString(3)}。開啟官方下載頁？"))
            Program.Open(release.Page);
        return $"有 Windows 版 {release.Version.ToString(3)}。";
    }

    bool CanOfferAddon()
    {
        try { return NvdaAddon.ShouldOffer(workspace.DataDirectory); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
    }

    void SetAddonButtons(bool available) => inputAddon.Visible = reviewAddon.Visible = available;

    Task<string> InstallAddonAsync()
    {
        if (!CanOfferAddon())
        {
            SetAddonButtons(false);
            return Task.FromResult("");
        }
        NvdaAddon.Install();
        return Task.FromResult("已開啟 NVDA 附加組件安裝檔。");
    }

    bool HasRimeUpdate => dataUpdates.Dictionary is DataUpdate.Available or DataUpdate.Downloaded;
    bool HasPronunciationUpdate => dataUpdates.Pronunciations == DataUpdate.Available;

    void SetDataUpdateButtons()
    {
        rimeUpdate.Visible = HasRimeUpdate;
        pronunciationUpdate.Visible = HasPronunciationUpdate;
    }

    async Task RefreshDataUpdatesAsync(Task<Dictionary<string, string>> dataCheck)
    {
        var index = await dataCheck;
        if (IsDisposed || Disposing) return;
        dataUpdates = workspace.DataUpdates(index);
        SetDataUpdateButtons();
    }

    async Task CheckForDataUpdatesAsync(Task<Dictionary<string, string>> dataCheck)
    {
        try
        {
            await RefreshDataUpdatesAsync(dataCheck);
            if (IsDisposed || Disposing) return;
            if (busy)
            {
                var retry = new System.Windows.Forms.Timer { Interval = 750 };
                retry.Tick += async (_, _) =>
                {
                    if (busy && !IsDisposed) return;
                    retry.Dispose();
                    if (!IsDisposed) await CheckForDataUpdatesAsync(dataCheck);
                };
                retry.Start();
                return;
            }
            if (HasRimeUpdate || HasPronunciationUpdate)
            {
                string updates = HasRimeUpdate && HasPronunciationUpdate ? "輸入法字庫同報讀配詞都有更新。" :
                    HasRimeUpdate ? "輸入法字庫有更新。" : "報讀配詞有更新。";
                if (Confirm(updates + "而家更新？"))
                    await RunOperationAsync(() => UpdateDataAsync(HasRimeUpdate, HasPronunciationUpdate));
                else SetStatus(updates);
            }
            else
            {
                string rime = dataUpdates.Dictionary == DataUpdate.Current ? "輸入法字庫已經係最新版本。" : "暫時未能檢查 輸入法字庫更新。";
                string review = dataUpdates.Pronunciations == DataUpdate.Current ? "報讀配詞已經係最新版本。" : "暫時未能檢查報讀配詞更新。";
                if (setupReady) SetStatus(rime + Environment.NewLine + review);
            }
        }
        catch (Exception error) { log.Record(ErrorText("檢查詞庫更新", error)); }
    }

    async Task<string> UpdateDataAsync(bool rime, bool pronunciations)
    {
        var results = new List<string>();
        bool errorOccurred = false;
        if (rime)
        {
            try
            {
                if (dataUpdates.Dictionary != DataUpdate.Downloaded)
                {
                    SetStatus("正在下載輸入法字庫。");
                    await workspace.DownloadAsync(false);
                }
                if (setupReady)
                {
                    SetStatus("正在處理輸入法字庫。");
                    await workspace.DeployAsync(applyPending: true);
                    results.Add("已更新及處理 輸入法字庫。");
                }
                else results.Add("輸入法字庫已下載。完成點心設定時會套用。");
            }
            catch (Exception error)
            {
                results.Add(ErrorText("更新 輸入法字庫", error));
                errorOccurred = true;
            }
        }
        if (pronunciations)
        {
            try
            {
                SetStatus("正在下載報讀配詞。");
                await workspace.DownloadAsync(true);
                results.Add("已更新報讀配詞。");
            }
            catch (Exception error)
            {
                results.Add(ErrorText("更新報讀配詞", error));
                errorOccurred = true;
            }
        }
        string result = string.Join(Environment.NewLine, results);
        if (errorOccurred) CopyDialog.Show(this, "更新結果", result);
        try { await RefreshDataUpdatesAsync(Workspace.CheckDataIndexAsync()); }
        catch (Exception error) { log.Record(ErrorText("檢查詞庫更新", error)); }
        return result;
    }

    Task<string> ExportReportAsync()
    {
        using var dialog = new SaveFileDialog { Title = "另存問題回報", FileName = "點心問題回報.txt",
            DefaultExt = "txt", Filter = "文字檔 (*.txt)|*.txt" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return Task.FromResult("");
        log.Export(dialog.FileName, defaultIme.Checked);
        return Task.FromResult("已另存點心問題回報。");
    }

    bool Confirm(string message) => MessageBox.Show(this, message, Text,
        MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) == DialogResult.Yes;

    void SetStatus(string message)
    {
        if (message.Length == 0) return;
        status.Text = message;
        if (ActiveForm == this)
            status.AccessibilityObject.RaiseAutomationNotification(AutomationNotificationKind.Other,
                AutomationNotificationProcessing.CurrentThenMostRecent, message);
    }

    static string ErrorText(string operation, Exception error) => $"{operation}未完成（0x{error.HResult:X8}）。{Environment.NewLine}{error.Message}";

    async Task RunOperationAsync(Func<Task<string>> action)
    {
        if (busy || IsDisposed || Disposing) return;
        busy = true;
        Control? focus = FocusedChild(this);
        tabs.Enabled = false;
        try
        {
            string message = await action();
            log.Record(message);
            SetStatus(message);
        }
        catch (Exception error)
        {
            string message = ErrorText("操作", error);
            log.Record(message);
            SetStatus(message);
            CopyDialog.Show(this, "操作未完成", message);
        }
        finally
        {
            tabs.Enabled = true;
            busy = false;
            if (focus?.CanFocus == true) focus.Focus();
        }
    }

    static Control? FocusedChild(Control container)
    {
        if (container.Focused) return container;
        return container.Controls.Cast<Control>().FirstOrDefault(control => control.ContainsFocus) is { } child
            ? FocusedChild(child) : null;
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (busy && e.CloseReason == CloseReason.UserClosing) e.Cancel = true;
        base.OnFormClosing(e);
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

}

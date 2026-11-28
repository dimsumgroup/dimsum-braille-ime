using System.Windows.Forms.Automation;

namespace Dimsum;

sealed class ManagerForm : Form
{
    const string AutomaticSpeechHint = "有多款讀屏開啟時，優先使用 NVDA，其次係 JAWS，再到 Windows 朗讀程式（Narrator）。關閉自動偵測就可以自行揀報讀方式。";
    const string ReviewDescription = "用 NVDA 原有快捷鍵連按兩下，讀出檢閱游標所在字嘅點心配詞。關閉後會用返 NVDA 原有解釋。";
    const string LookupLabel = "查配詞快捷鍵（可留空）";
    const string LookupDescription = "非 NVDA 使用者可以設定呢個快捷鍵去聆聽配詞解釋報讀。先複製要查嘅讀屏報讀，再按點心快捷鍵。";
    const string JawsLookupDescription = "按 JAWS 鍵加空格，再按 H 開啟語音紀錄。複製要查嘅一句，再按點心查配詞快捷鍵。";
    const string NarratorLookupDescription = "按朗讀程式鍵加 Ctrl 加 X 複製最近一句，再按點心查配詞快捷鍵。";
    readonly Workspace workspace;
    readonly OperationLog log;
    readonly TabControl tabs = new() { Dock = DockStyle.Fill };
    readonly Label status = new() { AutoSize = true, Dock = DockStyle.Fill, AccessibleName = "操作結果", Padding = new Padding(10, 5, 10, 8) };
    readonly Label shortcut = new() { AutoSize = true, TabStop = false };
    readonly CheckBox defaultIme = new() { AutoSize = true, Text = "將點心設為目前預設輸入法", Enabled = false };
    static readonly (CandidateOutput Mode, string Name)[] CandidateModes =
    [
        (CandidateOutput.Off, "關閉"),
        (CandidateOutput.Nvda, "NVDA"),
        (CandidateOutput.Jaws, "JAWS"),
        (CandidateOutput.Narrator, "Windows 朗讀程式（Narrator）"),
        (CandidateOutput.WindowsVoice, "Windows 內置語音合成器")
    ];
    readonly CheckBox automaticSpeech = new() { AutoSize = true, Text = "自動偵測讀屏軟件" };
    readonly ComboBox speechOutput = new() { Width = 320, DropDownStyle = ComboBoxStyle.DropDownList, AccessibleName = "候選報讀方式" };
    readonly ComboBox speechVoice = new() { Width = 320, DropDownStyle = ComboBoxStyle.DropDownList, AccessibleName = "Windows 發音聲線" };
    readonly Label voiceLabel = new() { AutoSize = true, Text = "Windows 發音聲線" };
    readonly Label speechHint = new() { AutoSize = true, MaximumSize = new Size(450, 0) };
    Windows.Media.SpeechSynthesis.VoiceInformation[] speechVoices = [];
    readonly ComboBox inputLanguage = new() { Width = 310, DropDownStyle = ComboBoxStyle.DropDownList, AccessibleName = "點心輸入法語言" };
    readonly System.Windows.Forms.Timer nvdaMonitor = new() { Interval = 3000 };
    readonly Button rimeUpdate, pronunciationUpdate, inputAddon, reviewAddon, continueSetup, applyLanguage, recordShortcut, updateOverview;
    (DataUpdate Dictionary, DataUpdate Pronunciations) dataUpdates;
    (Version Version, string Page)? programUpdate;
    bool busy, refreshingControls, setupReady, showUpdatesWhenIdle;

    public ManagerForm(Workspace workspace, bool setupProblem, bool nvdaPromptAlreadyShown)
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
        AddSpeechSettings(input);
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
        inputAddon = AddButton(input, "安裝或更新 NVDA 附加組件", InstallAddonAsync);

        var review = AddPage("配詞解釋報讀設定");
        AddSetting(review, "在 NVDA 開啟點心配詞解釋", () => workspace.Settings.UseDimsumReviewDescription,
            value => workspace.Settings.UseDimsumReviewDescription = value, ReviewDescription);
        AddLabel(review, ReviewDescription);
        AddSetting(review, "在 JAWS 開啟點心配詞解釋", () => workspace.Settings.UseJawsSpeechLookup,
            value => workspace.Settings.UseJawsSpeechLookup = value, JawsLookupDescription,
            save: () => SaveExternalLookupAsync(workspace.Settings.UseJawsSpeechLookup && !workspace.Settings.UseNarratorSpeechLookup));
        AddSetting(review, "在 Windows 朗讀程式（Narrator）開啟點心配詞解釋", () => workspace.Settings.UseNarratorSpeechLookup,
            value => workspace.Settings.UseNarratorSpeechLookup = value, NarratorLookupDescription,
            save: () => SaveExternalLookupAsync(workspace.Settings.UseNarratorSpeechLookup && !workspace.Settings.UseJawsSpeechLookup));
        Add(review, shortcut);
        AddLabel(review, LookupDescription);
        recordShortcut = AddButton(review, "設定查配詞快捷鍵", () =>
        {
            using var capture = new ShortcutCapture();
            return Task.FromResult(capture.ShowDialog(this) == DialogResult.OK ? SaveShortcut(capture.Shortcut) : "");
        });
        AddButton(review, "清除查配詞快捷鍵", () => Task.FromResult(SaveShortcut("")));
        AddButton(review, "另存配詞表供 NVDA 獨立使用", () => Task.FromResult(ExportPronunciations()));
        pronunciationUpdate = AddButton(review, "更新報讀配詞表", () => UpdateDataAsync(rime: false, pronunciations: true));
        reviewAddon = AddButton(review, "安裝或更新 NVDA 附加組件", InstallAddonAsync);

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
        var manualAddon = AddButton(support, "安裝 NVDA 附加組件", () =>
        {
            NvdaAddon.InstallManually();
            return Task.FromResult("已開啟 NVDA 附加組件安裝檔。");
        });
        manualAddon.AccessibleDescription = "請先開啟 NVDA。可以喺呢度手動開啟內附嘅附加組件安裝檔。";
        continueSetup = AddButton(support, "繼續設定點心", () => SetupAsync(repair: false));
        const string repairHint = "如果打唔到字，或者輸入法出現錯誤，請按呢個按鈕。";
        var repair = AddButton(support, "輸入法設定修復", () => SetupAsync(repair: true));
        repair.AccessibleDescription = repairHint;
        AddLabel(support, repairHint);
        AddLabel(support, "點心輸入法語言（只顯示呢部電腦已驗證嘅語言）");
        Add(support, inputLanguage);
        applyLanguage = AddButton(support, "套用輸入法語言", ApplyLanguageAsync);
        inputLanguage.SelectedIndexChanged += (_, _) => RefreshLanguageControls();
        updateOverview = AddButton(support, "查看可用更新", ShowAvailableUpdatesAsync);
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
        SetUpdateButtons();
        SetAddonButtons(false);
        if (!setupReady)
        {
            tabs.SelectedIndex = 2;
            string latest = log.Read().Split(Environment.NewLine).Last();
            SetStatus(setupProblem ? latest : "點心設定尚未完成。可以按「繼續設定點心」。");
        }

        nvdaMonitor.Tick += (_, _) =>
        {
            if (busy) return;
            SetAddonButtons(CanOfferAddon());
            RefreshSpeechSettings();
        };
        FormClosed += (_, _) => nvdaMonitor.Dispose();
        Shown += async (_, _) =>
        {
            var dataCheck = Workspace.CheckDataIndexAsync();
            using var updateTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var programCheck = Workspace.NewReleaseAsync(updateTimeout.Token);
            bool offer = CanOfferAddon();
            SetAddonButtons(offer);
            if (offer && !nvdaPromptAlreadyShown && Confirm($"偵測到 NVDA 正在執行。要安裝或更新點心 NVDA 附加組件至 {NvdaAddon.BundledVersion.ToString(3)} 嗎？"))
                await RunOperationAsync(InstallAddonAsync);
            if (IsDisposed || Disposing) return;
            nvdaMonitor.Start();
            try { await RefreshDefaultAsync(); }
            catch (Exception error) { log.Record(ErrorText("查看 Windows 預設輸入法", error)); }
            await CheckForUpdatesOnOpenAsync(dataCheck, programCheck);
        };
    }

    void AddSpeechSettings(TableLayoutPanel page)
    {
        Add(page, automaticSpeech);
        AddLabel(page, "候選報讀方式");
        Add(page, speechOutput);
        Add(page, voiceLabel);
        Add(page, speechVoice);
        Add(page, speechHint);
        speechOutput.Items.AddRange(CandidateModes.Select(mode => mode.Name).ToArray());
        RefreshSpeechSettings();

        automaticSpeech.CheckedChanged += async (_, _) =>
        {
            if (!refreshingControls)
                await RunOperationAsync(() => SaveSpeechSettings(automaticSpeech.Checked ? null :
                    CandidateModes[speechOutput.SelectedIndex].Mode, workspace.Settings.CandidateVoiceId));
        };
        speechOutput.SelectedIndexChanged += async (_, _) =>
        {
            if (!refreshingControls && speechOutput.SelectedIndex >= 0)
                await RunOperationAsync(() => SaveSpeechSettings(CandidateModes[speechOutput.SelectedIndex].Mode,
                    workspace.Settings.CandidateVoiceId));
        };
        speechVoice.SelectedIndexChanged += async (_, _) =>
        {
            if (!refreshingControls && speechVoice.SelectedIndex >= 0)
                await RunOperationAsync(() => SaveSpeechSettings(CandidateOutput.WindowsVoice,
                    speechVoices[speechVoice.SelectedIndex].Id));
        };
    }

    void RefreshSpeechSettings()
    {
        var readers = ScreenReaders.Detect();
        var mode = readers.Select(workspace.Settings.CandidateSpeechOutput);
        RefreshControls(() =>
        {
            automaticSpeech.Checked = workspace.Settings.CandidateSpeechOutput is null;
            speechOutput.Enabled = !automaticSpeech.Checked;
            speechOutput.SelectedIndex = Array.FindIndex(CandidateModes, item => item.Mode == mode);
            automaticSpeech.AccessibleDescription = automaticSpeech.Checked
                ? $"目前報讀方式係{speechOutput.Text}。{AutomaticSpeechHint}" : AutomaticSpeechHint;
            voiceLabel.Visible = speechVoice.Visible = mode == CandidateOutput.WindowsVoice;
            if (mode == CandidateOutput.WindowsVoice)
            {
                var installed = WindowsVoiceSpeech.Voices();
                if (!installed.Select(voice => voice.Id).SequenceEqual(speechVoices.Select(voice => voice.Id)))
                {
                    speechVoices = installed;
                    speechVoice.Items.Clear();
                    speechVoice.Items.AddRange(speechVoices.Select(voice => $"{voice.DisplayName}（{voice.Language}）").ToArray());
                }
                string? voiceId = workspace.Settings.CandidateVoiceId;
                speechVoice.SelectedIndex = Array.FindIndex(speechVoices, voice =>
                    voiceId is null ? WindowsVoiceSpeech.IsCantonese(voice.Language) : voice.Id == voiceId);
                speechVoice.Enabled = speechVoices.Length > 0;
            }

            speechHint.Text = mode switch
            {
                CandidateOutput.Off when automaticSpeech.Checked => "暫時未偵測到讀屏軟件，候選報讀已關閉。",
                CandidateOutput.Off => "已關閉點心候選報讀。",
                CandidateOutput.Nvda when !readers.Nvda => "未偵測到 NVDA 正在執行。",
                CandidateOutput.Nvda => "由點心 NVDA 附加組件報讀候選。",
                CandidateOutput.Jaws when !readers.Jaws => "未偵測到 JAWS。已手動選用 JAWS，請確認 JAWS 已開啟。",
                CandidateOutput.Jaws => "由 JAWS 報讀候選。如果未能聽到候選，可以改用 Windows 內置語音合成器。",
                CandidateOutput.Narrator when !readers.CanNotifyNarrator => "請先關閉 NVDA 同 JAWS，先可以由 Windows 朗讀程式報讀候選。",
                CandidateOutput.Narrator when !readers.Narrator => "未偵測到 Windows 朗讀程式。已手動選用，請確認朗讀程式已開啟。",
                CandidateOutput.Narrator => "由 Windows 朗讀程式報讀候選。",
                CandidateOutput.WindowsVoice when speechVoice.SelectedIndex < 0 => "請選擇已安裝嘅聲線，或者喺 Windows 語音設定安裝廣東話聲線。",
                _ => ""
            };
            speechOutput.AccessibleDescription = speechHint.Text;
            speechVoice.AccessibleDescription = speechHint.Text;
        });
    }

    Task<string> SaveSpeechSettings(CandidateOutput? mode, string? voiceId)
    {
        var previous = (workspace.Settings.CandidateSpeechOutput, workspace.Settings.CandidateVoiceId);
        workspace.Settings.CandidateSpeechOutput = mode;
        workspace.Settings.CandidateVoiceId = voiceId;
        try { workspace.Save(); }
        catch
        {
            (workspace.Settings.CandidateSpeechOutput, workspace.Settings.CandidateVoiceId) = previous;
            throw;
        }
        finally { RefreshSpeechSettings(); }
        return Task.FromResult("已更新候選報讀方式。");
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

    Task SaveExternalLookupAsync(bool enablingFirst)
    {
        if (enablingFirst) SpeechLookup.CheckAvailability(workspace.Settings.LookupGesture);
        workspace.Save();
        return Task.CompletedTask;
    }

    string SaveShortcut(string value)
    {
        string previous = workspace.Settings.LookupGesture;
        if (value != previous && (workspace.Settings.UseJawsSpeechLookup || workspace.Settings.UseNarratorSpeechLookup))
            SpeechLookup.CheckAvailability(value);
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
        programUpdate = await Workspace.NewReleaseAsync();
        SetUpdateButtons();
        if (programUpdate is not { } release) return "目前已經係最新 Windows 版本。";
        return Confirm($"有 Windows 新版 {release.Version.ToString(3)}。開啟官方下載頁？")
            ? OpenProgramDownload(release)
            : $"有 Windows 新版 {release.Version.ToString(3)}。";
    }

    bool CanOfferAddon()
    {
        try { return NvdaAddon.ShouldOffer(workspace.DataDirectory); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
    }

    void SetAddonButtons(bool available) => inputAddon.Visible = reviewAddon.Visible = available;

    Task<string> InstallAddonAsync()
    {
        if (!NvdaAddon.InstallIfOffered(workspace.DataDirectory))
        {
            SetAddonButtons(false);
            return Task.FromResult("");
        }
        return Task.FromResult("已開啟 NVDA 附加組件安裝檔。");
    }

    bool HasRimeUpdate => dataUpdates.Dictionary is DataUpdate.Available or DataUpdate.Downloaded;
    bool HasPronunciationUpdate => dataUpdates.Pronunciations == DataUpdate.Available;
    bool HasUpdates => programUpdate is not null || HasRimeUpdate || HasPronunciationUpdate;

    void SetUpdateButtons()
    {
        rimeUpdate.Visible = HasRimeUpdate;
        pronunciationUpdate.Visible = HasPronunciationUpdate;
        updateOverview.Visible = HasUpdates;
    }

    async Task RefreshDataUpdatesAsync(Task<Dictionary<string, string>> dataCheck)
    {
        var index = await dataCheck;
        if (IsDisposed || Disposing) return;
        dataUpdates = workspace.DataUpdates(index);
        SetUpdateButtons();
    }

    async Task CheckForUpdatesOnOpenAsync(Task<Dictionary<string, string>> dataCheck,
        Task<(Version Version, string Page)?> programCheck)
    {
        bool programChecked = false;
        try
        {
            programUpdate = await programCheck;
            programChecked = true;
        }
        catch (Exception error) { log.Record(ErrorText("檢查程式更新", error)); }

        try { await RefreshDataUpdatesAsync(dataCheck); }
        catch (Exception error) { log.Record(ErrorText("檢查詞庫更新", error)); }
        if (IsDisposed || Disposing) return;
        SetUpdateButtons();
        if (HasUpdates)
        {
            await OfferUpdatesWhenIdleAsync();
        }
        else if (setupReady && !busy)
        {
            string program = programChecked ? "目前已經係最新 Windows 版本。" : "暫時未能檢查程式更新。";
            string rime = dataUpdates.Dictionary == DataUpdate.Current ? "輸入法字庫已經係最新版本。" : "暫時未能檢查輸入法字庫更新。";
            string review = dataUpdates.Pronunciations == DataUpdate.Current ? "報讀配詞已經係最新版本。" : "暫時未能檢查報讀配詞更新。";
            SetStatus(string.Join(Environment.NewLine, program, rime, review));
        }
    }

    Task OfferUpdatesWhenIdleAsync()
    {
        if (IsDisposed || Disposing || !HasUpdates) return Task.CompletedTask;
        if (!busy) return RunOperationAsync(ShowAvailableUpdatesAsync);
        showUpdatesWhenIdle = true;
        return Task.CompletedTask;
    }

    async Task<string> ShowAvailableUpdatesAsync()
    {
        var release = programUpdate;
        var options = new List<(string Label, Func<Task<string>> Action)>();
        if (release is { } availableProgram)
            options.Add(($"開啟 Windows {availableProgram.Version.ToString(3)} 下載頁",
                () => Task.FromResult(OpenProgramDownload(availableProgram))));
        if (HasRimeUpdate)
            options.Add(("更新輸入法字庫", () => UpdateDataAsync(rime: true, pronunciations: false)));
        if (HasPronunciationUpdate)
            options.Add(("更新報讀配詞表", () => UpdateDataAsync(rime: false, pronunciations: true)));
        if (options.Count == 0) return "";

        using var dialog = new Form
        {
            Text = "有更新可用", StartPosition = FormStartPosition.CenterParent, AutoScaleMode = AutoScaleMode.Font,
            FormBorderStyle = FormBorderStyle.FixedDialog, ShowInTaskbar = false, MinimizeBox = false, MaximizeBox = false,
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink
        };
        var actions = new FlowLayoutPanel
        {
            AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(12)
        };
        Func<Task<string>>? selected = null;
        foreach (var option in options)
        {
            var button = new Button { Text = option.Label, AutoSize = true, MinimumSize = new Size(295, 31) };
            button.Click += (_, _) =>
            {
                selected = option.Action;
                dialog.DialogResult = DialogResult.OK;
            };
            actions.Controls.Add(button);
        }
        var later = new Button { Text = "稍後", AutoSize = true, MinimumSize = new Size(295, 31), DialogResult = DialogResult.Cancel };
        actions.Controls.Add(later);
        dialog.Controls.Add(actions);
        dialog.CancelButton = later;
        dialog.ActiveControl = actions.Controls[0];
        dialog.ShowDialog(this);
        return selected is null ? "有更新可用。可喺「修復與支援」查看。" : await selected();
    }

    static string OpenProgramDownload((Version Version, string Page) release)
    {
        Program.Open(release.Page);
        return $"已開啟 Windows {release.Version.ToString(3)} 下載頁。";
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
                    results.Add("已更新及處理輸入法字庫。");
                }
                else results.Add("輸入法字庫已下載。完成點心設定時會套用。");
            }
            catch (Exception error)
            {
                results.Add(ErrorText("更新輸入法字庫", error));
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
            if (showUpdatesWhenIdle && !IsDisposed && !Disposing)
            {
                showUpdatesWhenIdle = false;
                BeginInvoke((Action)(async () => await OfferUpdatesWhenIdleAsync()));
            }
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

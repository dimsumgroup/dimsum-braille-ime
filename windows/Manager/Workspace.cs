using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using YamlDotNet.RepresentationModel;

namespace Dimsum;

enum DataUpdate { CannotCheck, Current, Available, Downloaded }

sealed class Preferences
{
    public bool? SingleCandidateSound { get; set; }
    public bool ShiftSwitch { get; set; } = true;
    public string? SetupVersion { get; set; }
    public bool SetupPending { get; set; }
    public string? PendingInputLanguageTag { get; set; }
    public string? InputLanguageTag { get; set; }
    public bool Initialized { get; set; }
    public bool OmitCandidateNumbers { get; set; }
    // Retain a compatible value for a Dimsum NVDA add-on already installed.
    public string ReportingMode => OmitCandidateNumbers ? "compact" : "standard";
    public bool UseDimsumReviewDescription { get; set; }
    public string? PreviousDefaultInputTip { get; set; }
    public List<string> VerifiedInputLanguages { get; set; } = [];
    public bool PronounceCandidates { get; set; } = true;
    public string LookupGesture { get; set; } = "";
    public string? AppliedDictionary { get; set; }

    internal static Preferences Read(byte[] content)
    {
        using var json = JsonDocument.Parse(content);
        var settings = json.RootElement.Deserialize<Preferences>() ?? new();
        if (json.RootElement.ValueKind == JsonValueKind.Object &&
            !json.RootElement.TryGetProperty(nameof(OmitCandidateNumbers), out _) &&
            json.RootElement.TryGetProperty(nameof(ReportingMode), out var mode) && mode.ValueKind == JsonValueKind.String)
            settings.OmitCandidateNumbers = mode.ValueEquals("compact");
        return settings;
    }

}

sealed class Workspace
{
    const string Repository = "https://github.com/dimsumgroup/dimsum-braille-ime";
    const string Raw = "https://raw.githubusercontent.com/dimsumgroup/dimsum-braille-ime";
    const string Api = "https://api.github.com/repos/dimsumgroup/dimsum-braille-ime";
    const string Dictionary = "dimsum_cb.dict.yaml";
    const string UserDictionary = "dimsum_cb.user.dict.yaml";
    const string DictionaryRepositoryPath = "Dimsum dic and schema/" + Dictionary;
    const string PronunciationRepositoryFolder = "NVDA dic/";
    static readonly string Reporter = Path.Combine("lua", "dimsum_windows_reporter.lua");
    static readonly string[] Schemas = ["dimsum_cb_std", "dimsum_cb", "dimsum_full"];
    static readonly Encoding Utf8 = new UTF8Encoding(false, true);
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    static Workspace() => Http.DefaultRequestHeaders.UserAgent.ParseAdd("DimsumManager/1.3");

    readonly string data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DimsumInput");
    readonly string assets = Path.Combine(AppContext.BaseDirectory, "assets");
    public string Rime { get; }
    public string DataDirectory => data;
    public bool SoundEnabled => Settings.SingleCandidateSound ?? true;
    public bool SetupComplete => Settings.SetupVersion == AppVersion && !Settings.SetupPending;
    static string AppVersion => typeof(Workspace).Assembly.GetName().Version!.ToString(3);
    string Journal => Path.Combine(data, "deployment");
    string Pending => Path.Combine(data, "pending.dict.yaml");
    string SettingsPath => Path.Combine(data, "settings.json");
    string BundledReporter => Path.Combine(assets, "rime", Reporter);
    public Preferences Settings { get; }
    public bool ReportingNeedsDeployment
    {
        get
        {
            string installed = Path.Combine(Rime, Reporter);
            return !File.Exists(installed) || HashFile(installed) != HashFile(BundledReporter);
        }
    }

    public Workspace()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Rime\Weasel");
        Rime = Path.GetFullPath(key?.GetValue("RimeUserDir") is string path && Path.IsPathFullyQualified(path) ?
            path : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Rime"));
        Settings = File.Exists(SettingsPath) ?
            Preferences.Read(File.ReadAllBytes(SettingsPath)) : new();
    }

    public void Save()
    {
        string reporting = Path.Combine(data, "candidate-reporting-enabled");
        bool reportCandidates = Settings.Initialized;
        (string Path, bool Enabled)[] flags =
        [
            (Path.Combine(data, SingletonSound.Service), Settings.Initialized),
            (Path.Combine(data, SingletonSound.Flag), Settings.Initialized && SoundEnabled),
            (reporting, reportCandidates)
        ];
        var changed = new List<(string Path, bool Enabled)>();
        try
        {
            foreach (var flag in flags)
            {
                bool previous = File.Exists(flag.Path);
                if (previous == flag.Enabled) continue;
                SetFlag(flag.Path, flag.Enabled);
                changed.Add((flag.Path, previous));
            }
            WriteAtomic(SettingsPath, JsonSerializer.SerializeToUtf8Bytes(Settings));
        }
        catch (Exception error)
        {
            try
            {
                foreach (var flag in Enumerable.Reverse(changed)) SetFlag(flag.Path, flag.Enabled);
            }
            catch (Exception rollback)
            {
                throw new IOException($"{error.Message}{Environment.NewLine}未能復原先前嘅報讀或提示音設定。{rollback.Message}", error);
            }
            throw;
        }
        if (!reportCandidates)
        {
            foreach (string suffix in new[] { "1", "2", "3", "1.tmp", "2.tmp", "3.tmp" })
            {
                try { File.Delete(Path.Combine(data, "candidate-report-" + suffix)); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            }
        }
    }

    static void SetFlag(string path, bool enabled)
    {
        if (enabled) WriteAtomic(path, []);
        else File.Delete(path);
    }

    public string Status()
    {
        string stage = File.Exists(Pending) ? "有字庫等待處理。" : "暫無待處理字庫。";
        string dictionaries = Settings.AppliedDictionary is { } applied ? $"已處理嘅輸入法字庫{Environment.NewLine}{applied}" : "管理器尚未核對已處理嘅輸入法字庫。";
        string version;
        try
        {
            string? pronunciations = ActivePronunciations();
            version = pronunciations is null ? "安裝包內附版本" : Path.GetFileName(pronunciations);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            version = "未能讀取版本";
        }
        return $"{dictionaries}{Environment.NewLine}{stage}{Environment.NewLine}點心配詞資料 {version}";
    }

    public static async Task<Dictionary<string, string>> CheckDataIndexAsync()
    {
        try
        {
            using var json = await Http.GetFromJsonAsync<JsonDocument>(Api + "/git/trees/main?recursive=1");
            return json is null ? [] : ReadDataIndex(json.RootElement);
        }
        catch (Exception error) when (error is HttpRequestException or OperationCanceledException or IOException or JsonException)
        {
            return [];
        }
    }

    internal static Dictionary<string, string> ReadDataIndex(JsonElement root)
    {
        var index = new Dictionary<string, string>(StringComparer.Ordinal);
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("tree", out var tree) || tree.ValueKind != JsonValueKind.Array)
            return index;

        foreach (var item in tree.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object ||
                !item.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String || !type.ValueEquals("blob") ||
                !item.TryGetProperty("path", out var path) || path.ValueKind != JsonValueKind.String ||
                !item.TryGetProperty("sha", out var sha) || sha.ValueKind != JsonValueKind.String)
                continue;
            string name = path.GetString()!;
            string id = sha.GetString()!;
            if (Regex.IsMatch(id, @"\A[0-9a-f]{40}\z")) index[name] = id;
        }
        return index;
    }

    public (DataUpdate Dictionary, DataUpdate Pronunciations) DataUpdates(IReadOnlyDictionary<string, string> index)
    {
        DataUpdate dictionary = RimeDictionaryUpdate(index, Path.Combine(Rime, Dictionary),
            Pending, Path.Combine(assets, "rime", Dictionary));
        try
        {
            string path = ActivePronunciations() ?? Path.Combine(assets, "pronunciations");
            return (dictionary, PronunciationUpdate(index, path));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return (dictionary, DataUpdate.CannotCheck);
        }
    }

    internal static DataUpdate RimeDictionaryUpdate(IReadOnlyDictionary<string, string> index,
        string installed, string pending, string bundled)
    {
        if (!index.TryGetValue(DictionaryRepositoryPath, out string? remote)) return DataUpdate.CannotCheck;
        try
        {
            string? current = GitBlobId(installed);
            if (current == remote) return DataUpdate.Current;
            if (GitBlobId(pending) == remote) return DataUpdate.Downloaded;
            if (current is null && GitBlobId(bundled) == remote) return DataUpdate.Current;
            return DataUpdate.Available;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return DataUpdate.CannotCheck;
        }
    }

    internal static DataUpdate PronunciationUpdate(IReadOnlyDictionary<string, string> index, string directory)
    {
        if (NvdaDictionaries.Names.Any(name => !index.ContainsKey(PronunciationRepositoryFolder + name)))
            return DataUpdate.CannotCheck;
        try
        {
            return NvdaDictionaries.Names.All(name =>
                GitBlobId(Path.Combine(directory, name)) == index[PronunciationRepositoryFolder + name])
                ? DataUpdate.Current : DataUpdate.Available;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return DataUpdate.CannotCheck;
        }
    }

    internal static string? GitBlobId(string path)
    {
        FileStream file;
        try { file = File.OpenRead(path); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        using (file)
        using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1))
        {
            // Git tree entries identify blobs by their header and exact bytes.
            hash.AppendData(Encoding.ASCII.GetBytes("blob " + file.Length.ToString(CultureInfo.InvariantCulture) + "\0"));
            Span<byte> buffer = stackalloc byte[8192];
            int read;
            while ((read = file.Read(buffer)) != 0) hash.AppendData(buffer[..read]);
            return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        }
    }

    public static async Task<(Version Version, string Page)?> NewReleaseAsync(CancellationToken cancellationToken = default)
    {
        var current = typeof(Workspace).Assembly.GetName().Version!;
        (Version Version, string Page)? best = null;
        for (int page = 1; page <= 10; page++)
        {
            using var json = await Http.GetFromJsonAsync<JsonDocument>(Api + $"/releases?per_page=30&page={page}", cancellationToken);
            var list = json!.RootElement;
            foreach (var release in list.EnumerateArray())
            {
                if (release.GetProperty("draft").GetBoolean() || release.GetProperty("prerelease").GetBoolean()) continue;
                string? tag = release.GetProperty("tag_name").GetString();
                if (tag is null || !Regex.IsMatch(tag, @"\Av[0-9]+\.[0-9]+\.[0-9]+\z") ||
                    !Version.TryParse(tag[1..], out var version)) continue;
                version = new Version(version.Major, version.Minor, version.Build, 0);
                if (version <= current || (best is { } previous && version <= previous.Version)) continue;
                string name = "Dimsum_Setup_" + tag + ".exe";
                if (release.GetProperty("assets").EnumerateArray().Any(asset =>
                    asset.GetProperty("name").GetString() == name &&
                    asset.GetProperty("state").GetString() == "uploaded" &&
                    asset.GetProperty("browser_download_url").GetString() == $"{Repository}/releases/download/{tag}/{name}"))
                    best = (version, $"{Repository}/releases/tag/{tag}");
            }
            if (list.GetArrayLength() < 30) break;
        }
        return best;
    }

    public async Task DownloadAsync(bool includePronunciations)
    {
        using var head = await Http.GetFromJsonAsync<JsonDocument>(Api + "/commits/main");
        string revision = head!.RootElement.GetProperty("sha").GetString()!;
        if (!Regex.IsMatch(revision, @"\A[0-9a-f]{40}\z")) throw new IOException("GitHub 回傳嘅資料版本無效。");
        string address = $"{Raw}/{revision}";
        if (!includePronunciations)
        {
            byte[] remote = await Http.GetByteArrayAsync(address + "/" + DictionaryRepositoryPath.Replace(" ", "%20"));
            InspectDictionary(remote, "dimsum_cb", primary: true);
            string current = Path.Combine(Rime, Dictionary);
            if (File.Exists(current) && HashFile(current) == Hash(remote))
                File.Delete(Pending);
            else
                WriteAtomic(Pending, remote);
            return;
        }

        string pronunciations = Path.Combine(data, "pronunciations");
        var files = await Task.WhenAll(NvdaDictionaries.Names.Select(name =>
            Http.GetByteArrayAsync(address + "/" + PronunciationRepositoryFolder.Replace(" ", "%20") + name)));
        NvdaDictionaries.Validate(files);
        if (ActivePronunciations() is { } currentBundle && NvdaDictionaries.Names.Zip(files).All(file =>
        {
            string path = Path.Combine(currentBundle, file.First);
            return File.Exists(path) && HashFile(path) == Hash(file.Second);
        })) return;
        string bundleName = revision + "-" + Guid.NewGuid().ToString("N");
        string bundle = Path.Combine(pronunciations, bundleName);
        for (int index = 0; index < files.Length; index++)
            WriteAtomic(Path.Combine(bundle, NvdaDictionaries.Names[index]), files[index]);
        WriteAtomic(Path.Combine(pronunciations, "active.txt"), Utf8.GetBytes(bundleName));
    }

    string? ActivePronunciations()
    {
        string pointer = Path.Combine(data, "pronunciations", "active.txt");
        if (!File.Exists(pointer)) return null;
        string revision = File.ReadAllText(pointer).Trim();
        return Regex.IsMatch(revision, @"\A[0-9a-f]{40}(?:-[0-9a-f]{32})?\z") ?
            Path.Combine(data, "pronunciations", revision) : null;
    }

    public void ExportPronunciations(string destination, bool overwrite)
    {
        string source = ActivePronunciations() ?? Path.Combine(assets, "pronunciations");
        NvdaDictionaries.Export(destination, NvdaDictionaries.ReadBundle(source), overwrite);
    }

    public void ConfigureStartup(bool retireLegacy = false) =>
        Program.ConfigureStartup(Settings.Initialized, retireLegacy);

    public void StartSound()
    {
        if (Settings.Initialized)
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--sound") { UseShellExecute = false })?.Dispose();
    }

    public void MarkSetupPending(InputLanguageChoice language)
    {
        if (Settings.SetupPending && Settings.PendingInputLanguageTag == language.Tag) return;
        Settings.SetupPending = true;
        Settings.PendingInputLanguageTag = language.Tag;
        Save();
    }

    internal bool SetupDeploymentUsable(DateTime? deployedAfterUtc = null)
    {
        if (!Settings.Initialized || Settings.AppliedDictionary is null || File.Exists(Pending) || Directory.Exists(Journal)) return false;
        try
        {
            if (ReportingNeedsDeployment || (deployedAfterUtc is DateTime since &&
                File.GetLastWriteTimeUtc(Path.Combine(Rime, "build", "dimsum_cb.table.bin")) < since)) return false;
            VerifyTable(File.ReadAllBytes(Path.Combine(Rime, Dictionary)), File.ReadAllBytes(Path.Combine(Rime, UserDictionary)));
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
    }

    public async Task<string> CompleteSetupAsync(InputLanguageChoice language, bool makeDefault,
        Action<string>? progress = null, bool forceDeploy = false)
    {
        if (forceDeploy || !SetupDeploymentUsable())
        {
            progress?.Invoke("正在處理點心字庫同設定。");
            await DeployAsync(applyPending: true, reporting: true);
        }
        progress?.Invoke($"正在將點心加入到「{language.Label}」。");
        return await FinishSetupAsync(language, makeDefault);
    }

    internal async Task<string> FinishSetupAsync(InputLanguageChoice language, bool makeDefault)
    {
        string result = await ApplyInputLanguageAsync(language, makeDefault);
        WindowsSetup.StartAvailableEngine();
        await WindowsSetup.HideInstallationEntriesAsync();
        return result;
    }

    public async Task<string> ApplyInputLanguageAsync(InputLanguageChoice language, bool makeDefault)
    {
        var (verified, notice) = await WindowsSetup.EnableOnlyDimsumLanguageAsync(language, makeDefault);
        Settings.VerifiedInputLanguages = Settings.VerifiedInputLanguages.Concat(verified).Append(language.Tag)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        Settings.InputLanguageTag = language.Tag;
        Settings.SetupVersion = AppVersion;
        Settings.SetupPending = false;
        Settings.PendingInputLanguageTag = null;
        Save();
        string applied = LanguageAppliedMessage(language);
        return notice is null ? applied : applied + Environment.NewLine + notice;
    }

    internal static string LanguageAppliedMessage(InputLanguageChoice language) =>
        $"點心已加入到「{language.Label}」。";

    public async Task<(WindowsSetup.EngineOutcome Engine, bool RestartNvda)> RemoveAsync()
    {
        string? deployer = FindDeployer();
        string? engine = Path.GetDirectoryName(deployer);
        if (engine is not null)
        {
            string relative = Path.GetRelativePath(engine, Rime);
            if (!Path.IsPathRooted(relative) && relative != ".." && !relative.StartsWith(@"..\", StringComparison.Ordinal))
                throw new IOException("用家資料位於輸入引擎資料夾內。請先將用家資料移到其他位置，再移除引擎。");
        }
        ArchiveInterruptedDeployment(Journal, Rime, Path.Combine(data, "未完成部署備份"));
        const string filename = "default.custom.yaml";
        string path = Path.Combine(Rime, filename);
        string? original = File.Exists(path) ? File.ReadAllText(path) : null;
        string? remaining = original is null ? null : RemoveDimsumSchemas(original);

        string[] profiles = WindowsSetup.UserProfiles().ToArray();
        var legacy = new LegacyUpgrade(this);
        string? backupId = await legacy.BeginRemovalAsync();
        bool machineCleanup = backupId is not null && MachineLegacyUpgrade.NeedsMachineCleanup(backupId);
        if (await WindowsSetup.GetDefaultTipAsync() is { } currentDefault && WindowsSetup.IsDimsumProfileTip(currentDefault))
            await WindowsSetup.RestoreDefaultInputAsync(Settings.PreviousDefaultInputTip);
        var outcome = await WindowsSetup.RemoveEngineAsync(machineCleanup ? backupId : null);

        if (remaining != original) WriteAtomic(path, Utf8.GetBytes(remaining!));
        WindowsSetup.RemoveUserProfiles(profiles);
        Settings.Initialized = false;
        Settings.SetupVersion = null;
        Settings.PreviousDefaultInputTip = null;
        Settings.SetupPending = false;
        Settings.PendingInputLanguageTag = null;
        Settings.AppliedDictionary = null;
        Save();
        ConfigureStartup(retireLegacy: true);
        SingletonSound.WaitForStop();
        legacy.FinishRemoval(backupId);
        return (outcome, NvdaAddon.Remove(DataDirectory));
    }

    public async Task DeployAsync(bool applyPending = false, string? importFile = null, bool reporting = false,
        bool legacyMigration = false, bool settingsOnly = false)
    {
        if (!legacyMigration && new LegacyUpgrade(this).NeedsUpgrade())
            throw new IOException("請先按「繼續設定點心」完成舊版遷移，再處理點心。");
        string deployer = FindDeployer() ?? throw new IOException("搵唔到輸入引擎處理程式。請先按「繼續設定點心」。");
        if (LegacyUpgrade.ConverterRunning(MachineLegacyUpgrade.KnownFolders()))
            throw new IOException("舊版詞庫更新仍在執行。請完成後再處理。");
        Directory.CreateDirectory(Rime);
        if (legacyMigration)
            ArchiveInterruptedDeployment(Journal, Rime, Path.Combine(data, "未完成部署備份"));
        else
            await RecoverAsync(deployer);
        var changes = new Dictionary<string, byte[]>();
        foreach (string filename in Schemas.Select(schema => schema + ".schema.yaml")
            .Concat(new[] { "default.custom.yaml", "weasel.custom.yaml", Dictionary, UserDictionary }))
        {
            string path = Path.Combine(Rime, filename);
            if (!File.Exists(path) || ((filename is "default.custom.yaml" or "weasel.custom.yaml") &&
                IsBlankYaml(File.ReadAllText(path))))
                changes[filename] = File.ReadAllBytes(Path.Combine(assets, "rime", filename));
        }

        if (applyPending && File.Exists(Pending))
            changes[Dictionary] = File.ReadAllBytes(Pending);

        if (importFile is not null)
            changes[UserDictionary] = ImportLegacy(importFile, File.Exists(Path.Combine(Rime, UserDictionary))
                ? File.ReadAllBytes(Path.Combine(Rime, UserDictionary)) : changes[UserDictionary]);

        reporting |= ReportingNeedsDeployment;
        if (reporting)
        {
            const string defaultPatch = "default.custom.yaml";
            string defaultPath = Path.Combine(Rime, defaultPatch);
            if (!changes.ContainsKey(defaultPatch) && File.Exists(defaultPath))
            {
                string existing = File.ReadAllText(defaultPath);
                string updated = IncludeDimsumSchemas(existing);
                if (updated != existing) changes[defaultPatch] = Utf8.GetBytes(updated);
            }
            foreach (string schema in Schemas)
            {
                string filename = schema + ".custom.yaml";
                string path = Path.Combine(Rime, filename);
                string currentPatch = File.Exists(path) ? File.ReadAllText(path) : "";
                (currentPatch, var overrides) = EnsurePatch(currentPatch);
                var processorKey = new YamlScalarNode("engine/processors/@before 0");
                if (overrides.Children.TryGetValue(processorKey, out var processor))
                {
                    if (processor.ToString() != "lua_processor@*dimsum_windows_reporter")
                        throw new IOException($"已有自訂輸入模組設定（{filename}）。");
                }
                else if (overrides.Children.ContainsKey(new YamlScalarNode("engine")) ||
                         overrides.Children.ContainsKey(new YamlScalarNode("engine/processors")))
                    throw new IOException($"已有自訂輸入模組設定（{filename}）。");
                else
                    currentPatch = InsertPatch(currentPatch, "  engine/processors/@before 0: lua_processor@*dimsum_windows_reporter");
                changes[filename] = Utf8.GetBytes(UpdateShiftPatch(currentPatch, Settings.ShiftSwitch));
            }
            changes[Reporter] = File.ReadAllBytes(BundledReporter);
        }

        foreach (string schema in Schemas)
        {
            string filename = schema + ".custom.yaml";
            string path = Path.Combine(Rime, filename);
            string existing = changes.TryGetValue(filename, out byte[]? staged) ? Utf8.GetString(staged) :
                File.Exists(path) ? File.ReadAllText(path) : "";
            string preview = EnableNativeCandidatePreview(existing);
            if (existing != preview) changes[filename] = Utf8.GetBytes(preview);
        }

        byte[] primary = changes.TryGetValue(Dictionary, out var next) ? next : File.ReadAllBytes(Path.Combine(Rime, Dictionary));
        byte[] custom = changes.TryGetValue(UserDictionary, out var nextCustom) ? nextCustom : File.ReadAllBytes(Path.Combine(Rime, UserDictionary));
        string version = InspectDictionary(primary, "dimsum_cb", primary: true);
        InspectDictionary(custom, "dimsum_cb.user", primary: false);
        string? previousDictionary = Settings.AppliedDictionary;
        bool previousInitialized = Settings.Initialized;
        StartJournal(changes.Keys);
        try
        {
            foreach (var change in changes) WriteAtomic(Path.Combine(Rime, change.Key), change.Value);
            await RunDeployerAsync(deployer);
            VerifyTable(primary, custom);
            if (reporting)
            {
                foreach (string schema in Schemas)
                {
                    string built = Path.Combine(Rime, "build", schema + ".schema.yaml");
                    if (!File.Exists(built) || !File.ReadAllText(built).Contains("lua_processor@*dimsum_windows_reporter", StringComparison.Ordinal))
                        throw new IOException($"未確認報讀設定已處理（{schema}）。請確認方案已加入輸入方案選單。");
                }
            }
            Settings.AppliedDictionary = $"{version} · SHA-256 {Hash(primary)[..12]}";
            Settings.Initialized = true;
            Save();
        }
        catch (Exception error)
        {
            Settings.AppliedDictionary = previousDictionary;
            Settings.Initialized = previousInitialized;
            try { await RecoverAsync(deployer); }
            catch (Exception rollback)
            {
                throw new IOException($"{error.Message}{Environment.NewLine}恢復仍待完成。{rollback.Message}", error);
            }
            throw new IOException($"{error.Message}{Environment.NewLine}已復原處理前嘅來源檔案。", error);
        }
        CloseJournal();
        if (applyPending) File.Delete(Pending);
        if (!settingsOnly)
        {
            ConfigureStartup(retireLegacy: true);
            StartSound();
        }
    }

    static string InsertPatch(string document, string line)
    {
        var patch = Regex.Match(document, @"(?m)^patch:[ \t]*(?:#[^\r\n]*)?(?:\r?\n|$)");
        if (!patch.Success) throw new IOException("自訂設定需要用分行嘅 patch 格式。");
        string newline = patch.Value.EndsWith("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        return document.Insert(patch.Index + patch.Length, (patch.Value.EndsWith('\n') ? "" : newline) + line + newline);
    }

    internal static string EnableNativeCandidatePreview(string text)
    {
        // Stock NVDA reads Weasel via the TSF composition text. Keep the
        // candidates in that text for the Dimsum schemas in every speech mode.
        foreach ((string key, string value) in new[] { ("inline_preedit", "true"), ("preedit_type", "preview_all") })
        {
            (text, var overrides) = EnsurePatch(text);
            string path = "style/" + key;
            if (overrides.Children.TryGetValue(new YamlScalarNode(path), out var flat) && flat is YamlScalarNode scalar)
                text = ReplaceScalar(text, scalar, value);
            else if (overrides.Children.TryGetValue(new YamlScalarNode("style"), out var style) && style is YamlMappingNode nested)
            {
                if (nested.Children.TryGetValue(new YamlScalarNode(key), out var nestedValue) && nestedValue is YamlScalarNode setting)
                    text = ReplaceScalar(text, setting, value);
                else if (nested.Style == YamlDotNet.Core.Events.MappingStyle.Block && nested.Children.Count > 0)
                {
                    int index = checked((int)nested.Children.Keys.First().Start.Index);
                    int line = text.LastIndexOf('\n', index) + 1;
                    string newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
                    text = text.Insert(line, text[line..index] + key + ": " + value + newline);
                }
                else if (nested.Style == YamlDotNet.Core.Events.MappingStyle.Flow)
                {
                    int index = checked((int)nested.Start.Index + 1);
                    text = text.Insert(index, key + ": " + value + (nested.Children.Count == 0 ? "" : ", "));
                }
            }
            else
                text = InsertPatch(text, "  " + path + ": " + value);
        }
        return text;
    }

    static string ReplaceScalar(string text, YamlScalarNode scalar, string value)
    {
        int start = checked((int)scalar.Start.Index);
        int end = checked((int)scalar.End.Index);
        return scalar.Value == value ? text : text[..start] + value + text[end..];
    }

    internal static bool IsBlankYaml(string text) => text.TrimStart('\uFEFF').Split('\n').All(line =>
    {
        string content = line.Trim();
        return content is "" or "---" or "..." || content.StartsWith('#');
    });

    static (string Text, YamlMappingNode Patch) EnsurePatch(string text)
    {
        string newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        if (IsBlankYaml(text))
        {
            // Keep comments and discard empty document markers before adding the first mapping.
            string comments = Regex.Replace(text.TrimStart('\uFEFF'), @"(?m)^[ \t]*(?:---|\.\.\.)[ \t]*(?:\r?\n|$)", "");
            return (comments + (comments.Length == 0 || comments.EndsWith('\n') ? "" : newline) + "patch:" + newline, new());
        }
        var yaml = new YamlStream();
        yaml.Load(new StringReader(text));
        if (yaml.Documents.Count != 1 || yaml.Documents[0].RootNode is not YamlMappingNode root)
            throw new IOException("輸入法自訂設定需要一份 YAML 對照表。");
        if (!root.Children.TryGetValue(new YamlScalarNode("patch"), out var patch))
        {
            int end = Regex.Match(text, @"(?m)^\.\.\.[ \t]*(?:#.*)?\r?$") is { Success: true } marker
                ? marker.Index : text.Length;
            if (root.Style == YamlDotNet.Core.Events.MappingStyle.Flow)
            {
                if (root.Children.Count != 0) throw new IOException("輸入法自訂設定需要用分行格式。");
                int start = checked((int)root.Start.Index);
                int close = text.IndexOf('}', start);
                return (text.Remove(start, close - start + 1).Insert(start, "patch:"), new());
            }
            return (text.Insert(end, (end > 0 && text[end - 1] != '\n' ? newline : "") + "patch:" + newline), new());
        }
        if (patch is YamlMappingNode overrides && overrides.Children.Count != 0) return (text, overrides);
        if (patch is YamlMappingNode { Children.Count: 0 } ||
            patch is YamlScalarNode { Value: null or "" or "null" or "~" })
        {
            string headerOnly = Regex.Replace(text, @"(?m)^patch:[ \t]*(?:\{[ \t]*\}|null|~)(?=[ \t]*(?:#|\r?$))", "patch:");
            return (headerOnly, new());
        }
        throw new IOException("輸入法自訂設定嘅 patch 格式無效。");
    }

    internal static string IncludeDimsumSchemas(string text)
    {
        (text, var overrides) = EnsurePatch(text);
        if (!overrides.Children.TryGetValue(new YamlScalarNode("schema_list"), out var node))
            return InsertPatch(text, "  schema_list:\n" + string.Join("\n", Schemas.Select(schema => $"    - {{schema: {schema}}}")));
        if (node is not YamlSequenceNode list)
            throw new IOException("輸入方案選單自訂設定無法辨認。請先喺輸入引擎設定加入點心方案。");

        var selected = list.Children.OfType<YamlMappingNode>()
            .Select(entry => entry.Children.TryGetValue(new YamlScalarNode("schema"), out var schema) ? schema.ToString() : "");
        string[] missing = Schemas.Except(selected).ToArray();
        if (missing.Length == 0) return text;
        if (list.Style == YamlDotNet.Core.Events.SequenceStyle.Flow)
        {
            int closing = SequenceEnd(text, checked((int)list.Start.Index));
            return text.Insert(closing, (list.Children.Count == 0 ? "" : ", ") + string.Join(", ", missing.Select(schema => $"{{schema: {schema}}}")));
        }

        if (list.Children.Count == 0) throw new IOException("未能保留現有輸入方案選單。");
        var header = Regex.Match(text, @"(?m)^  schema_list:[ \t]*(?:#[^\r\n]*)?\r?\n");
        if (!header.Success) throw new IOException("未能保留現有輸入方案選單。");
        string nl = header.Value.EndsWith("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        string listIndent = new(' ', checked((int)list.Start.Column - 1));
        return text.Insert(header.Index + header.Length, string.Concat(missing.Select(schema => $"{listIndent}- {{schema: {schema}}}{nl}")));
    }

    internal static string RemoveDimsumSchemas(string text)
    {
        var yaml = new YamlStream();
        yaml.Load(new StringReader(text));
        if (yaml.Documents.Count != 1 || yaml.Documents[0].RootNode is not YamlMappingNode root ||
            !root.Children.TryGetValue(new YamlScalarNode("patch"), out var patch) ||
            patch is not YamlMappingNode overrides ||
            !overrides.Children.TryGetValue(new YamlScalarNode("schema_list"), out var node) ||
            node is not YamlSequenceNode list)
            return text;

        var remaining = list.Children.Where(entry =>
            entry is not YamlMappingNode item || !item.Children.TryGetValue(new YamlScalarNode("schema"), out var schema) ||
            !Schemas.Contains(schema.ToString())).ToArray();
        if (remaining.Length == list.Children.Count) return text;

        var schemaKey = overrides.Children.Keys.OfType<YamlScalarNode>().Single(key => key.Value == "schema_list");
        int start = checked((int)list.Start.Index);
        int end = SequenceEnd(text, start);
        bool flow = list.Style == YamlDotNet.Core.Events.SequenceStyle.Flow;
        if (remaining.Length == 0 && overrides.Style == YamlDotNet.Core.Events.MappingStyle.Flow)
        {
            overrides.Children.Remove(schemaKey);
            using var writer = new StringWriter();
            yaml.Save(writer, assignAnchors: false);
            return writer.ToString();
        }

        int keyStart = checked((int)schemaKey.Start.Index);
        int startLine = text.LastIndexOf('\n', Math.Max(0, keyStart - 1)) + 1;
        string newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        if (flow) end++;
        else if (end < text.Length) end = text.LastIndexOf('\n', Math.Max(0, end - 1)) + 1;
        if (remaining.Length == 0)
        {
            if (flow)
            {
                int nextLine = text.IndexOf('\n', end);
                end = nextLine < 0 ? text.Length : nextLine + 1;
            }
            if (overrides.Children.Count > 1)
                return text.Remove(startLine, end - startLine);
            var patchKey = root.Children.Keys.OfType<YamlScalarNode>().Single(key => key.Value == "patch");
            int patchStart = checked((int)patchKey.Start.Index);
            return text[..patchStart] + "patch: {}" + newline + text[end..];
        }

        var replacement = new YamlSequenceNode(remaining) { Style = YamlDotNet.Core.Events.SequenceStyle.Flow };
        using var value = new StringWriter();
        new YamlStream(new YamlDocument(replacement)).Save(value, assignAnchors: false);
        string formatted = Regex.Replace(value.ToString().TrimEnd(), @"\r?\n\.\.\.$", "");
        if (flow)
            return text[..start] + formatted + text[end..];
        return text[..startLine] + text[startLine..keyStart] + "schema_list: " + formatted + newline + text[end..];
    }

    static int SequenceEnd(string text, int startIndex)
    {
        var parser = new YamlDotNet.Core.Parser(new StringReader(text));
        int depth = 0;
        while (parser.MoveNext())
        {
            if (parser.Current is YamlDotNet.Core.Events.SequenceStart start &&
                (depth > 0 || start.Start.Index == startIndex))
                depth++;
            else if (parser.Current is YamlDotNet.Core.Events.SequenceEnd end && depth > 0 && --depth == 0)
                return checked((int)end.Start.Index);
        }
        throw new IOException("未能保留現有輸入方案選單。");
    }

    internal static string UpdateShiftPatch(string document, bool enabled)
    {
        const string marker = " # Dimsum shift";
        string text = Regex.Replace(document,
            @"(?m)^  ascii_composer/switch_key/Shift_[LR]: inline_ascii # Dimsum shift\r?\n", "");
        if (!enabled) return text;
        (text, var overrides) = EnsurePatch(text);
        if (overrides.Children.Keys.OfType<YamlScalarNode>().Any(key => key.Value is "ascii_composer" or "ascii_composer/switch_key"))
            return text;
        foreach (string side in new[] { "R", "L" })
        {
            string key = "ascii_composer/switch_key/Shift_" + side;
            if (!overrides.Children.ContainsKey(new YamlScalarNode(key)))
                text = InsertPatch(text, $"  {key}: inline_ascii{marker}");
        }
        return text;
    }

    void StartJournal(IEnumerable<string> names)
    {
        Directory.CreateDirectory(Journal);
        var files = new Dictionary<string, bool>();
        foreach (string name in names)
        {
            string from = Path.Combine(Rime, name);
            bool exists = File.Exists(from);
            if (exists) WriteAtomic(Path.Combine(Journal, "original", name), File.ReadAllBytes(from));
            files.Add(name, exists);
        }
        if (File.Exists(Path.Combine(Rime, UserDictionary)))
        {
            string backups = Path.Combine(data, "backups");
            Directory.CreateDirectory(backups);
            if (files.ContainsKey(UserDictionary))
                File.Copy(Path.Combine(Rime, UserDictionary), Path.Combine(backups, $"{Guid.NewGuid():N}.user.dict.yaml"));
        }
        WriteAtomic(Path.Combine(Journal, "restore.json"), JsonSerializer.SerializeToUtf8Bytes(files));
    }

    internal static bool ArchiveInterruptedDeployment(string journal, string rime, string backups)
    {
        if (!Directory.Exists(journal)) return false;
        string backup = Path.Combine(backups, Guid.NewGuid().ToString("N"));
        try
        {
            var files = ReadDeploymentJournal(journal);
            MachineLegacyUpgrade.CopyTree(journal, backup);
            // Keep current edits. The snapshot can fill in any missing pre-deployment source.
            foreach (var file in files.Where(file => file.Value))
            {
                string current = Path.Combine(rime, file.Key);
                if (!File.Exists(current))
                    WriteAtomic(current, File.ReadAllBytes(Path.Combine(backup, "original", file.Key)));
            }
            // Closing only after the backup and source recovery succeed makes this retryable.
            CloseJournal(journal);
            return true;
        }
        catch (Exception error)
        {
            throw new IOException($"未能保全未完成處理嘅資料。{Environment.NewLine}{error.Message}{Environment.NewLine}備份位置：{backup}", error);
        }
    }

    static Dictionary<string, bool> ReadDeploymentJournal(string journal)
    {
        string marker = Path.Combine(journal, "restore.json");
        if (!File.Exists(marker)) return new();
        var files = JsonSerializer.Deserialize<Dictionary<string, bool>>(File.ReadAllBytes(marker)) ??
            throw new IOException("未能讀取處理備份記錄。");
        foreach (string name in files.Keys)
        {
            if (Path.IsPathRooted(name) || name.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Any(part => part is "" or "." or ".." || part.Contains(':')))
                throw new IOException("處理備份記錄含有無效路徑。");
        }
        return files;
    }

    void RestoreDeploymentSources()
    {
        foreach (var entry in ReadDeploymentJournal(Journal))
        {
            string path = Path.Combine(Rime, entry.Key);
            if (entry.Value) WriteAtomic(path, File.ReadAllBytes(Path.Combine(Journal, "original", entry.Key)));
            else File.Delete(path);
        }
    }

    async Task RecoverAsync(string deployer)
    {
        if (!Directory.Exists(Journal)) return;
        if (File.Exists(Path.Combine(Journal, "restore.json")))
        {
            RestoreDeploymentSources();
            await RunDeployerAsync(deployer);
            string primary = Path.Combine(Rime, Dictionary);
            string custom = Path.Combine(Rime, UserDictionary);
            if (File.Exists(primary) && File.Exists(custom)) VerifyTable(File.ReadAllBytes(primary), File.ReadAllBytes(custom));
        }
        CloseJournal();
    }

    void CloseJournal() => CloseJournal(Journal);

    static void CloseJournal(string journal)
    {
        string finished = journal + "." + Guid.NewGuid().ToString("N");
        Directory.Move(journal, finished);
        try { Directory.Delete(finished, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    void VerifyTable(byte[] primary, byte[] custom)
    {
        string path = Path.Combine(Rime, "build", "dimsum_cb.table.bin");
        if (!File.Exists(path)) throw new IOException("輸入引擎未產生點心字庫。請確認至少一個點心方案已啟用。");
        using var reader = File.OpenRead(path);
        Span<byte> header = stackalloc byte[44];
        if (reader.Length < header.Length) throw new IOException("編譯字庫檔案不完整。");
        reader.ReadExactly(header);
        if (!header.StartsWith("Rime::Table/"u8))
            throw new IOException("編譯字庫格式無法辨認。");
        if (BinaryPrimitives.ReadUInt32LittleEndian(header[40..]) == 0)
            throw new IOException("編譯字庫冇字詞。");
        uint actual = BinaryPrimitives.ReadUInt32LittleEndian(header[32..]);
        uint binary = DictionaryCrc(false, primary, custom);
        // The bundled Windows librime reads each YAML file in text mode. Newer
        // librime reads the same files in binary mode.
        if (actual == binary) return;
        uint windowsText = DictionaryCrc(true, primary, custom);
        if (actual != windowsText)
            throw new IOException($"編譯字庫與來源內容唔一致。編譯檔 {actual:X8}，來源 {binary:X8}，Windows 文字格式 {windowsText:X8}。");
    }

    internal static uint DictionaryCrc(bool windowsText, params byte[][] sources)
    {
        uint crc = 0; // librime explicitly seeds Boost CRC with a zero internal remainder.
        var table = new uint[256];
        for (uint value = 0; value < 256; value++)
        {
            uint remainder = value;
            for (int bit = 0; bit < 8; bit++)
                remainder = (remainder >> 1) ^ ((remainder & 1) == 0 ? 0 : 0xedb88320u);
            table[value] = remainder;
        }
        foreach (var source in sources)
        {
            for (int index = 0; index < source.Length; index++)
            {
                byte value = source[index];
                if (windowsText && value == '\r' && index + 1 < source.Length && source[index + 1] == '\n')
                    continue;
                crc = (crc >> 8) ^ table[(crc ^ value) & 0xff];
            }
        }
        return ~crc;
    }

    static string InspectDictionary(byte[] contents, string name, bool primary)
    {
        string text = Utf8.GetString(contents).TrimStart('\uFEFF');
        var end = Regex.Match(text, @"(?m)^\.\.\.[ \t]*\r?$");
        if (!end.Success || end.Index > 16384) throw new IOException($"字庫 {name} 嘅 YAML 標頭無效。");
        var yaml = new YamlStream();
        yaml.Load(new StringReader(text[..end.Index]));
        var map = yaml.Documents.Count == 1 ? yaml.Documents[0].RootNode as YamlMappingNode : null;
        if (map is null || Read("name") != name || string.IsNullOrWhiteSpace(Read("version")))
            throw new IOException($"字庫 {name} 嘅名稱或版本無效。");
        if (primary && (!map.Children.TryGetValue(new YamlScalarNode("import_tables"), out var importNode) || importNode is not YamlSequenceNode imports ||
            imports.Children.Count != 1 || imports.Children[0].ToString() != "dimsum_cb.user" ||
            Read("use_preset_vocabulary") == "true" || Read("vocabulary") is not null ||
            !text[(end.Index + end.Length)..].Split('\n').Any(row => !row.StartsWith('#') && row.Contains('\t'))))
            throw new IOException("點心字庫嘅匯入格式無法驗證。");
        return Read("version")!;

        string? Read(string key) => map is not null && map.Children.TryGetValue(new YamlScalarNode(key), out var node) && node is YamlScalarNode scalar ? scalar.Value : null;
    }

    internal static byte[] ImportLegacy(string source, byte[] existing)
    {
        string text = Utf8.GetString(existing);
        var rows = new HashSet<(string Word, string Code)>();
        foreach (string line in text.Split('\n'))
        {
            string[] fields = line.TrimEnd('\r').Split('\t');
            if (!line.StartsWith('#') && fields.Length >= 2) rows.Add((fields[0], fields[1]));
        }
        var added = new StringBuilder();
        foreach (string rawLine in File.ReadLines(source, Utf8))
        {
            string line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            string[] fields = line.Split('\t');
            foreach (string field in fields.Skip(1))
            {
                string word = field.Trim();
                if (word.Length > 0 && !word.StartsWith('#') && fields[0].Length > 0 && rows.Add((word, fields[0])))
                    added.AppendLine($"{word}\t{fields[0]}\t9999999");
            }
        }
        if (added.Length == 0) return existing;
        return Utf8.GetBytes(text.TrimEnd('\r', '\n') + Environment.NewLine + added);
    }

    static async Task RunDeployerAsync(string executable)
    {
        using var process = Process.Start(new ProcessStartInfo(executable, "/deploy")
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(executable)!
        }) ?? throw new IOException("未能啟動輸入引擎處理。");
        await process.WaitForExitAsync();
        if (process.ExitCode != 0) throw new IOException($"輸入引擎處理傳回錯誤 {process.ExitCode}。");
    }

    public static string? FindDeployer()
    {
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            using var key = machine.OpenSubKey(@"Software\Rime\Weasel");
            if (key?.GetValue("WeaselRoot") is not string root) continue;
            string file = Path.Combine(root, "WeaselDeployer.exe");
            if (File.Exists(file)) return file;
        }
        return null;
    }

    internal static string HashFile(string path) => Hash(File.ReadAllBytes(path));
    static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    internal static void WriteAtomic(string path, byte[] contents)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temporary, contents);
            File.Move(temporary, path, overwrite: true);
        }
        finally { File.Delete(temporary); }
    }
}

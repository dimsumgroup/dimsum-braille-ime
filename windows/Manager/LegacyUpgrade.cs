using System.Diagnostics;
using System.Drawing;
using System.Text.Json;
using Microsoft.Win32;

namespace Dimsum;

sealed class LegacyUpgrade(Workspace workspace)
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string RunName = "DimsumCbConverter";
    const string LegacySettings = @"Software\DimsumGroup\DimsumInput";
    const string EngineSettings = @"Software\Rime\Weasel";
    static readonly string[] OldValues = ["AppVersion", "InstallNvdaDict", "SkipVersion"];
    static readonly string[] EngineValues = ["RimeUserDir", "Hant", "Language", "ToggleMode"];
    internal static readonly string[] OldFiles = ["cb_converter.exe", "cb_converter.ps1"];
    enum Stage { Accepted, DataSaved, EngineReady, Deployed, Complete }
    sealed record Checkpoint(Guid Id, Stage Stage, bool DictionaryDeployed = false);
    string StateFile => Path.Combine(workspace.DataDirectory, "legacy-upgrade.json");
    string BackupFolder => Path.Combine(workspace.DataDirectory, "舊版遷移備份");
    Checkpoint? ReadState() => File.Exists(StateFile) ? JsonSerializer.Deserialize<Checkpoint>(File.ReadAllBytes(StateFile)) : null;

    public bool NeedsUpgrade()
    {
        if (ReadState() is { Stage: < Stage.Complete }) return true;
        using var old = Registry.CurrentUser.OpenSubKey(LegacySettings);
        using var run = Registry.CurrentUser.OpenSubKey(RunKey);
        return IsLegacyVersion(old?.GetValue("AppVersion") as string) ||
            ValidatedRunCommand(run) is not null ||
            WindowsSetup.FindEntries(RegistryHive.CurrentUser).Any(entry => !entry.IsEngine) ||
            WindowsSetup.FindEntries(RegistryHive.LocalMachine).Any(entry => !entry.IsEngine) ||
            MachineLegacyUpgrade.KnownFolders().Any(folder => OldFiles.Any(file => File.Exists(Path.Combine(folder, file)))) ||
            MachineLegacyUpgrade.HasOwnedTask();
    }

    public async Task<string?> RunAsync(IWin32Window owner, InputLanguageChoice language, Action<string> progress, bool makeDefault)
    {
        var state = ReadState();
        if (state?.Stage == Stage.Complete) state = null;
        if (state is null && !NeedsUpgrade()) return null;

        if (state is null)
        {
            if (!Confirm(owner)) return "";
            state = Advance(new(Guid.NewGuid(), Stage.Accepted), Stage.Accepted);
        }

        string id = state.Id.ToString("N");
        string backup = Path.Combine(BackupFolder, id);
        string nvdaBackup = Path.Combine(backup, NvdaDictionaries.BackupFolder);
        string stage = "備份用家資料";
        try
        {
            // A previous version could leave a migration checkpoint after its
            // engine uninstaller removed the binaries.
            if (state.Stage >= Stage.EngineReady && Workspace.FindDeployer() is null)
                state = Advance(state with { DictionaryDeployed = false }, Stage.DataSaved);
            if (state.Stage < Stage.DataSaved)
            {
                progress("正在備份用家資料。");
                Directory.CreateDirectory(backup);
                SaveAndPauseOldStartup(backup);
                SaveEngineSettings(backup);
                await WaitForOldUpdaterAsync();
                await WindowsSetup.StopEngineAsync();
                MachineLegacyUpgrade.CopyTree(workspace.Rime, Path.Combine(backup, "Rime"));
                state = Advance(state, Stage.DataSaved);
            }

            stage = "備份 NVDA 配詞檔";
            NvdaDictionaries.BackupInstalledDictionaries(NvdaDictionaries.FindInstallation(), nvdaBackup);

            if (state.Stage < Stage.EngineReady)
            {
                stage = "更新輸入引擎";
                progress("正在更新輸入引擎。");
                string? engineRoot = Path.GetDirectoryName(Workspace.FindDeployer());
                if (engineRoot is not null && MachineLegacyUpgrade.IsWithin(engineRoot, workspace.Rime))
                    throw new IOException("用家資料位於輸入引擎資料夾內。請先將用家資料移到其他位置，再更新引擎。");
                await WindowsSetup.RunAdminAsync("--prepare-legacy-engine", id);
                state = Advance(state, Stage.EngineReady);
            }

            if (state.Stage < Stage.Deployed)
            {
                stage = "部署點心字庫同設定";
                RestoreEngineSettings(backup);
                WindowsSetup.StartAvailableEngine();
                string engineCheckpoint = Path.Combine(MachineLegacyUpgrade.EngineBackup(id), "engine-package-installed");
                bool verified = state.DictionaryDeployed ? workspace.SetupDeploymentUsable() :
                    File.Exists(engineCheckpoint) && workspace.SetupDeploymentUsable(File.GetLastWriteTimeUtc(engineCheckpoint));
                if (!verified)
                {
                    progress("正在部署點心字庫同設定。");
                    await workspace.DeployAsync(applyPending: true, reporting: true, legacyMigration: true);
                }
                if (!state.DictionaryDeployed) state = Advance(state with { DictionaryDeployed = true }, state.Stage);
                stage = "加入 Windows 輸入法";
                progress($"正在將點心加入到「{language.Label}」。");
                await workspace.FinishSetupAsync(language, makeDefault);
                state = Advance(state, Stage.Deployed);
            }
            else if (!WindowsSetup.SameLanguage(workspace.Settings.InputLanguageTag, language.Tag) || workspace.Settings.SetupPending)
            {
                stage = "加入 Windows 輸入法";
                progress("正在加入點心到 Windows 輸入法。");
                await workspace.ApplyInputLanguageAsync(language, makeDefault);
            }

            stage = "移除舊版自動更新元件";
            progress("正在移除舊版自動更新元件。");
            if (MachineLegacyUpgrade.NeedsMachineCleanup(id))
                await WindowsSetup.RunAdminAsync("--finish-legacy-cleanup", id);
            ClearOldUserEntries(backup);
            Advance(state, Stage.Complete);
            workspace.ConfigureStartup(retireLegacy: true);
            string result = "已更新輸入引擎並移除舊版自動更新元件。已保留學習記錄、自訂詞庫同設定。";
            return Directory.Exists(nvdaBackup)
                ? result + Environment.NewLine + $"NVDA 現存配詞檔已備份到「{nvdaBackup}」。"
                : result;
        }
        catch (Exception error)
        {
            string recovery = "";
            try
            {
                RestoreEngineSettings(backup);
                WindowsSetup.StartAvailableEngine();
            }
            catch (Exception restart)
            {
                recovery = $"回復現有輸入設定時發生錯誤：{restart.Message}{Environment.NewLine}";
            }
            throw new IOException($"舊版遷移停喺「{stage}」。{Environment.NewLine}{error.Message}{Environment.NewLine}" +
                recovery +
                $"用家備份：{backup}{Environment.NewLine}引擎備份：{MachineLegacyUpgrade.EngineBackup(id)}{Environment.NewLine}" +
                "可以按「繼續設定點心」重試。", error);
        }
    }

    public async Task<string?> BeginRemovalAsync()
    {
        if (!NeedsUpgrade()) return null;
        string id = (ReadState()?.Id ?? Guid.NewGuid()).ToString("N");
        SaveAndPauseOldStartup(Path.Combine(BackupFolder, id));
        await WaitForOldUpdaterAsync();
        return id;
    }

    public void FinishRemoval(string? id)
    {
        if (id is not null) ClearOldUserEntries(Path.Combine(BackupFolder, id));
        File.Delete(StateFile);
    }

    static void SaveEngineSettings(string backup)
    {
        using var engine = Registry.CurrentUser.OpenSubKey(EngineSettings);
        string snapshot = Path.Combine(backup, "輸入引擎用家設定.json");
        if (!File.Exists(snapshot))
            Workspace.WriteAtomic(snapshot, JsonSerializer.SerializeToUtf8Bytes(EngineValues.ToDictionary(
                name => name, name => engine?.GetValue(name))));
    }

    static void RestoreEngineSettings(string backup)
    {
        string snapshot = Path.Combine(backup, "輸入引擎用家設定.json");
        if (!File.Exists(snapshot)) return;
        var saved = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllBytes(snapshot))!;
        using var engine = Registry.CurrentUser.CreateSubKey(EngineSettings);
        foreach (string name in EngineValues)
        {
            if (!saved.TryGetValue(name, out JsonElement value)) continue;
            if (value.ValueKind == JsonValueKind.String) engine.SetValue(name, value.GetString()!, RegistryValueKind.String);
            else if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int number))
                engine.SetValue(name, number, RegistryValueKind.DWord);
        }
    }

    Checkpoint Advance(Checkpoint state, Stage stage)
    {
        var next = state with { Stage = stage };
        Workspace.WriteAtomic(StateFile, JsonSerializer.SerializeToUtf8Bytes(next));
        return next;
    }

    static bool Confirm(IWin32Window owner)
    {
        using var form = new Form
        {
            Text = "偵測到舊版點心", StartPosition = FormStartPosition.CenterParent,
            Size = new Size(530, 220), FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false, MinimizeBox = false
        };
        var layout = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, Dock = DockStyle.Fill, Padding = new Padding(18) };
        layout.Controls.Add(new Label
        {
            AutoSize = true, MaximumSize = new Size(470, 0),
            Text = "偵測到舊版點心。按「確定」後，程式會更新輸入引擎，並移除舊版自動更新元件。你嘅學習記錄、自訂詞庫同設定會保留。"
        });
        var confirm = new Button { Text = "確定", DialogResult = DialogResult.OK, AutoSize = true };
        layout.Controls.Add(confirm);
        form.Controls.Add(layout);
        form.AcceptButton = confirm;
        return form.ShowDialog(owner) == DialogResult.OK;
    }

    static void SaveAndPauseOldStartup(string backup)
    {
        using var old = Registry.CurrentUser.OpenSubKey(LegacySettings);
        using var run = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        string? command = ValidatedRunCommand(run);
        string file = Path.Combine(backup, "舊版登記.json");
        if (!File.Exists(file))
            Workspace.WriteAtomic(file, JsonSerializer.SerializeToUtf8Bytes(new
            {
                Run = command,
                Settings = OldValues.ToDictionary(name => name, name => old?.GetValue(name))
            }));
        if (command is not null) run!.DeleteValue(RunName, false);
    }

    internal static string? ValidatedRunCommand(RegistryKey? run)
    {
        string? value = run?.GetValue(RunName) as string;
        string? executable = WindowsSetup.ParseExecutable(value);
        return executable is not null && MachineLegacyUpgrade.IsOwnedUpdater(executable, MachineLegacyUpgrade.KnownFolders()) ? value : null;
    }

    internal static void PauseOldStartup()
    {
        using var run = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        if (ValidatedRunCommand(run) is not null) run!.DeleteValue(RunName, false);
    }

    static void ClearOldUserEntries(string backup)
    {
        PauseOldStartup();
        using var old = Registry.CurrentUser.OpenSubKey(LegacySettings, writable: true);
        if (old is not null && (old.GetValue("AppVersion") is not string version || IsLegacyVersion(version)))
            foreach (string name in OldValues) old.DeleteValue(name, false);
        foreach (var entry in WindowsSetup.FindEntries(RegistryHive.CurrentUser).Where(entry => !entry.IsEngine).ToArray())
        {
            MachineLegacyUpgrade.SaveRegistration(backup, entry.Hive, entry.View, entry.Key);
            MachineLegacyUpgrade.DeleteOldEntry(entry);
        }
    }

    internal static bool IsLegacyVersion(string? version) =>
        Version.TryParse(version?.TrimStart('v', 'V'), out var parsed) && parsed < new Version(1, 3);

    static async Task WaitForOldUpdaterAsync()
    {
        string[] folders = MachineLegacyUpgrade.KnownFolders();
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        for (int attempt = 0; ConverterRunning(folders); attempt++)
        {
            if (attempt >= 70 || !await timer.WaitForNextTickAsync())
                throw new IOException("舊版詞庫更新仍在執行。請完成後再部署。");
        }
    }

    internal static bool ConverterRunning(string[] folders)
    {
        var processes = Process.GetProcessesByName("cb_converter");
        try
        {
            foreach (var process in processes)
            {
                try
                {
                    if (process.MainModule?.FileName is string executable && MachineLegacyUpgrade.IsOwnedUpdater(executable, folders))
                        return true;
                }
                catch (InvalidOperationException) { }
                catch (System.ComponentModel.Win32Exception) { /* A privileged migration rechecks before replacing the engine. */ }
            }
            return false;
        }
        finally { foreach (var process in processes) process.Dispose(); }
    }
}

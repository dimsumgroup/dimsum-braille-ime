using System.Runtime.InteropServices;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.Win32;

namespace Dimsum;

static class MachineLegacyUpgrade
{
    const string TaskName = "DimsumDictConverter";
    const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    internal static string[] KnownFolders() =>
        new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 }
            .Select(Environment.GetFolderPath).Where(path => !string.IsNullOrEmpty(path))
            .Select(path => Path.Combine(path, "DimsumInput")).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    internal static bool IsOwnedUpdater(string executable, IEnumerable<string> folders) =>
        LegacyUpgrade.OldFiles.Contains(Path.GetFileName(executable), StringComparer.OrdinalIgnoreCase) &&
        folders.Any(folder => string.Equals(Path.GetDirectoryName(executable), folder, StringComparison.OrdinalIgnoreCase));

    internal static string EngineBackup(string id)
    {
        if (!Guid.TryParseExact(id, "N", out var identity)) throw new ArgumentException("遷移識別碼無效。");
        string protectedPath = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolderOption.DoNotVerify);
        return Path.Combine(protectedPath, "DimsumGroup", "LegacyMigrationBackups", identity.ToString("N"));
    }

    public static async Task PrepareEngineAsync(string id)
    {
        string backup = EngineBackup(id);
        string step = "建立引擎備份位置";
        try
        {
            VerifyOrdinaryPath(backup);
            Directory.CreateDirectory(backup);

            step = "檢查並停用舊版更新排程";
            PauseOldUpdater(id);

            string ready = Path.Combine(backup, "engine-backup-complete");
            string installed = Path.Combine(backup, "engine-package-installed");
            bool updateEngine = !File.Exists(installed) || Workspace.FindDeployer() is null;
            if (updateEngine)
            {
                step = "等候舊輸入引擎退出";
                await WindowsSetup.StopEngineAsync();
            }

            if (!File.Exists(ready))
            {
                step = "備份舊輸入引擎同登記";
                string? deployer = Workspace.FindDeployer();
                if (deployer is not null)
                {
                    string oldEngine = Path.GetDirectoryName(deployer)!;
                    CopyTree(oldEngine, Path.Combine(backup, "engine"));
                    var reg = WindowsSetup.FindEntries(RegistryHive.LocalMachine).Where(entry => entry.IsEngine)
                        .Select(entry => (entry.View, entry.Key));
                    foreach (var entry in reg)
                        SaveRegistration(backup, RegistryHive.LocalMachine, entry.View, entry.Key);
                    File.WriteAllText(Path.Combine(backup, "original-engine-path.txt"), oldEngine);
                }
                Workspace.WriteAtomic(ready, []);
            }

            if (updateEngine)
            {
                step = "執行內附引擎安裝器並核對版本";
                await WindowsSetup.StopEngineAsync();
                await WindowsSetup.InstallBundledEngineAsync(forceUpgrade: true);
                Workspace.WriteAtomic(installed, []);
            }
        }
        catch (Exception error)
        {
            throw new IOException($"「{step}」失敗。{Environment.NewLine}{error.Message}（0x{error.HResult:X8}）", error);
        }
    }

    public static bool NeedsMachineCleanup(string id)
    {
        var folders = KnownFolders();
        return folders.Any(folder => LegacyUpgrade.OldFiles.Any(file => File.Exists(Path.Combine(folder, file)))) ||
            WindowsSetup.FindEntries(RegistryHive.LocalMachine).Any(entry => !entry.IsEngine) ||
            WindowsSetup.FindEntries(RegistryHive.CurrentUser).Any(entry => !entry.IsEngine &&
                folders.Contains(Path.GetDirectoryName(entry.Executable), StringComparer.OrdinalIgnoreCase)) ||
            File.Exists(Path.Combine(EngineBackup(id), TaskName + ".xml")) ||
            HasOwnedTask();
    }

    public static void PauseOldUpdater(string id)
    {
        string backup = EngineBackup(id);
        VerifyOrdinaryPath(backup);
        Directory.CreateDirectory(backup);
        var folders = KnownFolders();
        UseOwnedTask(folders, (_, task, xml) =>
        {
            string snapshot = Path.Combine(backup, TaskName + ".xml");
            if (!File.Exists(snapshot)) File.WriteAllText(snapshot, xml);
            task.Enabled = false;
            if ((int)task.GetInstances(0).Count != 0)
                throw new IOException("舊版自動更新排程仍在執行。請等佢完成後再試。");
        });
        if (LegacyUpgrade.ConverterRunning(folders))
            throw new IOException("舊版詞庫更新仍在執行。請等佢完成後再試。");
    }

    internal static bool HasOwnedTask()
    {
        bool found = false;
        try { UseOwnedTask(KnownFolders(), (_, _, _) => found = true); }
        catch (COMException) { }
        catch (TypeLoadException) { }
        return found;
    }

    public static void FinalizeCleanup(string id)
    {
        string backup = EngineBackup(id);
        VerifyOrdinaryPath(backup);
        if (!File.Exists(Path.Combine(backup, "engine-package-installed")))
            throw new IOException("未確認引擎已更新，舊版更新元件會保留俾稍後繼續處理。");
        RetireOldInstallation(id);
    }

    public static void RetireOldInstallation(string id)
    {
        string backup = EngineBackup(id);
        PauseOldUpdater(id);
        var folders = KnownFolders();
        UseOwnedTask(folders, (taskFolder, _, _) => taskFolder.DeleteTask(TaskName, 0));
        var entries = WindowsSetup.FindEntries(RegistryHive.LocalMachine)
            .Concat(WindowsSetup.FindEntries(RegistryHive.CurrentUser))
            .Where(entry => !entry.IsEngine).ToArray();
        foreach (string folder in folders)
        {
            VerifyOrdinaryPath(folder);
            string archive = Path.Combine(backup, "old-program",
                folder.Contains("(x86)", StringComparison.OrdinalIgnoreCase) ? "x86" : "native");
            var uninstallFiles = entries.SelectMany(entry => LegacyUninstallFiles(entry.Executable, folder));
            foreach (string file in LegacyUpgrade.OldFiles.Select(name => Path.Combine(folder, name))
                .Concat(uninstallFiles).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!File.Exists(file)) continue;
                VerifyOrdinaryPath(file);
                string destination = Path.Combine(archive, Path.GetFileName(file));
                VerifyOrdinaryPath(destination);
                Directory.CreateDirectory(archive);
                if (!File.Exists(destination)) File.Copy(file, destination);
                File.Delete(file);
            }
            if (Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any())
                Directory.Delete(folder);
        }
        // The unelevated manager handles the original user's HKCU record.
        foreach (var entry in entries.Where(entry => entry.Hive == RegistryHive.LocalMachine))
        {
            SaveRegistration(backup, entry.Hive, entry.View, entry.Key);
            DeleteOldEntry(entry);
        }
    }

    internal static IEnumerable<string> LegacyUninstallFiles(string executable, string folder)
    {
        if (!string.Equals(Path.GetDirectoryName(executable), folder, StringComparison.OrdinalIgnoreCase))
            yield break;
        // Executable comes from a validated legacy Inno Setup registration.
        yield return executable;
        yield return Path.ChangeExtension(executable, ".dat");
        yield return Path.ChangeExtension(executable, ".msg");
    }

    internal static void SaveRegistration(string backup, RegistryHive hive, RegistryView view, string name)
    {
        using var root = RegistryKey.OpenBaseKey(hive, view);
        using var entry = root.OpenSubKey($@"{UninstallKey}\{name}");
        if (entry is null) return;
        string record = Path.Combine(backup, $"{view}-{name}.json");
        if (!File.Exists(record))
            Workspace.WriteAtomic(record, JsonSerializer.SerializeToUtf8Bytes(entry.GetValueNames().ToDictionary(
                key => key, key => entry.GetValue(key, null, RegistryValueOptions.DoNotExpandEnvironmentNames))));
    }

    internal static void DeleteOldEntry(WindowsSetup.InstallEntry entry)
    {
        using var root = RegistryKey.OpenBaseKey(entry.Hive, entry.View);
        using var key = root.OpenSubKey(UninstallKey, writable: true);
        key?.DeleteSubKeyTree(entry.Key, false);
    }

    internal static void CopyTree(string source, string destination)
    {
        if (!Directory.Exists(source)) return;
        if (IsWithin(source, destination))
            throw new IOException("備份位置位於原有資料夾內。");
        VerifyOrdinaryPath(source);
        VerifyOrdinaryPath(destination);
        var pending = new Stack<(string From, string To)>();
        pending.Push((source, destination));
        while (pending.TryPop(out var item))
        {
            VerifyOrdinaryPath(item.From);
            VerifyOrdinaryPath(item.To);
            Directory.CreateDirectory(item.To);
            foreach (string entry in Directory.EnumerateFileSystemEntries(item.From))
            {
                VerifyOrdinaryPath(entry);
                string target = Path.Combine(item.To, Path.GetFileName(entry));
                if (Directory.Exists(entry)) pending.Push((entry, target));
                else
                {
                    VerifyOrdinaryPath(target);
                    File.Copy(entry, target, overwrite: true);
                }
            }
        }
    }

    internal static bool IsWithin(string root, string path)
    {
        string relative = Path.GetRelativePath(root, path);
        return !Path.IsPathRooted(relative) && relative != ".." &&
            !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    static void VerifyOrdinaryPath(string path)
    {
        for (string? part = path; part is not null; part = Path.GetDirectoryName(part))
            if ((Directory.Exists(part) || File.Exists(part)) && (File.GetAttributes(part) & FileAttributes.ReparsePoint) != 0)
                throw new IOException($"遷移路徑包含重新導向：{part}");
    }

    static void UseOwnedTask(string[] folders, Action<dynamic, dynamic, string> apply)
    {
        const int ServiceDisabled = unchecked((int)0x80070422);
        object? scheduler = null, folderObject = null, taskObject = null;
        try
        {
            scheduler = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service", throwOnError: true)!);
            dynamic connected = scheduler!;
            try { connected.Connect(); }
            catch (Exception error) when (error.HResult == ServiceDisabled) { return; }
            folderObject = connected.GetFolder(@"\");
            dynamic folder = folderObject!;
            taskObject = FindScheduledTask(folderObject!);
            if (taskObject is null) return;
            dynamic task = taskObject!;
            string xml = task.Xml;
            if (MatchesTask(xml, folders)) apply(folder, task, xml);
        }
        finally
        {
            foreach (object? handle in new[] { taskObject, folderObject, scheduler })
                if (handle is not null && Marshal.IsComObject(handle)) Marshal.FinalReleaseComObject(handle);
        }
    }

    internal static object? FindScheduledTask(object folder)
    {
        // COM interop can map the same Windows status to FileNotFoundException.
        const int NotFound = unchecked((int)0x80070002);
        try { return ((dynamic)folder).GetTask(TaskName); }
        catch (Exception error) when (error.HResult == NotFound) { return null; }
    }

    internal static bool MatchesTask(string xml, string[] folders)
    {
        var document = XDocument.Parse(xml);
        if (document.Root is null) return false;
        XNamespace ns = document.Root.Name.Namespace;
        var actions = document.Root.Element(ns + "Actions")?.Elements().ToArray();
        if (actions is not { Length: 1 } || actions[0].Name != ns + "Exec") return false;

        var action = actions[0];
        string command = Environment.ExpandEnvironmentVariables(action.Element(ns + "Command")?.Value ?? "").Trim('"');
        if (Path.IsPathFullyQualified(command) && IsOwnedUpdater(Path.GetFullPath(command), folders))
            return true;
        if (!Path.GetFileNameWithoutExtension(command).Equals("powershell", StringComparison.OrdinalIgnoreCase))
            return false;

        string arguments = Environment.ExpandEnvironmentVariables(action.Element(ns + "Arguments")?.Value ?? "");
        var script = System.Text.RegularExpressions.Regex.Match(arguments,
            @"(?:^|\s)-File\s+(?:""(?<file>[^""]+)""|(?<file>\S+))\s*$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        string file = script.Groups["file"].Value;
        return Path.IsPathFullyQualified(file) && IsOwnedUpdater(Path.GetFullPath(file), folders);
    }
}

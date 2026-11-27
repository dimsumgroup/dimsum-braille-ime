using Microsoft.Win32;

namespace Dimsum;

static partial class WindowsSetup
{
    internal enum EngineOutcome { Removed, RestartRequired, Incomplete }

    static string ServiceClassKey => $@"SOFTWARE\Classes\CLSID\{Service:B}";
    static string ServiceTipKey => $@"SOFTWARE\Microsoft\CTF\TIP\{Service:B}";
    static readonly RegistryView[] EngineViews = [RegistryView.Registry64, RegistryView.Registry32];

    public static async Task<EngineOutcome> RemoveEngineAsync(string? legacyBackupId)
    {
        string[] files = TrackedEngineFiles(FindEngineUninstaller());
        var initial = InspectRemoval(files);
        if (initial != EngineOutcome.Incomplete && legacyBackupId is null) return initial;

        bool stopped = await StopEngineAsync();
        try
        {
            string[] args = legacyBackupId is null
                ? ["--remove-engine-machine"]
                : ["--remove-engine-machine", legacyBackupId];
            int code = await RunProcessWithExitCodeAsync(Environment.ProcessPath!, true, true, args);
            return code == 3 ? EngineOutcome.RestartRequired : EngineOutcome.Removed;
        }
        catch (Exception error) when (stopped)
        {
            try { StartAvailableEngine(); }
            catch (Exception restart)
            {
                throw new IOException($"{error.Message}{Environment.NewLine}重新啟動引擎亦失敗：{restart.Message}", error);
            }
            throw;
        }
    }

    public static async Task<EngineOutcome> RemoveEngineMachineAsync()
    {
        var engine = FindEngineUninstaller();
        string[] files = TrackedEngineFiles(engine);
        var outcome = InspectRemoval(files);
        if (outcome != EngineOutcome.Incomplete) return outcome;

        if (engine is not null)
        {
            await StopEngineAsync();
            await RunProcessAsync(engine.Executable, elevate: false, "/S");
            // NSIS may hand over from its bootstrap process to a temporary uninstaller.
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(500));
            for (int attempt = 0; attempt < 60 && await timer.WaitForNextTickAsync(); attempt++)
            {
                outcome = InspectRemoval(files);
                if (outcome != EngineOutcome.Incomplete) return outcome;
                if (CanClearStaleService(ReadEngineFacts(files))) break;
            }
        }

        var facts = ReadEngineFacts(files);
        if (CanClearStaleService(facts))
        {
            ClearStaleService();
            outcome = InspectRemoval(files);
            if (outcome != EngineOutcome.Incomplete) return outcome;
            facts = ReadEngineFacts(files);
        }

        var remaining = new List<string>();
        if (facts.Registered)
        {
            var paths = facts.ServicePaths.Where(path => path.Length != 0).Distinct(StringComparer.OrdinalIgnoreCase);
            remaining.Add("輸入服務登記");
            remaining.AddRange(paths);
        }
        if (facts.StartsAtLogin) remaining.Add("登入啟動項目");
        if (facts.Listed) remaining.Add("引擎安裝登記");
        if (facts.Files.Length != 0) remaining.Add(string.Join("、", facts.Files.Select(Path.GetFileName)));
        throw new IOException("引擎移除尚未完成。仍待處理：" + string.Join("、", remaining) + "。");
    }

    static InstallEntry? FindEngineUninstaller() =>
        FindEntries(RegistryHive.LocalMachine).FirstOrDefault(entry => entry.IsEngine);

    static string[] EngineSystemDirectories() =>
        new[] { "System32", "SysWOW64", "SysArm32" }.Select(name =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), name)).ToArray();

    static string[] TrackedEngineFiles(InstallEntry? engine)
    {
        var tracked = new List<string>();
        string? root = Path.GetDirectoryName(engine?.Executable ?? Workspace.FindDeployer());
        if (root is not null)
            tracked.AddRange(new[] { "WeaselServer.exe", "WeaselDeployer.exe", "WeaselSetup.exe", "uninstall.exe" }
                .Select(file => Path.Combine(root, file)));
        tracked.AddRange(ReadEngineFacts([]).ServicePaths.Where(Path.IsPathFullyQualified));
        foreach (string system in EngineSystemDirectories())
            tracked.AddRange(new[] { "weasel.dll", "weasel.ime" }.Select(file => Path.Combine(system, file)));
        return tracked.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    static EngineOutcome InspectRemoval(string[] originalFiles)
    {
        var facts = ReadEngineFacts(originalFiles);
        string[] pending = PendingEngineFiles(originalFiles);
        return ClassifyRemoval(facts.Registered, facts.StartsAtLogin, facts.Listed, facts.Files, pending);
    }

    internal static EngineOutcome ClassifyRemoval(bool registered, bool startsAtLogin, bool listed, string[] files, string[] pending)
    {
        if (registered || startsAtLogin || listed || files.Except(pending, StringComparer.OrdinalIgnoreCase).Any())
            return EngineOutcome.Incomplete;
        return pending.Length != 0 ? EngineOutcome.RestartRequired : EngineOutcome.Removed;
    }

    sealed record EngineFacts(bool Tip, string[] ServicePaths, bool StartsAtLogin, bool Listed, string[] Files)
    {
        public bool Registered => Tip || ServicePaths.Length != 0;
    }

    static EngineFacts ReadEngineFacts(string[] originalFiles)
    {
        bool tip = false, startup = false, listed = false;
        var services = new List<string>();
        foreach (var view in EngineViews)
        {
            using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            using var classKey = machine.OpenSubKey($@"{ServiceClassKey}\InprocServer32");
            if (classKey is not null) services.Add(classKey.GetValue(null) as string ?? "");
            using var tipKey = machine.OpenSubKey(ServiceTipKey);
            tip |= tipKey is not null;
            using var run = machine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run");
            startup |= run?.GetValue("WeaselServer") is not null;
            using var uninstall = machine.OpenSubKey($@"{UninstallKey}\{EngineEntry}");
            listed |= uninstall is not null;
        }
        string[] paths = services.ToArray();
        return new(tip, paths, startup, listed,
            originalFiles.Concat(paths).Distinct(StringComparer.OrdinalIgnoreCase).Where(File.Exists).ToArray());
    }

    static bool CanClearStaleService(EngineFacts facts) =>
        facts.Registered && !facts.StartsAtLogin && !facts.Listed && facts.Files.Length == 0 &&
        facts.ServicePaths.All(path => IsSystemWeaselDll(path, EngineSystemDirectories()));

    internal static bool IsSystemWeaselDll(string path, IEnumerable<string> directories)
    {
        if (!Path.IsPathFullyQualified(path) || !string.Equals(Path.GetFileName(path), "weasel.dll", StringComparison.OrdinalIgnoreCase))
            return false;
        return directories.Any(directory => string.Equals(Path.GetDirectoryName(path), directory, StringComparison.OrdinalIgnoreCase));
    }

    static void ClearStaleService()
    {
        // Recheck with administrator privileges. Never unregister a still-installed engine
        // or an unexpected DLL registered under this CLSID.
        var facts = ReadEngineFacts(TrackedEngineFiles(FindEngineUninstaller()));
        if (!CanClearStaleService(facts)) return;
        foreach (var view in EngineViews)
        {
            using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            foreach (string path in new[] { ServiceClassKey, ServiceTipKey })
            {
                using var parent = machine.OpenSubKey(Path.GetDirectoryName(path)!, writable: true);
                parent?.DeleteSubKeyTree(Path.GetFileName(path), throwOnMissingSubKey: false);
            }
        }
    }

    static string[] PendingEngineFiles(string[] originals)
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager");
        string[] renamed = key?.GetValue("PendingFileRenameOperations") as string[] ?? [];
        return originals.Where(original => renamed.Any(item => IsPendingPath(item, original))).ToArray();
    }

    internal static bool IsPendingPath(string raw, string original)
    {
        string path = raw.TrimStart('!');
        if (path.StartsWith(@"\??\", StringComparison.Ordinal)) path = path[4..];
        // WeaselSetup may rename an in-use binary before scheduling deletion.
        if (path.Equals(original, StringComparison.OrdinalIgnoreCase)) return true;
        string prefix = original + ".old.";
        return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
            uint.TryParse(path.AsSpan(prefix.Length), out _);
    }
}

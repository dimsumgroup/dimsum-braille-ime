using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Dimsum;

static partial class WindowsSetup
{
    const string ManagerEntry = "DimsumGroup.DimsumInput.Manager_is1";
    internal const string EngineEntry = "Weasel";

    public static async Task HideInstallationEntriesAsync()
    {
        HideEntries(RegistryHive.CurrentUser);
        if (FindEntries(RegistryHive.LocalMachine).Any(entry => entry.IsEngine && !entry.Hidden))
            await RunAdminAsync("--hide-machine-entries");
    }

    public static void HideMachineEntries() => HideEntries(RegistryHive.LocalMachine);

    static void HideEntries(RegistryHive hive)
    {
        foreach (var entry in FindEntries(hive).Where(entry => entry.IsEngine))
        {
            if (entry.Hidden) continue;
            using var key = RegistryKey.OpenBaseKey(hive, entry.View);
            using var target = key.OpenSubKey($@"{UninstallKey}\{entry.Key}", writable: true);
            if (target is null) continue;
            target.SetValue("DimsumPreviousSystemComponent", target.GetValue("SystemComponent", -1)!);
            target.SetValue("SystemComponent", 1, RegistryValueKind.DWord);
        }
    }

    internal sealed record InstallEntry(RegistryHive Hive, RegistryView View, string Key, string Executable, bool Hidden, bool IsEngine);

    internal static IEnumerable<InstallEntry> FindEntries(RegistryHive hive)
    {
        string? engineRoot = Path.GetDirectoryName(Workspace.FindDeployer());
        foreach (var view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
        {
            using var root = RegistryKey.OpenBaseKey(hive, view);
            using var uninstall = root.OpenSubKey(UninstallKey);
            if (uninstall is null) continue;
            foreach (string name in uninstall.GetSubKeyNames())
            {
                if (name.Equals(ManagerEntry, StringComparison.OrdinalIgnoreCase)) continue;
                using var entry = uninstall.OpenSubKey(name);
                if (entry is null || ParseExecutable(entry.GetValue("UninstallString") as string) is not string executable)
                    continue;
                string? folder = Path.GetDirectoryName(executable);
                bool isEngine = name.Equals(EngineEntry, StringComparison.OrdinalIgnoreCase) &&
                    engineRoot is not null && string.Equals(folder, engineRoot, StringComparison.OrdinalIgnoreCase) &&
                    Path.GetFileName(executable).Equals("uninstall.exe", StringComparison.OrdinalIgnoreCase) &&
                    File.Exists(executable);
                bool isLegacy = MatchesLegacyEntry(name, entry.GetValue("DisplayName") as string,
                    entry.GetValue("Publisher") as string, entry.GetValue("DisplayVersion") as string, executable);
                if (isEngine || isLegacy)
                    yield return new(hive, view, name, executable, entry.GetValue("SystemComponent") is 1, isEngine);
            }
        }
    }

    internal static bool MatchesLegacyEntry(string key, string? display, string? publisher, string? version, string executable)
    {
        if (key.Equals(ManagerEntry, StringComparison.OrdinalIgnoreCase) ||
            !Regex.IsMatch(Path.GetFileName(executable), @"\Aunins[0-9]+\.exe\z", RegexOptions.IgnoreCase) ||
            !string.Equals(Path.GetFileName(Path.GetDirectoryName(executable)), "DimsumInput", StringComparison.OrdinalIgnoreCase))
            return false;
        if (key.Equals(Product + "_is1", StringComparison.OrdinalIgnoreCase))
            return true;
        return (display is Product or "點心粵語點字") && publisher == "Dimsum Group" &&
            Version.TryParse(version?.TrimStart('v', 'V'), out var parsed) && parsed < new Version(1, 3);
    }

    internal static string? ParseExecutable(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        var match = Regex.Match(command.Trim(), @"\A(?:""(?<file>[^""]+)""|(?<file>.+?\.exe))(?=\s|$)",
            RegexOptions.IgnoreCase);
        string path = match.Groups["file"].Value;
        return Path.IsPathFullyQualified(path) ? Path.GetFullPath(path) : null;
    }

    public static void LaunchUninstaller()
    {
        string? uninstaller = null;
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            using var root = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, view);
            using var key = root.OpenSubKey($@"{UninstallKey}\{ManagerEntry}");
            string? path = ParseExecutable(key?.GetValue("UninstallString") as string);
            if (path is not null && File.Exists(path) &&
                Regex.IsMatch(Path.GetFileName(path), @"\Aunins[0-9]+\.exe\z", RegexOptions.IgnoreCase) &&
                string.Equals(Path.GetDirectoryName(path), Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory), StringComparison.OrdinalIgnoreCase))
            {
                uninstaller = path;
                break;
            }
        }
        if (uninstaller is null) throw new IOException("未搵到點心管理器嘅移除程式。");
        var info = new ProcessStartInfo(uninstaller) { UseShellExecute = true };
        info.ArgumentList.Add("/SILENT");

        using var process = Process.Start(info) ?? throw new IOException("未能啟動移除程式。");
    }

}

using Microsoft.Win32;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Dimsum;

static class NvdaDictionaries
{
    internal static readonly string[] Names = ["characterDescriptions.dic", "symbols.dic", "cldr.dic"];
    static readonly string[] Locales = ["zh", "zh_CN", "zh_HK", "zh_TW"];
    static readonly RegistryView[] Views = [RegistryView.Registry64, RegistryView.Registry32];

    public static string? FindInstallation()
    {
        foreach (var view in Views)
        {
            using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            using var key = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\NVDA");
            if ((key?.GetValue("UninstallDirectory") ?? key?.GetValue("InstallLocation")) is string path &&
                Path.IsPathFullyQualified(path) && File.Exists(Path.Combine(path, "nvda.exe")))
                return path;
        }
        foreach (string root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) })
        {
            if (root.Length == 0) continue;
            string directory = Path.Combine(root, "NVDA");
            if (File.Exists(Path.Combine(directory, "nvda.exe"))) return directory;
        }
        return null;
    }

    internal static bool IsSameDirectory(string? left, string right) => left is not null &&
        string.Equals(Path.TrimEndingDirectorySeparator(left), Path.TrimEndingDirectorySeparator(right), StringComparison.OrdinalIgnoreCase);

    internal static string? InstalledConfigDirectory()
    {
        bool? useLocal = null;
        foreach (var view in Views)
        {
            using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            using var key = root.OpenSubKey(@"SOFTWARE\NVDA");
            if (key?.GetValue("configInLocalAppData") is not int enabled) continue;
            if (useLocal.HasValue && useLocal.Value != (enabled != 0)) return null;
            useLocal = enabled != 0;
        }
        var location = useLocal == true ? Environment.SpecialFolder.LocalApplicationData : Environment.SpecialFolder.ApplicationData;
        return Path.Combine(Environment.GetFolderPath(location), "nvda");
    }

    internal static string? ReadConfiguredLanguage(IEnumerable<string> lines)
    {
        bool general = false;
        foreach (string line in lines)
        {
            string text = line.Split('#', ';')[0].Trim();
            if (text.StartsWith('['))
            {
                general = text.Equals("[general]", StringComparison.OrdinalIgnoreCase);
                continue;
            }
            int separator = text.IndexOf('=');
            if (general && separator >= 0 &&
                text[..separator].Trim().Equals("language", StringComparison.OrdinalIgnoreCase))
                return text[(separator + 1)..].Trim().Trim('"', '\'');
        }
        return null;
    }

    internal const string BackupFolder = "NVDA舊配詞表備份";

    public static void CheckExportDestination(string destination)
    {
        string path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destination));
        foreach (string? nvda in new[] { NvdaSession.Find()?.Directory, FindInstallation() })
        {
            if (nvda is null) continue;
            string relative = Path.GetRelativePath(nvda, path);
            if (!Path.IsPathRooted(relative) && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar))
                throw new IOException("請揀 NVDA 安裝目錄以外嘅資料夾。");
        }
    }

    internal static void Export(string destination, byte[][] contents, bool overwrite)
    {
        string stage = Path.Combine(destination, ".dimsum-export-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        var replaced = new List<(string Target, string? Original)>();
        try
        {
            for (int index = 0; index < Names.Length; index++)
                File.WriteAllBytes(Path.Combine(stage, Names[index]), contents[index]);
            foreach (string name in Names)
            {
                string prepared = Path.Combine(stage, name);
                string target = Path.Combine(destination, name);
                if (File.Exists(target) && overwrite)
                {
                    string original = prepared + ".original";
                    File.Replace(prepared, target, original, ignoreMetadataErrors: true);
                    replaced.Add((target, original));
                }
                else
                {
                    File.Move(prepared, target);
                    replaced.Add((target, null));
                }
            }
        }
        catch
        {
            foreach (var file in Enumerable.Reverse(replaced))
            {
                try
                {
                    if (file.Original is null) File.Delete(file.Target);
                    else File.Move(file.Original, file.Target, overwrite: true);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    throw new IOException($"未能復原原有檔案。復原資料夾喺「{stage}」。{error.Message}", error);
                }
            }
            Directory.Delete(stage, recursive: true);
            throw;
        }
        Directory.Delete(stage, recursive: true);
    }

    public static (string Location, string[] Missing)? BackupCurrent(string data, string destination)
    {
        var running = NvdaSession.Find();
        string? installed = FindInstallation();
        string? resources = running is null ? installed : running.Directory;
        if (resources is null) return null;
        var active = running?.ReadProfile(data);
        string? profile = active?.ConfigDirectory ?? (IsSameDirectory(installed, resources)
            ? InstalledConfigDirectory() : Path.Combine(resources, "userConfig"));
        string? language = active?.DictionaryLanguage ?? active?.Language;
        if (language is null && profile is not null)
        {
            try
            {
                string path = Path.Combine(profile, "nvda.ini");
                if (File.Exists(path)) language = ReadConfiguredLanguage(File.ReadLines(path)) ?? "Windows";
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
        if (language?.Equals("Windows", StringComparison.OrdinalIgnoreCase) == true)
            language = CultureInfo.CurrentUICulture.Name;
        language = language?.Trim().Replace('-', '_');
        if (language is null || !Regex.IsMatch(language, @"\A[a-zA-Z]{2,8}(?:_[a-zA-Z0-9]{2,8})*\z")) return null;
        return BackupDictionaries(resources, language, destination);
    }

    internal static (string Location, string[] Missing) BackupDictionaries(string resources, string language, string destination)
    {
        string backup = Path.Combine(destination, BackupFolder);
        if (Directory.Exists(backup))
            backup = Path.Combine(backup, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N"));
        string[] languages = new[] { language, language.Split('_')[0], "en" }.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var missing = new List<string>();
        foreach (string name in Names)
        {
            string? locale = languages.FirstOrDefault(locale => File.Exists(Path.Combine(resources, "locale", locale, name)));
            if (locale is not null)
            {
                try
                {
                    string source = Path.Combine(resources, "locale", locale, name);
                    Workspace.WriteAtomic(Path.Combine(backup, locale, name), File.ReadAllBytes(source));
                    continue;
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            }
            missing.Add($"{name}（{locale ?? language}）");
        }
        return (backup, [.. missing]);
    }

    internal static void BackupInstalledDictionaries(string? install, string backup)
    {
        if (install is null) return;

        foreach (string language in Locales)
        foreach (string name in Names)
        {
            string target = Path.Combine(backup, language, name);
            if (File.Exists(target)) continue;

            byte[] contents;
            try { contents = File.ReadAllBytes(Path.Combine(install, "locale", language, name)); }
            catch (FileNotFoundException) { continue; }
            catch (DirectoryNotFoundException) { continue; }
            Workspace.WriteAtomic(target, contents);
        }
    }

    internal static byte[][] ReadBundle(string source)
    {
        byte[][] contents = Names.Select(name => File.ReadAllBytes(Path.Combine(source, name))).ToArray();
        Validate(contents);
        return contents;
    }

    internal static void Validate(byte[][] contents)
    {
        for (int index = 0; index < contents.Length; index++)
        {
            string text = new System.Text.UTF8Encoding(false, true).GetString(contents[index]).TrimStart('\uFEFF');
            bool valid = index == 0
                ? text.Split('\n').Any(line => line.StartsWith("過\t", StringComparison.Ordinal))
                : text.Contains("\nsymbols:", StringComparison.Ordinal);
            if (!valid) throw new IOException($"配詞檔案格式無效（{Names[index]}）。");
        }
    }
}

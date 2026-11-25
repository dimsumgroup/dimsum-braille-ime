using Microsoft.Win32;
using System.Text;
using System.Text.Json;
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

    static (string Profile, string? Locale) SelectDestination(NvdaSession? running, string data, string resources)
    {
        var active = running?.ReadProfile(data);
        string? profile = active?.ConfigDirectory;
        if (profile is null && running?.Directory is { } directory && !IsSameDirectory(FindInstallation(), directory))
            profile = Path.Combine(directory, "userConfig");
        profile ??= InstalledConfigDirectory() ?? throw new IOException("未能確認 NVDA 用家設定資料夾。");
        string? language = active?.Language;
        if (language is null)
        {
            try
            {
                string path = Path.Combine(profile, "nvda.ini");
                if (File.Exists(path)) language = ReadConfiguredLanguage(File.ReadLines(path));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return (profile, ResolveLocale(resources, language));
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

    internal static string? ResolveLocale(string install, string? language)
    {
        string? requested = language?.Trim().Replace('-', '_');
        return Locales.FirstOrDefault(locale => locale.Equals(requested, StringComparison.OrdinalIgnoreCase) &&
            Directory.Exists(Path.Combine(install, "locale", locale)));
    }

    static readonly UTF8Encoding Utf8 = new(false, true);

    public static (bool Copied, string Scope) CopyToProfile(string data, string source)
    {
        var running = NvdaSession.Find();
        string resources = running?.Directory ?? FindInstallation() ?? throw new IOException("未搵到 NVDA。");
        var destination = SelectDestination(running, data, resources);
        bool copied = ApplyToProfile(destination.Profile, resources, ReadBundle(source), destination.Locale);
        return (copied, destination.Locale is null ? "已存在嘅中文語言" : destination.Locale);
    }

    internal static bool ApplyToProfile(string profile, string resources, byte[][] contents, string? locale)
    {
        var languages = Targets(resources, locale).Select(target => target.BackupName.Split('.')[0]).Distinct().ToArray();
        using var mutex = new Mutex(false, @"Local\DimsumNvdaProfileDictionaries");
        Acquire(mutex);
        try
        {
            bool changed = false;
            foreach (string language in languages)
            {
                string directory = Path.Combine(profile, "DimsumPronunciations", language);
                string active = Path.Combine(directory, "active.txt");
                RejectRedirects(active);
                string? previous = File.Exists(active) ? File.ReadAllText(active).Trim() : null;
                bool same = Guid.TryParseExact(previous, "N", out _) &&
                    Names.Select((name, index) => (Name: name, Index: index)).All(file =>
                    {
                        string path = Path.Combine(directory, previous!, file.Name);
                        RejectRedirects(path);
                        return File.Exists(path) && File.ReadAllBytes(path).AsSpan().SequenceEqual(contents[file.Index]);
                    });
                if (same) continue;
                string revision = Guid.NewGuid().ToString("N");
                string target = Path.Combine(directory, revision);
                for (int index = 0; index < Names.Length; index++)
                    Workspace.WriteAtomic(Path.Combine(target, Names[index]), contents[index]);
                Workspace.WriteAtomic(active, Utf8.GetBytes(revision));
                changed = true;
            }
            return changed;
        }
        finally { mutex.ReleaseMutex(); }
    }

    public static (int Restored, int Missing) RestoreInstalled()
    {
        string install = FindInstallation() ?? throw new IOException("未搵到已安裝嘅 NVDA。");
        using var mutex = new Mutex(false, @"Global\DimsumNvdaDictionaries");
        Acquire(mutex);
        try { return RestoreInstalled(install); }
        finally { mutex.ReleaseMutex(); }
    }

    static void Acquire(Mutex mutex)
    {
        try
        {
            if (!mutex.WaitOne(TimeSpan.FromSeconds(30)))
                throw new IOException("另一個配詞操作仍在執行。");
        }
        catch (AbandonedMutexException) { }
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

    static (string Path, int File, string BackupName)[] Targets(string install, string? locale)
    {
        string localeRoot = Path.Combine(install, "locale");
        var targets = (from language in Locales
                       where locale is null || language == locale
                       let folder = Path.Combine(localeRoot, language)
                       where Directory.Exists(folder)
                       from index in Enumerable.Range(0, Names.Length)
                       select (Path.Combine(folder, Names[index]), index, language + "." + Names[index])).ToArray();
        if (targets.Length == 0)
            throw new IOException(locale is null ? "NVDA 入面搵唔到繁體或簡體中文配詞目錄。" : $"未搵到已選嘅 NVDA 語言資料夾（{locale}）。");
        return targets;
    }

    internal static (int Restored, int Missing) RestoreInstalled(string install)
    {
        string backups = Path.Combine(install, "DimsumDictionaryBackups");
        string journal = Path.Combine(backups, "pending");
        RejectRedirects(journal);
        Restore(journal, install);
        var targets = Targets(install, null);
        var histories = new List<(string Directory, Dictionary<string, bool> Files)>();
        if (Directory.Exists(backups))
        {
            foreach (string directory in Directory.EnumerateDirectories(backups).OrderDescending(StringComparer.Ordinal))
            {
                if (!Regex.IsMatch(Path.GetFileName(directory), @"\A[0-9]{8}-[0-9]{6}-[0-9a-f]{32}\z")) continue;
                string marker = Path.Combine(directory, "originals.json");
                RejectRedirects(marker);
                if (!File.Exists(marker)) continue;
                var originals = JsonSerializer.Deserialize<Dictionary<string, bool>>(File.ReadAllBytes(marker));
                if (originals is not null) histories.Add((directory, originals));
            }
        }

        var changes = new List<(string Path, string BackupName, byte[] Current, byte[]? Replacement)>();
        int missing = 0;
        foreach (var target in targets)
        {
            RejectRedirects(target.Path);
            if (!File.Exists(target.Path)) continue;
            byte[] current = File.ReadAllBytes(target.Path);
            if (!IsOldDimsumFile(current)) continue;
            bool found = false;
            foreach (var history in histories)
            {
                if (!history.Files.TryGetValue(target.BackupName, out bool existed)) continue;
                byte[]? replacement = null;
                if (existed)
                {
                    string saved = Path.Combine(history.Directory, target.BackupName);
                    RejectRedirects(saved);
                    if (!File.Exists(saved)) continue;
                    replacement = File.ReadAllBytes(saved);
                    if (IsOldDimsumFile(replacement)) continue;
                }
                changes.Add((target.Path, target.BackupName, current, replacement));
                found = true;
                break;
            }
            if (!found) missing++;
        }

        if (changes.Count == 0) return (0, missing);
        foreach (var change in changes)
            Workspace.WriteAtomic(Path.Combine(journal, change.BackupName), change.Current);
        Workspace.WriteAtomic(Path.Combine(journal, "originals.json"),
            JsonSerializer.SerializeToUtf8Bytes(changes.ToDictionary(change => change.BackupName, _ => true)));
        try
        {
            foreach (var change in changes)
            {
                if (change.Replacement is null) File.Delete(change.Path);
                else Workspace.WriteAtomic(change.Path, change.Replacement);
            }
            Directory.Move(journal, Path.Combine(backups, "restored-" + Guid.NewGuid().ToString("N")));
        }
        catch
        {
            Restore(journal, install);
            throw;
        }
        return (changes.Count, missing);
    }

    static bool IsOldDimsumFile(byte[] contents)
    {
        string header = Encoding.UTF8.GetString(contents.AsSpan(0, Math.Min(contents.Length, 1024)));
        return header.Contains("dimsumbrl.work", StringComparison.OrdinalIgnoreCase) &&
            header.Contains("Dimsum Group", StringComparison.Ordinal);
    }

    static void Restore(string journal, string install)
    {
        if (!Directory.Exists(journal)) return;
        string marker = Path.Combine(journal, "originals.json");
        RejectRedirects(marker);
        if (File.Exists(marker))
        {
            var originals = JsonSerializer.Deserialize<Dictionary<string, bool>>(File.ReadAllBytes(marker))!;
            // A pending copy from an older version may include other locales.
            foreach (var target in Targets(install, null))
            {
                if (!originals.TryGetValue(target.BackupName, out bool existed)) continue;
                RejectRedirects(target.Path);
                string saved = Path.Combine(journal, target.BackupName);
                if (existed)
                {
                    RejectRedirects(saved);
                    Workspace.WriteAtomic(target.Path, File.ReadAllBytes(saved));
                }
                else File.Delete(target.Path);
            }
        }
        string recovered = journal + "-recovered-" + Guid.NewGuid().ToString("N");
        Directory.Move(journal, recovered);
    }

    static void RejectRedirects(string path)
    {
        for (string? part = path; part is not null; part = Path.GetDirectoryName(part))
            if ((File.Exists(part) || Directory.Exists(part)) &&
                (File.GetAttributes(part) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("NVDA 配詞目錄包含重新導向。");
    }
}

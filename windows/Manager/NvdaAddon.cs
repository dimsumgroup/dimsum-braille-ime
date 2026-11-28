using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace Dimsum;

enum NvdaAddonOffer { None, Install, Update }

static class NvdaAddon
{
    const string Name = "dimsumCandidates";
    const string PluginFile = "globalPlugins/dimsumCandidates.py";
    static readonly Lazy<(Version Version, byte[] PluginHash)> Bundled = new(ReadBundle);
    public static Version BundledVersion => Bundled.Value.Version;
    static string Bundle => Path.Combine(AppContext.BaseDirectory, "assets", Name + ".nvda-addon");

    public static bool InstallIfOffered(string data)
    {
        if (!ShouldOffer(data) || NvdaSession.Find() is not { } active) return false;

        Install(active.Directory);
        return true;
    }

    public static void InstallManually() => Install(NvdaSession.Find()?.Directory);

    static void Install(string? running)
    {
        if (running is not null)
        {
            string helper = Path.Combine(running, "nvda_slave.exe");
            if (File.Exists(helper))
            {
                // This is the same action as NVDA's .nvda-addon file association.
                var command = new ProcessStartInfo(helper) { UseShellExecute = false, WorkingDirectory = running };
                command.ArgumentList.Add("addons_installAddonPackage");
                command.ArgumentList.Add(Bundle);
                using var process = Process.Start(command) ?? throw new IOException("未能開啟 NVDA 附加元件安裝檔。");
                return;
            }
        }
        Program.Open(Bundle);
    }

    public static bool ShouldOffer(string data) => GetOffer(data) != NvdaAddonOffer.None;

    public static NvdaAddonOffer GetOffer(string data)
    {
        var running = NvdaSession.Find();
        if (running is null) return NvdaAddonOffer.None;
        string? profile = running.ReadProfile(data)?.ConfigDirectory;
        if (profile is null && running.Directory is { } directory)
        {
            string? installed = NvdaDictionaries.FindInstallation();
            profile = NvdaDictionaries.IsSameDirectory(installed, directory)
                ? NvdaDictionaries.InstalledConfigDirectory()
                : Path.Combine(directory, "userConfig");
        }
        if (profile is null) return NvdaAddonOffer.None;

        _ = BundledVersion;
        bool hasAddon = false;
        foreach (string folder in AddonFolders(profile))
        {
            if (ReadOwnedVersion(folder) is not { } version) continue;
            hasAddon = true;
            if (IsCurrentOrNewer(folder, version)) return NvdaAddonOffer.None;
        }
        return hasAddon ? NvdaAddonOffer.Update : NvdaAddonOffer.Install;
    }

    static (Version Version, byte[] PluginHash) ReadBundle()
    {
        using var addon = ZipFile.OpenRead(Bundle);
        using var manifestFile = addon.GetEntry("manifest.ini")?.Open()
            ?? throw new IOException("內附 NVDA 附加組件缺少版本資料。");
        using var manifest = new StreamReader(manifestFile);
        var version = ReadManifestVersion(manifest.ReadToEnd());
        if (version is null || version != typeof(NvdaAddon).Assembly.GetName().Version)
            throw new IOException("內附 NVDA 附加組件版本同管理器唔一致。");
        using var plugin = addon.GetEntry(PluginFile)?.Open()
            ?? throw new IOException("內附 NVDA 附加組件缺少報讀模組。");
        return (version, SHA256.HashData(plugin));
    }

    static bool IsCurrentOrNewer(string folder, Version installed)
    {
        if (installed < BundledVersion) return false;
        if (installed > BundledVersion) return true;

        // A previous test installer used the same public version number.
        // Confirm the installed or pending plugin has the updated code.
        string plugin = Path.Combine(folder, PluginFile);
        if (!File.Exists(plugin) || !OrdinaryPath(plugin)) return false;
        using var file = File.OpenRead(plugin);
        return SHA256.HashData(file).AsSpan().SequenceEqual(Bundled.Value.PluginHash);
    }

    public static bool Remove(string data)
    {
        var running = NvdaSession.Find();
        bool restart = false;
        var roots = ConfigRoots(data, running?.Directory);
        if (running?.ReadProfile(data)?.ConfigDirectory is { } profile)
            roots = roots.Append(profile).Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (string root in roots)
            restart |= RemoveFromProfile(root, running is not null);
        return restart;
    }

    static IEnumerable<string> ConfigRoots(string data, string? running)
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "nvda"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "nvda")
        };
        if (running is not null) roots.Add(Path.Combine(running, "userConfig"));
        string recorded = Path.Combine(data, "nvda-addon-profiles");
        if (Directory.Exists(recorded))
        {
            foreach (string file in Directory.EnumerateFiles(recorded, "*.path"))
            {
                if (!OrdinaryPath(file)) continue;
                string path = File.ReadAllText(file);
                if (Path.IsPathFullyQualified(path)) roots.Add(Path.GetFullPath(path));
            }
        }
        return roots;
    }

    static IEnumerable<string> AddonFolders(string root)
    {
        yield return Path.Combine(root, "addons", Name);
        yield return Path.Combine(root, "addons", Name + ".pendingInstall");
    }

    internal static bool RemoveFromProfile(string root, bool running)
    {
        bool restart = false;
        foreach (string source in AddonFolders(root))
        {
            if (ReadOwnedVersion(source) is null) continue;
            string removed = Path.Combine(Path.GetDirectoryName(source)!, Name + "-" + Guid.NewGuid().ToString("N") + ".delete");
            // NVDA ignores .delete folders and already knows how to clean them up at startup.
            Directory.Move(source, removed);
            try { Directory.Delete(removed, recursive: true); }
            catch (IOException) { restart = true; }
            catch (UnauthorizedAccessException) { restart = true; }
            restart |= running;
        }
        return restart;
    }

    static Version? ReadOwnedVersion(string path)
    {
        string manifest = Path.Combine(path, "manifest.ini");
        if (!File.Exists(manifest) || !OrdinaryPath(manifest)) return null;
        return ReadManifestVersion(File.ReadAllText(manifest));
    }

    internal static Version? ReadManifestVersion(string text)
    {
        if (Read("name") != Name || Read("author") != "Dimsum Group") return null;
        return Version.TryParse(Read("version"), out var version)
            ? new Version(version.Major, version.Minor, Math.Max(0, version.Build), Math.Max(0, version.Revision)) : null;

        string Read(string key) => Regex.Match(text, $@"(?m)^{key}[ \t]*=[ \t]*([^\r\n]+)")
            .Groups[1].Value.Trim().Trim('"', '\'');
    }

    static bool OrdinaryPath(string path)
    {
        for (string? part = path; part is not null; part = Path.GetDirectoryName(part))
            if ((Directory.Exists(part) || File.Exists(part)) && (File.GetAttributes(part) & FileAttributes.ReparsePoint) != 0)
                return false;
        return true;
    }
}

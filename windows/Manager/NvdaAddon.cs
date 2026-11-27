using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Dimsum;

static class NvdaAddon
{
    const string Name = "dimsumCandidates";
    static string Bundle => Path.Combine(AppContext.BaseDirectory, "assets", Name + ".nvda-addon");

    public static void Install()
    {
        string? running = NvdaSession.Find()?.Directory;
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

    public static bool ShouldOffer(string data)
    {
        var running = NvdaSession.Find();
        if (running is null) return false;
        string? profile = running.ReadProfile(data)?.ConfigDirectory;
        if (profile is null && running.Directory is { } directory)
        {
            string? installed = NvdaDictionaries.FindInstallation();
            profile = NvdaDictionaries.IsSameDirectory(installed, directory)
                ? NvdaDictionaries.InstalledConfigDirectory()
                : Path.Combine(directory, "userConfig");
        }
        return profile is not null && !AddonFolders(profile).Any(folder => ReadOwnedVersion(folder) is not null);
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

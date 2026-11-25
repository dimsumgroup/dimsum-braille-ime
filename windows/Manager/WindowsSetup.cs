using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Dimsum;

static partial class WindowsSetup
{
    static readonly string? EngineHash = typeof(WindowsSetup).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
        .FirstOrDefault(attribute => attribute.Key == "BundledEngineSha256")?.Value;
    const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
    const string Product = "點心粵語點字輸入法";
    public const ushort HongKong = 0x0c04;
    const uint InputProcessorProfile = 1;
    static readonly Guid Service = new("A3F4CDED-B1E9-41EE-9CA6-7B4D0DE6CB0A");
    static readonly Guid EngineProfile = new("3D02CAB6-2B8E-4781-BA20-1C9267529467");
    static readonly Guid Profile = new("8CF77989-1DE6-4846-ACF9-96054DBD642B");
    const uint RemoveLayout = 1;

    public static async Task<bool> InstallEngineAsync()
    {
        if (Workspace.FindDeployer() is not null)
        {
            await HideInstallationEntriesAsync();
            return false;
        }
        await RunAdminAsync("--install-engine");
        return true;
    }

    public static async Task InstallBundledEngineAsync(bool forceUpgrade = false)
    {
        if (!forceUpgrade && Workspace.FindDeployer() is not null)
        {
            HideMachineEntries();
            return;
        }
        string installer = Path.Combine(AppContext.BaseDirectory, "assets", "weasel-installer.exe");
        if (!File.Exists(installer) || Workspace.HashFile(installer) != EngineHash)
            throw new IOException("內附輸入引擎檔案校驗失敗。");
        await RunProcessAsync(installer, elevate: false, "/S", "/T", "/L=1028");
        string? server = EngineServerPath();
        if (server is null || Workspace.FindDeployer() is null)
            throw new IOException("未搵到已安裝嘅輸入引擎。請再試步驟 01。");
        var version = FileVersionInfo.GetVersionInfo(server);
        if (version.FileMajorPart != 0 || version.FileMinorPart != 17 || version.FileBuildPart != 4)
            throw new IOException("未能確認內附輸入引擎版本已安裝。");
        HideMachineEntries();
    }

    static string? EngineServerPath()
    {
        string? folder = Path.GetDirectoryName(Workspace.FindDeployer());
        if (folder is null) return null;
        string server = Path.Combine(folder, "WeaselServer.exe");
        return File.Exists(server) ? server : null;
    }

    public static async Task<bool> StopEngineAsync()
    {
        if (EngineServerPath() is not { } server) return false;
        using var current = Process.GetCurrentProcess();
        int session = current.SessionId;
        if (!EngineIsRunning(server, session)) return false;

        await RunProcessAsync(server, elevate: false, "/quit");
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
        for (int attempt = 0; EngineIsRunning(server, session); attempt++)
        {
            if (attempt >= 40 || !await timer.WaitForNextTickAsync())
                throw new IOException($"輸入引擎仍在執行，未開始更新或移除。{Environment.NewLine}{server}");
        }
        return true;
    }

    static bool EngineIsRunning(string server, int session)
    {
        var processes = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(server));
        try
        {
            foreach (var process in processes)
            {
                try
                {
                    if (process.SessionId != session || process.HasExited) continue;
                    string? path = process.MainModule?.FileName;
                    if (!process.HasExited && (path is null || string.Equals(path, server, StringComparison.OrdinalIgnoreCase)))
                        return true;
                }
                catch (InvalidOperationException) { }
                catch (Win32Exception) { return true; }
            }
            return false;
        }
        finally { foreach (var process in processes) process.Dispose(); }
    }

    public static void StartAvailableEngine()
    {
        using var current = Process.GetCurrentProcess();
        if (EngineServerPath() is { } server && !EngineIsRunning(server, current.SessionId))
            Process.Start(new ProcessStartInfo(server) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(server)! })?.Dispose();
    }

    public static ushort PreferredLanguage()
    {
        var current = InputLanguage.CurrentInputLanguage.Culture;
        if (SupportedCulture(current)) return (ushort)current.KeyboardLayoutId;
        var ui = CultureInfo.CurrentUICulture;
        return SupportedCulture(ui) ? (ushort)ui.KeyboardLayoutId : HongKong;
    }

    static bool SupportedCulture(CultureInfo culture) =>
        !culture.IsNeutralCulture && culture.KeyboardLayoutId is > 0 and < 0x10000 &&
        culture.TwoLetterISOLanguageName is "en" or "zh";

    public static bool IsOurProfile(ushort language)
    {
        using var key = OpenProfile(language);
        return (key?.GetValue("Description") as string) == Product;
    }

    public static void RegisterProfile(ushort language)
    {
        string? tsfPath = RegisteredServicePath();
        if (tsfPath is null)
            throw new IOException("未搵到可以登記點心名稱嘅輸入服務。");

        using var key = OpenProfile(language);
        if ((key?.GetValue("Description") as string) == Product) return;
        var type = Type.GetTypeFromCLSID(new Guid("33C53A50-F456-4884-B049-85FD643ECFED"), throwOnError: true)!;
        var manager = (IInputProcessorProfiles)Activator.CreateInstance(type)!;
        try
        {
            nint substitute = 0;
            bool enabled = false;
            if (manager.GetProfile(InputProcessorProfile, language, Service, Profile, 0, out var existing) >= 0)
            {
                substitute = existing.Substitute;
                enabled = (existing.Flags & 0x2) != 0;
            }
            Marshal.ThrowExceptionForHR(manager.RegisterProfile(Service, language, Profile, Product, (uint)Product.Length,
                tsfPath, (uint)tsfPath.Length, 0, substitute, 0, enabled, 0));
        }
        finally { Marshal.FinalReleaseComObject(manager); }
    }

    public static void RegisterWithFallback(ushort language)
    {
        RegisterProfile(HongKong);
        if (language != HongKong)
        {
            try { RegisterProfile(language); }
            catch (COMException) { /* The Hong Kong profile remains available. */ }
        }
    }

    public static IEnumerable<string> UserProfiles(bool legacyOnly = false)
    {
        using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var profiles = root.OpenSubKey($@"SOFTWARE\Microsoft\CTF\TIP\{Service:B}\LanguageProfile");
        if (profiles is null) yield break;
        foreach (string name in profiles.GetSubKeyNames())
        {
            if (!name.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ||
                !ushort.TryParse(name.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ushort language))
                continue;
            using var entry = profiles.OpenSubKey(name);
            using var current = entry?.OpenSubKey(Profile.ToString("B"));
            if (!legacyOnly && current is not null)
                yield return $"{language:X4}:{Service:B}{Profile:B}";
            using var old = entry?.OpenSubKey(EngineProfile.ToString("B"));
            if (old?.GetValue("Description") as string == Product)
                yield return $"{language:X4}:{Service:B}{EngineProfile:B}";
        }
    }

    public static void RemoveUserProfiles(IEnumerable<string> profiles)
    {
        foreach (string profile in profiles) InstallLayoutOrTip(profile, RemoveLayout);
    }

    static string? RegisteredServicePath()
    {
        using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = baseKey.OpenSubKey($@"SOFTWARE\Classes\CLSID\{Service:B}\InprocServer32");
        string? path = key?.GetValue(null) as string;
        return path is not null && Path.IsPathFullyQualified(path) && File.Exists(path) ? path : null;
    }

    static RegistryKey? OpenProfile(ushort language)
    {
        using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        return baseKey.OpenSubKey($@"SOFTWARE\Microsoft\CTF\TIP\{Service:B}\LanguageProfile\0x{language:x8}\{Profile:B}");
    }

    [DllImport("input.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool InstallLayoutOrTip(string profiles, uint flags);

    [DllImport("input.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool SetDefaultLayoutOrTip(string profiles, uint flags);

    public static bool EnableProfileForUser(ushort language, bool makeDefault)
    {
        string profile = $"{language:X4}:{Service:B}{Profile:B}";
        if (!InstallLayoutOrTip(profile, 0)) return false;
        if (makeDefault && !SetDefaultLayoutOrTip(profile, 0))
            throw new IOException("已加入點心。Windows 未能將點心設為預設輸入法。");
        return true;
    }

    public static Task RunAdminAsync(params string[] arguments) => RunElevatedAsync(Environment.ProcessPath!, arguments);

    static Task RunElevatedAsync(string executable, params string[] arguments) =>
        RunProcessAsync(executable, elevate: true, arguments);

    static Task RunProcessAsync(string executable, bool elevate, params string[] arguments) =>
        RunProcessWithExitCodeAsync(executable, elevate, false, arguments);

    static async Task<int> RunProcessWithExitCodeAsync(string executable, bool elevate, bool allowRestart, params string[] arguments)
    {
        var info = new ProcessStartInfo(executable) { UseShellExecute = true };
        if (elevate) info.Verb = "runas";
        foreach (string argument in arguments) info.ArgumentList.Add(argument);
        Process process;
        try { process = Process.Start(info) ?? throw new IOException("未能啟動操作。"); }
        catch (Win32Exception error) when (error.NativeErrorCode == 1223)
        {
            throw new IOException("未獲管理員授權。你可以稍後喺管理器再試。", error);
        }
        catch (Win32Exception error)
        {
            throw new IOException($"未能啟動 {executable}。Windows 錯誤 {error.NativeErrorCode}：{error.Message}", error);
        }
        using (process)
        {
            await process.WaitForExitAsync();
            if (process.ExitCode != 0 && !(allowRestart && process.ExitCode == 3))
                throw new IOException($"{Path.GetFileName(executable)} 操作未完成（錯誤 {process.ExitCode}）。");
            return process.ExitCode;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct TsfProfile
    {
        uint type;
        ushort language;
        Guid clsid, guid, category;
        public nint Substitute;
        uint caps;
        nint layout;
        public uint Flags;
    }

    [ComImport, Guid("71c6e74c-0f28-11d8-a82a-00065b84435c"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IInputProcessorProfiles
    {
        [PreserveSig] int ActivateProfile(uint type, ushort language, in Guid clsid, in Guid profile, nint layout, uint flags);
        [PreserveSig] int DeactivateProfile(uint type, ushort language, in Guid clsid, in Guid profile, nint layout, uint flags);
        [PreserveSig] int GetProfile(uint type, ushort language, in Guid clsid, in Guid profile, nint layout, out TsfProfile value);
        [PreserveSig] int EnumProfiles(ushort language, out nint enumerator);
        [PreserveSig] int ReleaseInputProcessor(in Guid clsid, uint flags);
        [PreserveSig]
        int RegisterProfile(in Guid clsid, ushort language, in Guid profile,
            [MarshalAs(UnmanagedType.LPWStr)] string description, uint descriptionLength,
            [MarshalAs(UnmanagedType.LPWStr)] string icon, uint iconLength, uint iconIndex,
            nint substitute, uint preferredLayout, [MarshalAs(UnmanagedType.Bool)] bool enabled, uint flags);
    }
}

using Microsoft.Win32;
using System.Diagnostics;

namespace Dimsum;

static class Program
{
    public const string Title = "點心粵語點字輸入法管理器";
    public const string Website = "http://dimsumbrl.work/";
    public static string SessionMutexName(string processKind) =>
        $@"Local\{processKind}." + System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value;

    [STAThread]
    static void Main(string[] args)
    {
        if (args.Length > 0 && args[0] is "--restore-nvda-dictionaries" or "--finish-setup" or "--hide-machine-entries"
            or "--install-engine" or "--after-install" or "--remove-components" or "--prepare-legacy-engine"
            or "--finish-legacy-cleanup" or "--remove-engine-machine" or "--retire-legacy-on-removal"
            or "--check-nvda-addon" or "--install-nvda-addon")
        {
            try
            {
                switch (args[0])
                {
                    case "--restore-nvda-dictionaries" when args.Length == 1:
                        var restore = NvdaDictionaries.RestoreInstalled();
                        string result = restore.Restored > 0 ? $"已從備份還原 {restore.Restored} 份 NVDA 配詞檔。請重新啟動 NVDA。" :
                            "未有需要由備份還原嘅 NVDA 配詞檔。";
                        if (restore.Missing > 0)
                            result += $"{Environment.NewLine}{restore.Missing} 份檔案搵唔到原有備份。可以用原廠安裝程式修復 NVDA。";
                        MessageBox.Show(result, Title);
                        break;
                    case "--check-nvda-addon" when args.Length == 1:
                        string data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DimsumInput");
                        if (NvdaAddon.ShouldOffer(data)) Environment.ExitCode = 6;
                        break;
                    case "--install-nvda-addon" when args.Length == 1:
                        NvdaAddon.Install();
                        break;
                    case "--finish-setup" when args.Length == 2 && ushort.TryParse(args[1], out ushort language):
                        WindowsSetup.RegisterWithFallback(language);
                        break;
                    case "--hide-machine-entries":
                        WindowsSetup.HideMachineEntries();
                        break;
                    case "--install-engine":
                        WindowsSetup.InstallBundledEngineAsync().GetAwaiter().GetResult();
                        break;
                    case "--prepare-legacy-engine" when args.Length == 3 && ushort.TryParse(args[2], out ushort detected):
                        MachineLegacyUpgrade.PrepareEngineAsync(args[1]).GetAwaiter().GetResult();
                        try { WindowsSetup.RegisterWithFallback(detected); }
                        catch (Exception error)
                        {
                            throw new IOException($"「登記新版輸入法」失敗。{Environment.NewLine}{error.Message}（0x{error.HResult:X8}）", error);
                        }
                        break;
                    case "--finish-legacy-cleanup" when args.Length == 2:
                        MachineLegacyUpgrade.FinalizeCleanup(args[1]);
                        break;
                    case "--retire-legacy-on-removal" when args.Length == 2:
                        MachineLegacyUpgrade.RetireOldInstallation(args[1]);
                        break;
                    case "--remove-engine-machine" when args.Length is 1 or 2:
                        if (args.Length == 2) MachineLegacyUpgrade.PauseOldUpdater(args[1]);
                        var engineOutcome = WindowsSetup.RemoveEngineMachineAsync().GetAwaiter().GetResult();
                        if (args.Length == 2) MachineLegacyUpgrade.RetireOldInstallation(args[1]);
                        if (engineOutcome == WindowsSetup.EngineOutcome.RestartRequired)
                            Environment.ExitCode = 3;
                        break;
                    case "--after-install":
                        var installedWorkspace = new Workspace();
                        installedWorkspace.ConfigureStartup();
                        if (new LegacyUpgrade(installedWorkspace).NeedsUpgrade())
                        {
                            installedWorkspace.StartSound();
                            break;
                        }
                        if (installedWorkspace.Settings.Initialized)
                        {
                            installedWorkspace.Save();
                            installedWorkspace.StartSound();
                            WindowsSetup.HideInstallationEntriesAsync().GetAwaiter().GetResult();
                        }
                        break;
                    case "--remove-components" when args.Length == 2 && args[1] is "keep-engine" or "remove-engine":
                        var removal = new Workspace().RemoveAsync(args[1] == "remove-engine").GetAwaiter().GetResult();
                        bool restartEngine = removal.Engine == WindowsSetup.EngineOutcome.RestartRequired;
                        Environment.ExitCode = (restartEngine, removal.RestartNvda) switch
                        {
                            (true, true) => 5,
                            (false, true) => 4,
                            (true, false) => 3,
                            _ => 0
                        };
                        break;
                    default:
                        Environment.ExitCode = 2;
                        break;
                }
            }
            catch (Exception error)
            {
                Environment.ExitCode = 1;
                if (args[0] is not ("--after-install" or "--check-nvda-addon"))
                    MessageBox.Show(error.Message, Title);
            }
            return;
        }

        // An old login command can still be queued when an upgrade finishes.
        if (args.Contains("--boot")) return;

        bool sound = args.Contains("--sound");
        string processKind = sound ? "DimsumSound" : "DimsumManager";
        using var instance = new Mutex(true, SessionMutexName(processKind), out bool acquired);
        if (!acquired)
        {
            if (!sound)
                MessageBox.Show("點心管理器已在執行。請稍候再開啟。", Title);
            return;
        }

        var workspace = new Workspace();
        if (sound)
        {
            SingletonSound.Run(workspace.DataDirectory);
            return;
        }
        ApplicationConfiguration.Initialize();
        Application.Run(new ManagerForm(workspace, args.Contains("--first-launch")));
    }

    public static void Open(string target) => Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });

    public static void ConfigureStartup(bool playSound, bool retireLegacy)
    {
        using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        key.DeleteValue("DimsumManager", false);
        if (playSound) key.SetValue("DimsumSound", $"\"{Environment.ProcessPath}\" --sound");
        else key.DeleteValue("DimsumSound", false);
        if (retireLegacy)
            LegacyUpgrade.PauseOldStartup();
    }
}

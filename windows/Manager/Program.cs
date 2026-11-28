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
        if (args.Length > 0 && args[0] is "--finish-setup" or "--hide-machine-entries"
            or "--install-engine" or "--complete-install" or "--legacy-notice" or "--remove-components" or "--prepare-legacy-engine"
            or "--finish-legacy-cleanup" or "--remove-engine-machine" or "--check-nvda-addon" or "--install-nvda-addon")
        {
            try
            {
                switch (args[0])
                {
                    case "--legacy-notice" when args.Length == 1:
                        if (new LegacyUpgrade(new Workspace()).NeedsNotice()) Environment.ExitCode = 6;
                        break;
                    case "--check-nvda-addon" when args.Length == 1:
                        if (NvdaAddon.ShouldOffer(new Workspace().DataDirectory)) Environment.ExitCode = 6;
                        break;
                    case "--install-nvda-addon" when args.Length == 1:
                        NvdaAddon.InstallIfOffered(new Workspace().DataDirectory);
                        break;
                    case "--complete-install" when args.Length == 1:
                        var installedWorkspace = new Workspace();
                        var log = new OperationLog(installedWorkspace);
                        try
                        {
                            log.Record(SetupCoordinator.RunAsync(installedWorkspace, WindowsSetup.LanguageChoice("zh-HK")!,
                                repair: false, progress: log.Record, confirmed: true).GetAwaiter().GetResult());
                        }
                        catch (Exception error)
                        {
                            log.Record($"安裝未完成（0x{error.HResult:X8}）。{error.Message}");
                            Environment.ExitCode = 1;
                        }
                        break;
                    case "--finish-setup" when args.Length == 2 && ushort.TryParse(args[1], out ushort language):
                        WindowsSetup.RegisterProfile(language);
                        break;
                    case "--hide-machine-entries":
                        WindowsSetup.HideMachineEntries();
                        break;
                    case "--install-engine" when args.Length == 1:
                        WindowsSetup.InstallBundledEngineAsync().GetAwaiter().GetResult();
                        break;
                    case "--prepare-legacy-engine" when args.Length == 2:
                        MachineLegacyUpgrade.PrepareEngineAsync(args[1]).GetAwaiter().GetResult();
                        break;
                    case "--finish-legacy-cleanup" when args.Length == 2:
                        MachineLegacyUpgrade.FinalizeCleanup(args[1]);
                        break;
                    case "--remove-engine-machine" when args.Length is 1 or 2:
                        if (args.Length == 2) MachineLegacyUpgrade.PauseOldUpdater(args[1]);
                        var engineOutcome = WindowsSetup.RemoveEngineMachineAsync().GetAwaiter().GetResult();
                        if (args.Length == 2) MachineLegacyUpgrade.RetireOldInstallation(args[1]);
                        if (engineOutcome == WindowsSetup.EngineOutcome.RestartRequired)
                            Environment.ExitCode = 3;
                        break;
                    case "--remove-components" when args.Length == 1:
                        var removal = new Workspace().RemoveAsync().GetAwaiter().GetResult();
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
                if (args[0] is not ("--complete-install" or "--legacy-notice" or "--check-nvda-addon"))
                    CopyDialog.Show(null, "操作未完成",
                        $"錯誤碼 0x{error.HResult:X8}。{Environment.NewLine}{error.Message}");
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
        Application.Run(new ManagerForm(workspace, args.Contains("--setup-problem"), args.Contains("--skip-nvda-prompt")));
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

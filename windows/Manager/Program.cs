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
            or "--install-engine" or "--after-install" or "--remove-components" or "--prepare-legacy-engine"
            or "--finish-legacy-cleanup" or "--remove-engine-machine" or "--retire-legacy-on-removal"
            or "--check-nvda-addon" or "--install-nvda-addon")
        {
            try
            {
                switch (args[0])
                {
                    case "--check-nvda-addon" when args.Length == 1:
                        string data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DimsumInput");
                        if (NvdaAddon.ShouldOffer(data)) Environment.ExitCode = 6;
                        break;
                    case "--install-nvda-addon" when args.Length == 1:
                        NvdaAddon.Install();
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
        int input = Array.IndexOf(args, "--input-language");
        ushort installerLanguage = input >= 0 && input + 1 < args.Length && ushort.TryParse(args[input + 1], out ushort captured)
            ? captured : (ushort)0;
        Application.Run(new ManagerForm(workspace, args.Contains("--first-launch"), installerLanguage));
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

using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Windows.ApplicationModel.Activation;
using Windows.ApplicationModel.DataTransfer;
using Windows.ApplicationModel.DataTransfer.ShareTarget;
using Windows.Storage;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace FileTransferApp.WinUI
{
    /// <summary>
    /// Provides application-specific behavior to supplement the default Application class.
    /// </summary>
    public partial class App : MauiWinUIApplication
    {
        /// <summary>
        /// Initializes the singleton application object.  This is the first line of authored code
        /// executed, and as such is the logical equivalent of main() or WinMain().
        /// </summary>
        public App()
        {
            this.InitializeComponent();

            // ساخت میانبر "Send to" در حالت لانچ معمولی (Activated برای لانچ اولیه fire نمی‌شود)
            try { CreateSendToShortcut(); } catch { }

            // تک نمونه‌ای بودن: اگر نمونه‌ای در حال اجراست، این نمونه کار را به آن واگذار می‌کند
            // تا "Send to" / Open with به پنجره در حال اجرا برسد
            bool redirectRequired = false;
            try
            {
                var mainInstance = AppInstance.FindOrRegisterForKey("FileTransferApp_Main");
                if (!mainInstance.IsCurrent)
                {
                    var activationArgs = AppInstance.GetCurrent().GetActivatedEventArgs();
                    _ = mainInstance.RedirectActivationToAsync(activationArgs);
                    redirectRequired = true;
                }
                else
                {
                    AppInstance.GetCurrent().Activated += OnAppActivated;
                }
            }
            catch
            {
                try { AppInstance.GetCurrent().Activated += OnAppActivated; }
                catch { }
            }

            if (redirectRequired)
            {
                System.Environment.Exit(0);
                return;
            }
        }

        protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();

        private static bool _sendToShortcutChecked;

        private void OnAppActivated(object sender, AppActivationArguments args)
        {
            try
            {
                if (args == null) return;

                if (!_sendToShortcutChecked)
                {
                    _sendToShortcutChecked = true;
                    CreateSendToShortcut();
                }

                var filePaths = GetFileArguments(args);
                if (filePaths.Count > 0)
                {
                    global::FileTransferApp.App.HandleSharedFiles(filePaths);
                    return;
                }

                if (args.Kind != ExtendedActivationKind.ShareTarget) return;

                if (args.Data is ShareTargetActivatedEventArgs shareArgs)
                    _ = HandleShareActivationAsync(shareArgs);
            }
            catch { }
        }

        private static List<string> GetFileArguments(AppActivationArguments args)
        {
            var result = new List<string>();

            // ۱) آرگومان‌های ارسال‌شده از طریق redirect (نمونه دوم)
            try
            {
                if (args?.Data is ILaunchActivatedEventArgs launch &&
                    !string.IsNullOrWhiteSpace(launch.Arguments))
                {
                    foreach (var p in SplitCommandLine(launch.Arguments))
                    {
                        if (!string.IsNullOrWhiteSpace(p) && File.Exists(p))
                            result.Add(p);
                    }
                    if (result.Count > 0) return result;
                }
            }
            catch { }

            // ۲) اگر خود فرآیند مستقیماً با مسیر فایل اجرا شده باشد
            try
            {
                var cli = Environment.GetCommandLineArgs();
                for (int i = 1; i < cli.Length; i++)
                {
                    if (!string.IsNullOrWhiteSpace(cli[i]) && File.Exists(cli[i]))
                        result.Add(cli[i]);
                }
            }
            catch { }

            return result;
        }

        private static List<string> SplitCommandLine(string text)
        {
            var result = new List<string>();
            var sb = new StringBuilder();
            bool inQuotes = false;
            foreach (var ch in text)
            {
                if (ch == '"')
                    inQuotes = !inQuotes;
                else if (ch == ' ' && !inQuotes)
                {
                    if (sb.Length > 0) { result.Add(sb.ToString()); sb.Clear(); }
                }
                else
                    sb.Append(ch);
            }
            if (sb.Length > 0) result.Add(sb.ToString());
            return result;
        }

        private static void CreateSendToShortcut()
        {
            try
            {
                string sendToFolder = Environment.GetFolderPath(Environment.SpecialFolder.SendTo);
                if (string.IsNullOrWhiteSpace(sendToFolder) || !Directory.Exists(sendToFolder))
                    return;

                string exePath = Environment.ProcessPath;
                if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
                    return;

                string lnkPath = Path.Combine(sendToFolder, "FileTransferApp.lnk");

                // اگر میانبر قبلاً ساخته شده و به همان فایل اشاره می‌کند، نیازی به ساخت نیست
                if (File.Exists(lnkPath) && File.Exists(lnkPath + ".target"))
                {
                    try
                    {
                        var stored = File.ReadAllText(lnkPath + ".target");
                        if (string.Equals(stored, exePath, StringComparison.OrdinalIgnoreCase))
                            return;
                    }
                    catch { }
                }

                var shellType = Type.GetTypeFromProgID("WScript.Shell");
                if (shellType == null) return;

                dynamic shell = Activator.CreateInstance(shellType);
                dynamic shortcut = shell.CreateShortcut(lnkPath);
                shortcut.TargetPath = exePath;
                shortcut.WorkingDirectory = Path.GetDirectoryName(exePath) ?? string.Empty;
                shortcut.Description = FileTransferApp.Services.LocalizationResourceManager.T("SendToDescription");
                shortcut.Save();

                File.WriteAllText(lnkPath + ".target", exePath);
            }
            catch { }
        }

        private static async Task HandleShareActivationAsync(ShareTargetActivatedEventArgs shareArgs)
        {
            try
            {
                ShareOperation shareOp = shareArgs.ShareOperation;
                var paths = new List<string>();

                if (shareOp.Data.Contains(StandardDataFormats.StorageItems))
                {
                    var items = await shareOp.Data.GetStorageItemsAsync();
                    foreach (var item in items.OfType<StorageFile>())
                    {
                        try
                        {
                            // Copy into app temp folder so we can read it later
                            var copied = await item.CopyAsync(ApplicationData.Current.TemporaryFolder,
                                item.Name,
                                NameCollisionOption.GenerateUniqueName);
                            paths.Add(copied.Path);
                        }
                        catch { }
                    }
                }

                if (paths.Count > 0)
                {
                    global::FileTransferApp.App.HandleSharedFiles(paths);
                }

                try { shareOp.ReportCompleted(); } catch { }
            }
            catch
            {
                // ignore
            }
        }
    }

}
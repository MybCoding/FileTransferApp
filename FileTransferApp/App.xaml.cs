using FileTransferApp.Models;
using FileTransferApp.Services;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Dispatching;
using Microsoft.Maui.Storage;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace FileTransferApp
{
    public partial class App : Application
    {
        private static readonly object _shareLock = new();
        private static readonly List<string> _pendingSharedFiles = new();

        public App()
        {
            Debug.WriteLine("App: Constructor");
            InitializeComponent();
            _ = LocalizationResourceManager.Instance;

#if ANDROID
            AndroidFileStorage.ValidateSavedTreeUri();
            DiskSpaceValidator.CacheFreeSpaceProvider = AndroidFileStorage.GetCacheFreeSpace;
            DiskSpaceValidator.SharedStorageFreeSpaceProvider = AndroidFileStorage.GetSharedStorageFreeSpace;
#else
            DiskSpaceValidator.FinalDownloadPathProvider = () => GetBaseDownloadDirectory();
#endif

            _ = Task.Run(async () =>
            {
                try { await Message_Service.StartListenerService(); }
                catch (Exception ex) { Debug.WriteLine($"CRITICAL: Failed to start listener: {ex.Message}"); }
            });

            try { GlobalMessageHandler.Initialize(); }
            catch (Exception ex) { Debug.WriteLine($"CRITICAL: GlobalMessageHandler init failed: {ex.Message}"); }

            MainPage = new SplashPage();
        }

        public static void HandleSharedFiles(List<string> filePaths)
        {
            if (filePaths == null || filePaths.Count == 0) return;

            MainThread.BeginInvokeOnMainThread(async () =>
            {
                for (int i = 0; i < 30 && (Application.Current?.MainPage == null || Shell.Current == null); i++)
                    await Task.Delay(200);

                if (Application.Current?.MainPage == null || Shell.Current == null) return;

                try
                {
                    EnqueueSharedFiles(filePaths);

                    // اگر از قبل صفحه چت با یک دستگاه متصل باز است، مستقیم به همان چت ارسال کن
                    var topChat = FindTopmostChatPage();
                    if (topChat?.BindingContext is ViewModels.ChatPageViewModel openVm &&
                        openVm.TargetDevice != null)
                    {
                        try { await openVm.SendSharedFilesAsync(filePaths); } catch { }
                        return;
                    }

                    string fileList = string.Join("\n", filePaths.Select(Path.GetFileName));
                    bool sendNow = await Application.Current.MainPage.DisplayAlert(
                        LocalizationResourceManager.T("SharedFilesTitle"),
                        LocalizationResourceManager.T("SharedFilesBody", fileList),
                        LocalizationResourceManager.T("Yes"),
                        LocalizationResourceManager.T("No"));

                    if (sendNow)
                        await Shell.Current.GoToAsync("//MainPage");
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"App: HandleSharedFiles error: {ex.Message}");
                }
            });
        }

        private static ChatPage FindTopmostChatPage()
        {
            try
            {
                var stack = Shell.Current?.Navigation?.NavigationStack;
                if (stack == null || stack.Count == 0) return null;
                for (int i = stack.Count - 1; i >= 0; i--)
                {
                    if (stack[i] is ChatPage chat) return chat;
                }
            }
            catch { }
            return null;
        }

        private static void EnqueueSharedFiles(IEnumerable<string> filePaths)
        {
            try
            {
                lock (_shareLock)
                {
                    foreach (var p in filePaths)
                    {
                        if (!string.IsNullOrWhiteSpace(p))
                            _pendingSharedFiles.Add(p);
                    }
                }
            }
            catch { }
        }

        public static bool TryConsumePendingSharedFiles(out List<string> files)
        {
            lock (_shareLock)
            {
                if (_pendingSharedFiles.Count == 0)
                {
                    files = null;
                    return false;
                }

                files = new List<string>(_pendingSharedFiles);
                _pendingSharedFiles.Clear();
                return true;
            }
        }

        public DeviceModel FindOrCreateDevice(string ipAddress, string name)
        {
            var device = new DeviceModel
            {
                IPAddress = ipAddress,
                Name = string.IsNullOrWhiteSpace(name) ? ipAddress : name,
                IsOnline = true,
                LastSeen = DateTime.Now
            };
            return device;
        }

        public ChatPage GetExistingChatPage(DeviceModel targetDevice, bool preferDeviceId = false)
        {
            if (targetDevice == null) return null;

            var stack = Shell.Current?.Navigation?.NavigationStack;
            if (stack == null) return null;

            foreach (var page in stack.Reverse())
            {
                if (page is ChatPage chatPage && chatPage.BindingContext is ViewModels.ChatPageViewModel vm)
                {
                    // اول DeviceId
                    if (preferDeviceId &&
                        !string.IsNullOrWhiteSpace(targetDevice.DeviceId) &&
                        !string.IsNullOrWhiteSpace(vm.TargetDevice?.DeviceId) &&
                        string.Equals(vm.TargetDevice.DeviceId, targetDevice.DeviceId, StringComparison.OrdinalIgnoreCase))
                        return chatPage;

                    // سپس IP به عنوان fallback
                    if (!string.IsNullOrWhiteSpace(targetDevice.IPAddress) &&
                        string.Equals(vm.TargetDevice?.IPAddress, targetDevice.IPAddress, StringComparison.OrdinalIgnoreCase))
                        return chatPage;
                }
            }
            return null;
        }

        public async Task<ViewModels.ChatPageViewModel> NavigateToChatAndInjectFileAsync(
            string senderIp, string senderName, string senderDeviceId,
            string fileName, string tempFilePath, long fileSize)
        {
            var device = FindOrCreateDevice(senderIp, senderName);
            device.DeviceId = senderDeviceId;

            var page = GetExistingChatPage(device, preferDeviceId: true);
            if (page == null)
            {
                page = new ChatPage(device);
                await MainThread.InvokeOnMainThreadAsync(async () =>
                {
                    await Shell.Current.Navigation.PushAsync(page);
                });
            }

            var vm = page.BindingContext as ViewModels.ChatPageViewModel;
            if (vm != null)
                await vm.InjectReceivedFileAsync(senderIp, senderName, fileName, tempFilePath);
            return vm;
        }

        public async Task<ViewModels.ChatPageViewModel> NavigateToChatAndInjectTextAsync(
            string senderIp, string senderName, string senderDeviceId, string message)
        {
            var device = FindOrCreateDevice(senderIp, senderName);
            device.DeviceId = senderDeviceId;

            var page = GetExistingChatPage(device, preferDeviceId: true);
            if (page == null)
            {
                page = new ChatPage(device);
                await MainThread.InvokeOnMainThreadAsync(async () =>
                {
                    await Shell.Current.Navigation.PushAsync(page);
                });
            }

            var vm = page.BindingContext as ViewModels.ChatPageViewModel;
            if (vm != null)
                await vm.InjectReceivedTextAsync(senderIp, senderName, message);
            return vm;
        }

        public async Task<string> SaveReceivedFileAutomatically(string tempFilePath, string originalFileName, string senderName)
        {
            if (string.IsNullOrWhiteSpace(tempFilePath) || !File.Exists(tempFilePath)) return null;

            string safeOriginalName = Path.GetFileName(originalFileName);
            if (string.IsNullOrWhiteSpace(safeOriginalName))
                safeOriginalName = $"file_{Guid.NewGuid():N}";
            safeOriginalName = SanitizeFileName(safeOriginalName);

            try
            {
                string senderFolder = SanitizeFileName(string.IsNullOrWhiteSpace(senderName) ? LocalizationResourceManager.T("UnknownSender") : senderName);
                string categoryFolder = GetDownloadCategoryFolder(safeOriginalName);

                return await SaveFileAsync(tempFilePath, safeOriginalName, senderFolder, categoryFolder, deleteSource: true);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"App: Error saving file: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// «Save» دستی کاربر — فایل را به مقصد عمومی (Downloads/Pictures تماشایی) ذخیره می‌کند
        /// و منبع app-private را دست‌نخورده نگه می‌دارد.
        /// </summary>
        public async Task<string> SaveToPublicDestinationAsync(string sourceFilePath, string originalFileName, string senderName)
        {
            if (string.IsNullOrWhiteSpace(sourceFilePath) || !File.Exists(sourceFilePath)) return null;

            string safeOriginalName = Path.GetFileName(originalFileName);
            if (string.IsNullOrWhiteSpace(safeOriginalName))
                safeOriginalName = $"file_{Guid.NewGuid():N}";
            safeOriginalName = SanitizeFileName(safeOriginalName);

            try
            {
                string senderFolder = SanitizeFileName(string.IsNullOrWhiteSpace(senderName) ? LocalizationResourceManager.T("UnknownSender") : senderName);
                string categoryFolder = GetDownloadCategoryFolder(safeOriginalName);

                return await SaveFileAsync(sourceFilePath, safeOriginalName, senderFolder, categoryFolder, deleteSource: false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"App: Error saving file to public: {ex.Message}");
                return null;
            }
        }

        private async Task<string> SaveFileAsync(string sourceFilePath, string safeOriginalName, string senderFolder, string categoryFolder, bool deleteSource)
        {
#if ANDROID
            if (deleteSource)
                return await AndroidFileStorage.SaveReceivedFileAsync(sourceFilePath, safeOriginalName, senderFolder, categoryFolder);
            return await AndroidFileStorage.SaveToPublicAsync(sourceFilePath, safeOriginalName, senderFolder, categoryFolder);
#else
            string baseDir = GetBaseDownloadDirectory();
            string destDir = Path.Combine(baseDir, senderFolder, categoryFolder);
            if (!Directory.Exists(destDir))
                Directory.CreateDirectory(destDir);

            string destPath = Path.Combine(destDir, safeOriginalName);
            string nameOnly = Path.GetFileNameWithoutExtension(safeOriginalName);
            string ext = Path.GetExtension(safeOriginalName);
            int counter = 1;
            while (File.Exists(destPath))
            {
                destPath = Path.Combine(destDir, $"{nameOnly}({counter}){ext}");
                counter++;
            }

            await CopyFileAsync(sourceFilePath, destPath);
            if (deleteSource)
            {
                try { File.Delete(sourceFilePath); } catch { }
            }
            return destPath;
#endif
        }

        private static async Task CopyFileAsync(string sourcePath, string destPath)
        {
            const int bufferSize = 64 * 1024;
            await using var src = File.Open(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            await using var dst = File.Create(destPath);
            await src.CopyToAsync(dst, bufferSize);
        }

        private string SanitizeFileName(string name)
        {
            var invalid = Path.GetInvalidFileNameChars().Concat(Path.GetInvalidPathChars()).Distinct();
            foreach (var ch in invalid)
                name = name.Replace(ch.ToString(), "");
            return name.Trim();
        }

        private string GetBaseDownloadDirectory()
        {
            string baseDirectory;

            if (DeviceInfo.Platform == DevicePlatform.WinUI)
            {
                baseDirectory = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    "Downloads", "FileTransferApp");
            }
#if IOS
            else if (DeviceInfo.Platform == DevicePlatform.iOS)
            {
                try
                {
                    string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                    baseDirectory = Path.Combine(documents, "FileTransferApp", "Downloads");
                    if (!Directory.Exists(baseDirectory)) Directory.CreateDirectory(baseDirectory);
                }
                catch
                {
                    baseDirectory = Path.Combine(FileSystem.AppDataDirectory, "FileTransferApp", "Downloads");
                }
            }
#endif
            else
            {
                baseDirectory = Path.Combine(FileSystem.AppDataDirectory, "FileTransferApp", "Downloads");
            }

            return baseDirectory;
        }

        private string GetDownloadCategoryFolder(string fileName)
        {
            string ext = Path.GetExtension(fileName)?.ToLowerInvariant();
            return ext switch
            {
                ".jpg" or ".jpeg" or ".png" or ".gif" or ".bmp" or ".webp" or ".heic" or ".heif" => "Images",
                ".mp4" or ".mov" or ".avi" or ".mkv" or ".wmv" or ".flv" => "Videos",
                ".mp3" or ".wav" or ".aac" or ".ogg" => "Audio",
                ".pdf" or ".doc" or ".docx" or ".xls" or ".xlsx" or ".ppt" or ".pptx" or ".txt" or ".rtf" => "Documents",
                ".zip" or ".rar" or ".7z" => "Archives",
                _ => "Others"
            };
        }

        protected override void OnSleep()
        {
            base.OnSleep();
            // On Windows MAUI raises OnSleep every time the window loses focus, which would
            // kill the TCP listener while the user simply works on another app. Keep the
            // listener alive on desktop; only stop it on mobile where the app truly backgrounds.
            if (DeviceInfo.Platform != DevicePlatform.WinUI)
                Message_Service.StopListener();

#if ANDROID
            Platforms.Android.DiscoveryForegroundService.SetDeviceInfo(DeviceInfo.Current.Name, Preferences.Get("DeviceId", string.Empty));
            var intent = new Android.Content.Intent(Android.App.Application.Context, typeof(Platforms.Android.DiscoveryForegroundService));
            Android.App.Application.Context.StartForegroundService(intent);
#endif
        }

        protected override void OnResume()
        {
            base.OnResume();
            _ = Task.Run(async () => await Message_Service.StartListenerService());

#if ANDROID
            var intent = new Android.Content.Intent(Android.App.Application.Context, typeof(Platforms.Android.DiscoveryForegroundService));
            intent.SetAction("com.yazdani.filetransferapp.STOP_DISCOVERY");
            Android.App.Application.Context.StartService(intent);
#endif
        }
    }
}
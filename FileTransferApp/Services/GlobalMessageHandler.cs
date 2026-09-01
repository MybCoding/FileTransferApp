using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Controls;
using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace FileTransferApp.Services
{
    public static class GlobalMessageHandler
    {
        private static bool _inited;
        private static readonly object _statusLock = new();
        private static readonly System.Collections.Generic.Dictionary<string, System.Threading.CancellationTokenSource> _statusClearCtsByDevice =
            new System.Collections.Generic.Dictionary<string, System.Threading.CancellationTokenSource>();

        public static void Initialize()
        {
            if (_inited) return;
            _inited = true;

            Message_Service.TextMessageReceivedEx += OnTextMessageReceivedEx;
            Message_Service.FileMessageReceivedEx += OnFileMessageReceivedEx;
            Message_Service.StatusReceived += OnStatusReceived;
        }

        private static void OnStatusReceived(string ip, string deviceId, string status)
        {
            try
            {
                if (Application.Current is not App app) return;

                // Try find device by deviceId first (more stable), fallback to ip
                var device = app.FindOrCreateDevice(ip, ip);
                if (!string.IsNullOrWhiteSpace(deviceId))
                    device.DeviceId = deviceId;

                MainThread.BeginInvokeOnMainThread(() =>
                {
                    device.StatusMessage = LocalizationResourceManager.TR(status);

                    lock (_statusLock)
                    {
                        var key = !string.IsNullOrWhiteSpace(device.DeviceId) ? device.DeviceId : device.IPAddress;
                        if (string.IsNullOrWhiteSpace(key)) return;

                        if (_statusClearCtsByDevice.TryGetValue(key, out var old))
                        {
                            try { old.Cancel(); old.Dispose(); } catch { }
                            _statusClearCtsByDevice.Remove(key);
                        }

                        if (!string.IsNullOrWhiteSpace(status))
                        {
                            var cts = new System.Threading.CancellationTokenSource();
                            _statusClearCtsByDevice[key] = cts;
                            var token = cts.Token;
                            var translated = LocalizationResourceManager.TR(status);
                            _ = System.Threading.Tasks.Task.Delay(5000, token).ContinueWith(t =>
                            {
                                if (t.IsCanceled) return;
                                MainThread.BeginInvokeOnMainThread(() =>
                                {
                                    if (device.StatusMessage == translated)
                                        device.StatusMessage = null;
                                });
                            });
                        }
                    }
                });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[GlobalMessageHandler] Status handler error: {ex.Message}");
            }
        }

        private static async void OnFileMessageReceivedEx(string ip, string senderName, string senderDeviceId, string fileName, string tempPath, long fileSize)
        {
            try
            {
                if (Application.Current is not App app) return;

                var device = app.FindOrCreateDevice(ip, senderName);
                device.DeviceId = senderDeviceId;

                // اگر صفحه چت باز است، VM خودش هندل می‌کند (بدون دیالوگ در گلوبال)
                var existing = app.GetExistingChatPage(device, preferDeviceId: true);
                if (existing != null) return;

                // اگر قبلاً اعتماد شده
                if (TrustService.IsTrusted(senderDeviceId))
                {
                    await app.NavigateToChatAndInjectFileAsync(ip, senderName, senderDeviceId, fileName, tempPath, fileSize);
                    return;
                }

                // پرسش یک‌باره: دریافت فایل از این دستگاه؟
                var choice = await MainThread.InvokeOnMainThreadAsync(async () =>
                    await Application.Current.MainPage.DisplayActionSheet(
                        LocalizationResourceManager.T("SenderSentFile", senderName, fileName),
                        LocalizationResourceManager.T("Cancel"), null,
                        LocalizationResourceManager.T("TrustOnce"),
                        LocalizationResourceManager.T("TrustAlways")));

                if (choice == LocalizationResourceManager.T("TrustOnce"))
                {
                    TrustService.TrustOnce(senderDeviceId);
                    await app.NavigateToChatAndInjectFileAsync(ip, senderName, senderDeviceId, fileName, tempPath, fileSize);
                }
                else if (choice == LocalizationResourceManager.T("TrustAlways"))
                {
                    TrustService.TrustAlways(senderDeviceId);
                    await app.NavigateToChatAndInjectFileAsync(ip, senderName, senderDeviceId, fileName, tempPath, fileSize);
                }
                else
                {
                    try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[GlobalMessageHandler] File handler error: {ex.Message}");
            }
        }

        private static async void OnTextMessageReceivedEx(string ip, string senderName, string senderDeviceId, string message)
        {
            try
            {
                if (Application.Current is not App app) return;

                var device = app.FindOrCreateDevice(ip, senderName);
                device.DeviceId = senderDeviceId;

                // اگر صفحه چت باز است، VM مدیریت می‌کند
                var existing = app.GetExistingChatPage(device, preferDeviceId: true);
                if (existing != null) return;

                if (TrustService.IsTrusted(senderDeviceId))
                {
                    // مستقیماً به چت برو و پیام را تزریق کن
                    await app.NavigateToChatAndInjectTextAsync(ip, senderName, senderDeviceId, message);
                    return;
                }

                var choice = await MainThread.InvokeOnMainThreadAsync(async () =>
                    await Application.Current.MainPage.DisplayActionSheet(
                        LocalizationResourceManager.T("MessageFrom", senderName),
                        LocalizationResourceManager.T("Cancel"), null,
                        LocalizationResourceManager.T("TrustOnce"),
                        LocalizationResourceManager.T("TrustAlways")));

                if (choice == LocalizationResourceManager.T("TrustOnce"))
                {
                    TrustService.TrustOnce(senderDeviceId);
                    await app.NavigateToChatAndInjectTextAsync(ip, senderName, senderDeviceId, message);
                }
                else if (choice == LocalizationResourceManager.T("TrustAlways"))
                {
                    TrustService.TrustAlways(senderDeviceId);
                    await app.NavigateToChatAndInjectTextAsync(ip, senderName, senderDeviceId, message);
                }
                else
                {
                    // نادیده گرفتن
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[GlobalMessageHandler] Text handler error: {ex.Message}");
            }
        }
    }
}
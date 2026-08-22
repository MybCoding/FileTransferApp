using FileTransferApp.Models;
using FileTransferApp.Services;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Dispatching;
using Microsoft.Maui.Storage;
using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Collections.Generic;
using System.Linq;
using System.Net.NetworkInformation;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;

namespace FileTransferApp
{
    public partial class MainPage : ContentPage
    {
        private readonly DeviceDiscoveryService _discoveryService = new();
        private readonly DownloadServerService _downloadServer = new(); // NEW

        private CancellationTokenSource _cts;
        private IDispatcherTimer _broadcastTimer;
        private IDispatcherTimer _statusTimer;

        private const int OFFLINE_THRESHOLD_SECONDS = 60;

        public ObservableCollection<DeviceModel> DiscoveredDevices { get; } = new();
        public ObservableCollection<DeviceGroup> GroupedDevices { get; } = new();

        private const string HiddenIpsPrefKey = "HiddenDeviceIpsJson";
        private readonly HashSet<string> _hiddenIps = new(StringComparer.OrdinalIgnoreCase);

        private string _statusMessage = "Ready to discover devices";
        public string StatusMessage
        {
            get => _statusMessage;
            set { if (_statusMessage == value) return; _statusMessage = value; OnPropertyChanged(nameof(StatusMessage)); }
        }

        private bool _isRefreshing;
        public bool IsRefreshing
        {
            get => _isRefreshing;
            set { if (_isRefreshing == value) return; _isRefreshing = value; OnPropertyChanged(nameof(IsRefreshing)); }
        }

        private string _localIPAddress;
        private string _localDeviceId;

        // NEW: LAN server bindable props
        private bool _isLanServerRunning;
        public bool IsLanServerRunning
        {
            get => _isLanServerRunning;
            set
            {
                if (_isLanServerRunning == value) return;
                _isLanServerRunning = value;
                OnPropertyChanged(nameof(IsLanServerRunning));
                OnPropertyChanged(nameof(LanServerButtonText));
            }
        }

        private string _lanServerUrl;
        public string LanServerUrl
        {
            get => _lanServerUrl;
            set { if (_lanServerUrl == value) return; _lanServerUrl = value; OnPropertyChanged(nameof(LanServerUrl)); }
        }

        public string LanServerButtonText => IsLanServerRunning ? "Stop LAN link" : "Start LAN link";

        public ICommand RefreshCommand { get; }
        public ICommand SendCommand { get; }
        public ICommand DeleteDeviceCommand { get; }
        public ICommand PairingCommand { get; }

        public MainPage()
        {
            InitializeComponent();
            BindingContext = this;

            OverlayVersionLabel.Text = $"نسخه {AppInfo.Current.VersionString}";

            _localIPAddress = GetLocalIPv4();
            _localDeviceId = Preferences.Get("DeviceId", string.Empty);

            RefreshCommand = new Command(async () => await DoRefreshAsync());
            SendCommand = new Command<DeviceModel>(async (device) =>
            {
                if (device != null)
                    await NavigateToChatPage(device);
            });

            DeleteDeviceCommand = new Command<DeviceModel>(async (device) => await DeleteDeviceAsync(device));
            PairingCommand = new Command<DeviceModel>(async (device) => await StartPairingAsync(device));

            LoadHiddenIps();
            DiscoveredDevices.CollectionChanged += (_, __) => RebuildGroups();
        }

        private void OnLanDownloadTapped(object sender, EventArgs e) => LanOverlay.IsVisible = true;
        private void OnCloseLanClicked(object sender, EventArgs e) => LanOverlay.IsVisible = false;
        private void OnCloseLanTapped(object sender, EventArgs e) => LanOverlay.IsVisible = false;

        private void OnAboutTapped(object sender, EventArgs e) => AboutOverlay.IsVisible = true;
        private void OnCloseAboutClicked(object sender, EventArgs e) => AboutOverlay.IsVisible = false;
        private void OnCloseAboutTapped(object sender, EventArgs e) => AboutOverlay.IsVisible = false;

        private async void OnAboutEmailTapped(object sender, EventArgs e)
        {
            try
            {
                await Launcher.Default.OpenAsync(new Uri("mailto:Mostafa.Yazdani65@gmail.com"));
            }
            catch
            {
                await DisplayAlert("خطا", "امکان باز کردن برنامه ایمیل وجود ندارد.", "باشه");
            }
        }

        private async void OnPrivacyPolicyTapped(object sender, EventArgs e)
        {
            AboutOverlay.IsVisible = false;
            try
            {
                await Shell.Current.GoToAsync("PrivacyPolicy");
            }
            catch
            {
                await Navigation.PushAsync(new PrivacyPolicyPage());
            }
        }

        protected override bool OnBackButtonPressed()
        {
            if (LanOverlay?.IsVisible == true)
            {
                LanOverlay.IsVisible = false;
                return true;
            }
            if (AboutOverlay?.IsVisible == true)
            {
                AboutOverlay.IsVisible = false;
                return true;
            }
            return base.OnBackButtonPressed();
        }

        protected override void OnAppearing()
        {
            base.OnAppearing();

            _discoveryService.DeviceDiscovered += OnDeviceDiscovered;

            Message_Service.PairingStarted += OnPairingPrompt;
            Message_Service.PairingRequested += OnPairingPrompt;
            Message_Service.PairingCompleted += OnPairingCompleted;

            _cts = new CancellationTokenSource();
            _ = Task.Run(() => _discoveryService.StartListeningAsync(_cts.Token));

            _broadcastTimer = Application.Current.Dispatcher.CreateTimer();
            _broadcastTimer.Interval = TimeSpan.FromSeconds(5 + new Random().NextDouble());
            _broadcastTimer.Tick += async (_, __) =>
            {
                await _discoveryService.BroadcastPresenceAsync(DeviceInfo.Name, _cts.Token);
            };
            _broadcastTimer.Start();

            _statusTimer = Application.Current.Dispatcher.CreateTimer();
            _statusTimer.Interval = TimeSpan.FromSeconds(10);
            _statusTimer.Tick += (_, __) => CheckDeviceStatus();
            _statusTimer.Start();

            _ = _discoveryService.BroadcastPresenceAsync(DeviceInfo.Name, _cts.Token);
        }

        protected override async void OnDisappearing()
        {
            base.OnDisappearing();

            // Stop LAN server if running
            if (IsLanServerRunning)
            {
                try { await _downloadServer.StopAsync(); } catch { }
                IsLanServerRunning = false;
                LanServerUrl = string.Empty;
            }

            _discoveryService.DeviceDiscovered -= OnDeviceDiscovered;

            Message_Service.PairingStarted -= OnPairingPrompt;
            Message_Service.PairingRequested -= OnPairingPrompt;
            Message_Service.PairingCompleted -= OnPairingCompleted;

            _broadcastTimer?.Stop();
            _statusTimer?.Stop();
            _broadcastTimer = null;
            _statusTimer = null;

            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;
        }

        private async Task DoRefreshAsync()
        {
            try
            {
                IsRefreshing = true;
                StatusMessage = "Refreshing device list...";
                for (int i = 0; i < 2; i++)
                {
                    await _discoveryService.BroadcastPresenceAsync(DeviceInfo.Name, _cts?.Token ?? CancellationToken.None);
                    await Task.Delay(400);
                }
                StatusMessage = "Device list refreshed";
            }
            catch (Exception ex)
            {
                StatusMessage = $"Error refreshing: {ex.Message}";
            }
            finally
            {
                IsRefreshing = false;
            }
        }

        private void OnDeviceDiscovered(DeviceModel device)
        {
            if (!string.IsNullOrEmpty(device.DeviceId) && device.DeviceId == _localDeviceId) return;
            if (!string.IsNullOrEmpty(device.IPAddress) && device.IPAddress == _localIPAddress) return;
            if (!string.IsNullOrWhiteSpace(device.IPAddress) && _hiddenIps.Contains(device.IPAddress)) return;

            MainThread.BeginInvokeOnMainThread(() =>
            {
                var existing = DiscoveredDevices.FirstOrDefault(d =>
                    (!string.IsNullOrEmpty(device.DeviceId) && d.DeviceId == device.DeviceId) ||
                    (string.IsNullOrEmpty(device.DeviceId) && d.IPAddress == device.IPAddress));

                if (existing == null)
                {
                    device.PropertyChanged += DeviceOnPropertyChanged;
                    DiscoveredDevices.Add(device);
                }
                else
                {
                    existing.Name = device.Name;
                    existing.OS = device.OS;
                    existing.IPAddress = device.IPAddress;
                    existing.LastSeen = DateTime.Now;
                    existing.IsOnline = true;
                    RebuildGroups();
                }
            });
        }

        private void DeviceOnPropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(DeviceModel.IPAddress))
                RebuildGroups();
        }

        private void RebuildGroups()
        {
            GroupedDevices.Clear();

            var groups = DiscoveredDevices
                .Where(d => d != null && !_hiddenIps.Contains(d.IPAddress ?? string.Empty))
                .GroupBy(d => string.IsNullOrWhiteSpace(d.IPAddress) ? "(unknown)" : d.IPAddress)
                .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase);

            foreach (var g in groups)
            {
                var grp = new DeviceGroup(g.Key);
                foreach (var item in g.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
                    grp.Add(item);
                GroupedDevices.Add(grp);
            }
        }

        private void LoadHiddenIps()
        {
            try
            {
                var json = Preferences.Get(HiddenIpsPrefKey, string.Empty);
                if (string.IsNullOrWhiteSpace(json)) return;
                var list = JsonSerializer.Deserialize<List<string>>(json);
                if (list == null) return;
                foreach (var ip in list)
                {
                    if (!string.IsNullOrWhiteSpace(ip))
                        _hiddenIps.Add(ip);
                }
            }
            catch { }
        }

        private void SaveHiddenIps()
        {
            try
            {
                var json = JsonSerializer.Serialize(_hiddenIps.ToList());
                Preferences.Set(HiddenIpsPrefKey, json);
            }
            catch { }
        }

        private async Task DeleteDeviceAsync(DeviceModel device)
        {
            if (device == null) return;

            var ip = device.IPAddress ?? string.Empty;
            var ok = await DisplayAlert("Delete device",
                $"Remove this device from the list?\n\n{device.Name}\n{ip}",
                "Delete",
                "Cancel");

            if (!ok) return;

            if (!string.IsNullOrWhiteSpace(ip))
            {
                _hiddenIps.Add(ip);
                SaveHiddenIps();
            }

            device.PropertyChanged -= DeviceOnPropertyChanged;
            DiscoveredDevices.Remove(device);
            RebuildGroups();
        }

        private void CheckDeviceStatus()
        {
            var now = DateTime.Now;
            MainThread.BeginInvokeOnMainThread(() =>
            {
                foreach (var device in DiscoveredDevices)
                {
                    var offline = (now - device.LastSeen).TotalSeconds > OFFLINE_THRESHOLD_SECONDS;
                    if (offline && device.IsOnline)
                    {
                        device.IsOnline = false;
                    }
                }
            });
        }

        private async Task NavigateToChatPage(DeviceModel device)
        {
            try
            {
                var page = new ChatPage(device);
                await Navigation.PushAsync(page);

                // اگر فایل/عکس از Share وارد برنامه شده باشد، بعد از انتخاب دستگاه به صورت خودکار ارسال کن
                if (page.BindingContext is ViewModels.ChatPageViewModel vm &&
                    App.TryConsumePendingSharedFiles(out var sharedFiles) &&
                    sharedFiles != null && sharedFiles.Count > 0)
                {
                    await vm.SendSharedFilesAsync(sharedFiles);
                }
            }
            catch (Exception ex)
            {
                await DisplayAlert("Navigation Error", $"Failed to navigate to chat: {ex.Message}", "OK");
            }
        }

        // ============================ STAGE 5: PAIRING UI ============================

        private async Task StartPairingAsync(DeviceModel device)
        {
            if (device == null || string.IsNullOrWhiteSpace(device.IPAddress))
            {
                await DisplayAlert("Pairing", "Device IP address is missing.", "OK");
                return;
            }

            StatusMessage = $"Pairing with {device.Name}...";
            bool ok = false;
            try
            {
                ok = await Message_Service.PairWithAsync(device.IPAddress);
            }
            catch (Exception ex)
            {
                StatusMessage = $"Pairing error: {ex.Message}";
                return;
            }

            // PairingCompleted also reports the result (responder + initiator);
            // this covers failures that abort before the event is raised.
            if (!ok) StatusMessage = $"Pairing with {device.Name} failed.";
        }

        private async void OnPairingPrompt(string ip, string peerDeviceId, string peerName, string sas, string sessionId)
        {
            try
            {
                if (!MainThread.IsMainThread)
                {
                    MainThread.BeginInvokeOnMainThread(() => OnPairingPrompt(ip, peerDeviceId, peerName, sas, sessionId));
                    return;
                }

                var accepted = await DisplayAlert(
                    "Confirm pairing",
                    $"Device: {peerName} ({ip})\n\n" +
                    $"Compare the code shown here with the code on the other device.\n\n" +
                    $"SAS: {sas}\n\n" +
                    "Do the codes match?",
                    "Yes, trust this device",
                    "No");

                Message_Service.CompletePairing(sessionId, accepted);
            }
            catch { }
        }

        private void OnPairingCompleted(string peerDeviceId, string peerName, bool success)
        {
            if (!MainThread.IsMainThread)
            {
                MainThread.BeginInvokeOnMainThread(() => OnPairingCompleted(peerDeviceId, peerName, success));
                return;
            }
            StatusMessage = success
                ? $"Paired with {peerName}."
                : $"Pairing with {peerName} failed or was cancelled.";

            if (success && !string.IsNullOrWhiteSpace(peerDeviceId))
            {
                var device = DiscoveredDevices.FirstOrDefault(d =>
                    !string.IsNullOrWhiteSpace(d.DeviceId) &&
                    string.Equals(d.DeviceId, peerDeviceId, StringComparison.OrdinalIgnoreCase));
                device?.RefreshPairState();
            }
        }

        private async void RefreshDeviceList(object sender, EventArgs e)
        {
            await DoRefreshAsync();
        }

        private static string GetLocalIPv4()
        {
            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback ||
                        ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel)
                        continue;

                    foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                    {
                        if (ua.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                        {
                            var ip = ua.Address.ToString();
                            if (IsPrivateIPv4(ip))
                                return ip;
                        }
                    }
                }
            }
            catch { }
            return "127.0.0.1";
        }

        private static bool IsPrivateIPv4(string ip) =>
            !string.IsNullOrWhiteSpace(ip) &&
            (ip.StartsWith("10.") ||
             ip.StartsWith("192.168.") ||
             ip.StartsWith("172.16.") || ip.StartsWith("172.17.") || ip.StartsWith("172.18.") || ip.StartsWith("172.19.") ||
             ip.StartsWith("172.20.") || ip.StartsWith("172.21.") || ip.StartsWith("172.22.") || ip.StartsWith("172.23.") ||
             ip.StartsWith("172.24.") || ip.StartsWith("172.25.") || ip.StartsWith("172.26.") || ip.StartsWith("172.27.") ||
             ip.StartsWith("172.28.") || ip.StartsWith("172.29.") || ip.StartsWith("172.30.") || ip.StartsWith("172.31."));

        // NEW: UI event handlers for LAN server
        private async void OnLanServerToggle(object sender, EventArgs e)
        {
            try
            {
                if (!IsLanServerRunning)
                {
                    // 1) گرفتن لینک آخرین نصاب ویندوز از اینترنت
                    var (downloadUrl, version) = await RemoteConfigService.GetWindowsInstallerUrlAsync();

                    await _downloadServer.StartAsync(
                        port: 8080,
                        redirectUrl: string.IsNullOrWhiteSpace(downloadUrl) ? null : downloadUrl,
                        localFilePath: null,
                        autoStop: TimeSpan.FromMinutes(10));

                    IsLanServerRunning = true;
                    LanServerUrl = _downloadServer.BaseUrl + "/";

                    var verText = string.IsNullOrWhiteSpace(version) ? "" : $" (v{version})";
                    var extra = string.IsNullOrWhiteSpace(downloadUrl)
                        ? "\n\n(Download link is not configured yet. The page will open but download may not start.)"
                        : string.Empty;
                    await DisplayAlert("LAN link ready",
                        $"On your PC, open a browser and enter:{verText}\n\n{LanServerUrl}{extra}",
                        "OK");
                }
                else
                {
                    await _downloadServer.StopAsync();
                    IsLanServerRunning = false;
                    LanServerUrl = string.Empty;
                }
            }
            catch (Exception ex)
            {
                await DisplayAlert("LAN link error", ex.Message, "OK");
            }
        }

        private async void OnCopyLanUrl(object sender, EventArgs e)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(LanServerUrl))
                {
                    await Clipboard.SetTextAsync(LanServerUrl);
                    await DisplayAlert("Copied", "Address copied to clipboard.", "OK");
                }
            }
            catch (Exception ex)
            {
                await DisplayAlert("Copy error", ex.Message, "OK");
            }
        }
    }
}
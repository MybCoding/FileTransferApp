using FileTransferApp.Models;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Storage;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace FileTransferApp.Services
{
    public class DeviceDiscoveryService
    {
        private const int Port = 4040;

        private UdpClient _udpClient;
        private bool _isBound;
        private readonly object _socketLock = new();

        private List<string> _localIpAddresses = new();
        private readonly string _localDeviceId;

        public event Action<DeviceModel> DeviceDiscovered;

        public DeviceDiscoveryService()
        {
            RefreshLocalAddresses();
            _localDeviceId = GetOrCreateDeviceId();
        }

        private string GetOrCreateDeviceId()
        {
            try
            {
                var id = Preferences.Get("DeviceId", string.Empty);
                if (string.IsNullOrWhiteSpace(id))
                {
                    id = Guid.NewGuid().ToString();
                    Preferences.Set("DeviceId", id);
                }
                return id;
            }
            catch
            {
                return Guid.NewGuid().ToString();
            }
        }

        public async Task BroadcastPresenceAsync(string deviceName, CancellationToken ct = default)
        {
            try
            {
                RefreshLocalAddresses();
                string os = DeviceInfo.Platform.ToString();
                string localIP = GetBestLocalIPAddress();

                string message = $"{deviceName}|{localIP}|{os}|{_localDeviceId}";
                byte[] data = Encoding.UTF8.GetBytes(message);

                using var sender = new UdpClient(AddressFamily.InterNetwork);
                sender.EnableBroadcast = true;

                try
                {
                    await sender.SendAsync(data, data.Length, new IPEndPoint(IPAddress.Broadcast, Port)).WaitAsync(ct);
                }
                catch { }

                foreach (var bcast in GetSubnetBroadcastAddresses())
                {
                    try { await sender.SendAsync(data, data.Length, new IPEndPoint(bcast, Port)).WaitAsync(ct); }
                    catch { }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Discovery] Broadcast error: {ex.Message}");
            }
        }

        public async Task StartListeningAsync(CancellationToken token)
        {
            try
            {
                EnsureBound();
                using var ml = AcquireMulticastLock();

                while (!token.IsCancellationRequested)
                {
#if NET7_0_OR_GREATER
                    var result = await _udpClient.ReceiveAsync(token);
#else
                    var receiveTask = _udpClient.ReceiveAsync();
                    var completed = await Task.WhenAny(receiveTask, Task.Delay(-1, token));
                    if (completed != receiveTask) break;
                    var result = receiveTask.Result;
#endif
                    var remoteIp = result.RemoteEndPoint.Address?.ToString() ?? "0.0.0.0";
                    string payload = Encoding.UTF8.GetString(result.Buffer);
                    var parts = payload.Split('|');
                    if (parts.Length < 4) continue;

                    string name = parts[0];
                    string ip = parts[1];
                    string os = parts[2];
                    string deviceId = parts[3];

                    if (!string.IsNullOrEmpty(deviceId) && deviceId == _localDeviceId) continue;
                    if (string.IsNullOrWhiteSpace(ip) || ip == "127.0.0.1" || ip == "0.0.0.0")
                        ip = remoteIp;

                    var device = new DeviceModel
                    {
                        Name = name,
                        IPAddress = ip,
                        OS = os,
                        DeviceId = deviceId,
                        IsOnline = true,
                        LastSeen = DateTime.Now
                    };

                    DeviceDiscovered?.Invoke(device);
                }
            }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Discovery] Listening error: {ex.Message}");
            }
            finally
            {
                CleanupSocket();
            }
        }

        private void EnsureBound()
        {
            lock (_socketLock)
            {
                if (_udpClient == null)
                    _udpClient = new UdpClient(AddressFamily.InterNetwork);

                if (!_isBound)
                {
                    _udpClient.EnableBroadcast = true;
                    _udpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                    _udpClient.Client.Bind(new IPEndPoint(IPAddress.Any, Port));
                    _isBound = true;
                }
            }
        }

        private void CleanupSocket()
        {
            lock (_socketLock)
            {
                try { _udpClient?.Close(); _udpClient?.Dispose(); } catch { }
                _udpClient = null;
                _isBound = false;
            }
        }

        private void RefreshLocalAddresses() => _localIpAddresses = GetLocalIPAddresses();

        private List<string> GetLocalIPAddresses()
        {
            var addresses = new List<string>();
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
                        if (ua.Address.AddressFamily == AddressFamily.InterNetwork &&
                            !IPAddress.IsLoopback(ua.Address))
                        {
                            addresses.Add(ua.Address.ToString());
                        }
                    }
                }
            }
            catch { }
            return addresses;
        }

        private string GetBestLocalIPAddress()
        {
            foreach (var ip in _localIpAddresses)
            {
                if (IsPrivateIPv4(ip)) return ip;
            }
            return _localIpAddresses.FirstOrDefault(ip => ip != "127.0.0.1") ?? "127.0.0.1";
        }

        private static bool IsPrivateIPv4(string ip)
        {
            if (string.IsNullOrWhiteSpace(ip)) return false;
            return ip.StartsWith("10.") ||
                   ip.StartsWith("192.168.") ||
                   ip.StartsWith("172.16.") || ip.StartsWith("172.17.") || ip.StartsWith("172.18.") || ip.StartsWith("172.19.") ||
                   ip.StartsWith("172.20.") || ip.StartsWith("172.21.") || ip.StartsWith("172.22.") || ip.StartsWith("172.23.") ||
                   ip.StartsWith("172.24.") || ip.StartsWith("172.25.") || ip.StartsWith("172.26.") || ip.StartsWith("172.27.") ||
                   ip.StartsWith("172.28.") || ip.StartsWith("172.29.") || ip.StartsWith("172.30.") || ip.StartsWith("172.31.");
        }

        private List<IPAddress> GetSubnetBroadcastAddresses()
        {
            var list = new List<IPAddress>();
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
                        if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                        if (ua.IPv4Mask == null) continue;

                        var ipBytes = ua.Address.GetAddressBytes();
                        var maskBytes = ua.IPv4Mask.GetAddressBytes();
                        if (ipBytes.Length != 4 || maskBytes.Length != 4) continue;

                        var bcast = new byte[4];
                        for (int i = 0; i < 4; i++)
                            bcast[i] = (byte)(ipBytes[i] | (~maskBytes[i]));
                        list.Add(new IPAddress(bcast));
                    }
                }
            }
            catch { }
            return list;
        }

        private IDisposable AcquireMulticastLock()
        {
#if ANDROID
            try
            {
                var ctx = Android.App.Application.Context;
                var wifi = (Android.Net.Wifi.WifiManager)ctx.GetSystemService(Android.Content.Context.WifiService);
                var mlock = wifi?.CreateMulticastLock("ftapp_discovery_lock");
                if (mlock != null)
                {
                    mlock.SetReferenceCounted(true);
                    mlock.Acquire();
                    return new AndroidMulticastLockScope(mlock);
                }
            }
            catch { }
            return new NoopDisposable();
#else
            return new NoopDisposable();
#endif
        }

#if ANDROID
        private sealed class AndroidMulticastLockScope : IDisposable
        {
            private Android.Net.Wifi.WifiManager.MulticastLock _lock;
            public AndroidMulticastLockScope(Android.Net.Wifi.WifiManager.MulticastLock l) => _lock = l;
            public void Dispose()
            {
                try { if (_lock?.IsHeld == true) _lock.Release(); } catch { }
                _lock?.Dispose();
                _lock = null;
            }
        }
#endif
        private sealed class NoopDisposable : IDisposable { public void Dispose() { } }
    }
}
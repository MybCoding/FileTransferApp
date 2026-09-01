using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using AndroidX.Core.App;
using FileTransferApp.Services;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace FileTransferApp.Platforms.Android;

[Service(Exported = false, ForegroundServiceType = ForegroundService.TypeDataSync)]
public class DiscoveryForegroundService : Service
{
    private const string CHANNEL_ID = "discovery_channel";
    private const int NOTIFICATION_ID = 1001;
    private const string ACTION_STOP = "com.yazdani.filetransferapp.STOP_DISCOVERY";

    private CancellationTokenSource _cts;
    private DeviceDiscoveryService _discovery;
    private static string _deviceName;
    private static string _deviceId;
    private static bool _isRunning;

    public static bool IsRunning => _isRunning;

    public static void SetDeviceInfo(string name, string id)
    {
        _deviceName = name;
        _deviceId = id;
    }

    public override IBinder OnBind(Intent intent) => null;

    public override StartCommandResult OnStartCommand(Intent intent, StartCommandFlags flags, int startId)
    {
        if (intent?.Action == ACTION_STOP)
        {
            StopForeground(StopForegroundFlags.Remove);
            StopSelf();
            return StartCommandResult.NotSticky;
        }

        CreateNotificationChannel();

        var notification = BuildNotification();
        StartForeground(NOTIFICATION_ID, notification);

        _cts = new CancellationTokenSource();
        _discovery = new DeviceDiscoveryService();
        _isRunning = true;

        _ = Task.Run(async () =>
        {
            try
            {
                await _discovery.BroadcastPresenceAsync(_deviceName ?? "Android Device", _cts.Token);

                while (!_cts.Token.IsCancellationRequested)
                {
                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(5), _cts.Token);
                        if (!_cts.Token.IsCancellationRequested)
                            await _discovery.BroadcastPresenceAsync(_deviceName ?? "Android Device", _cts.Token);
                    }
                    catch (TaskCanceledException) { break; }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[DiscoveryService] Error: {ex.Message}");
            }
        });

        _ = Task.Run(async () =>
        {
            try
            {
                await _discovery.StartListeningAsync(_cts.Token);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[DiscoveryService] Listen error: {ex.Message}");
            }
        });

        return StartCommandResult.Sticky;
    }

    public override void OnDestroy()
    {
        _isRunning = false;
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        _discovery = null;
        base.OnDestroy();
    }

    private void CreateNotificationChannel()
    {
        if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
        {
            var channel = new NotificationChannel(CHANNEL_ID, LocalizationResourceManager.T("ChannelName"), NotificationImportance.Low)
            {
                Description = LocalizationResourceManager.T("ChannelDesc")
            };
            var manager = GetSystemService(NotificationService) as NotificationManager;
            manager?.CreateNotificationChannel(channel);
        }
    }

    private Notification BuildNotification()
    {
        var stopIntent = new Intent(this, typeof(DiscoveryForegroundService));
        stopIntent.SetAction(ACTION_STOP);
        var stopPendingIntent = PendingIntent.GetService(this, 0, stopIntent,
            PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);

        var openIntent = new Intent(this, typeof(MainActivity));
        openIntent.SetAction(Intent.ActionMain);
        openIntent.AddCategory(Intent.CategoryLauncher);
        var openPendingIntent = PendingIntent.GetActivity(this, 0, openIntent,
            PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);

        var builder = new NotificationCompat.Builder(this, CHANNEL_ID)
            .SetContentTitle("FileTransferApp")
            .SetContentText(LocalizationResourceManager.T("SearchingStatus"))
            .SetSmallIcon(global::Android.Resource.Drawable.IcMenuManage)
            .SetOngoing(true)
            .SetContentIntent(openPendingIntent)
            .AddAction(global::Android.Resource.Drawable.IcMenuCloseClearCancel, LocalizationResourceManager.T("StopAction"), stopPendingIntent);

        if (Build.VERSION.SdkInt >= BuildVersionCodes.JellyBean)
        {
            builder.SetPriority(NotificationCompat.PriorityLow);
        }

        return builder.Build();
    }
}

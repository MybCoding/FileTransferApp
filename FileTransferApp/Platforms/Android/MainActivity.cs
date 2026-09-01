using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Provider;
using Android.Database;
using System.Collections.Generic;
using System.Threading.Tasks;
using Android.Net.Wifi; // اضافه شده
using Android.Util;

namespace FileTransferApp
{
    [Activity(
        Theme = "@style/Maui.SplashTheme",
        MainLauncher = true,
        LaunchMode = LaunchMode.SingleTop,
        ConfigurationChanges =
            ConfigChanges.ScreenSize |
            ConfigChanges.Orientation |
            ConfigChanges.UiMode |
            ConfigChanges.ScreenLayout |
            ConfigChanges.SmallestScreenSize |
            ConfigChanges.Density)]
    // Share target (so the app appears in Android share sheet)
    [IntentFilter(
        new[] { Intent.ActionSend },
        Categories = new[] { Intent.CategoryDefault },
        DataMimeType = "*/*")]
    [IntentFilter(
        new[] { Intent.ActionSendMultiple },
        Categories = new[] { Intent.CategoryDefault },
        DataMimeType = "*/*")]
    public class MainActivity : MauiAppCompatActivity
    {
        private WifiManager.MulticastLock _wifiLock; // اضافه شده

        protected override void OnCreate(Bundle? savedInstanceState)
        {
            try
            {
                Android.Runtime.AndroidEnvironment.UnhandledExceptionRaiser -= AndroidEnvironment_UnhandledExceptionRaiser;
                Android.Runtime.AndroidEnvironment.UnhandledExceptionRaiser += AndroidEnvironment_UnhandledExceptionRaiser;
            }
            catch { }

            try
            {
                base.OnCreate(savedInstanceState);
            }
            catch (Android.Content.Res.Resources.NotFoundException ex)
            {
                try
                {
                    // Try to resolve resource name from "0x7f......" in the message
                    var msg = ex.Message ?? string.Empty;
                    int idx = msg.IndexOf("0x", StringComparison.OrdinalIgnoreCase);
                    if (idx >= 0)
                    {
                        int end = idx + 2;
                        while (end < msg.Length && Uri.IsHexDigit(msg[end])) end++;
                        var hex = msg.Substring(idx + 2, end - (idx + 2));
                        if (int.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out int resId))
                        {
                            string name = null;
                            try { name = Resources?.GetResourceName(resId); } catch { }
                            System.Diagnostics.Debug.WriteLine($"[Android] ResourceId=0x{resId:x8} Name={name ?? "(unknown)"}");
                        }
                    }
                }
                catch { }

                System.Diagnostics.Debug.WriteLine($"[Android] Resources.NotFoundException: {ex.Message}\n{ex}");
                throw;
            }
            catch (System.Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Android] OnCreate error: {ex.Message}\n{ex}");
                throw;
            }

            // دریافت MulticastLock برای اجازه دریافت بسته‌های UDP Broadcast
            try
            {
                var wifiManager = (WifiManager)GetSystemService(Context.WifiService);
                if (wifiManager != null)
                {
                    _wifiLock = wifiManager.CreateMulticastLock("FileTransferAppMulticastLock");
                    _wifiLock.SetReferenceCounted(true);
                    _wifiLock.Acquire();
                    System.Diagnostics.Debug.WriteLine("Android MulticastLock acquired.");
                }
            }
            catch (System.Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error acquiring MulticastLock: {ex.Message}");
            }

            HandleIntent(Intent);

            if (Build.VERSION.SdkInt >= BuildVersionCodes.Tiramisu)
            {
                if (CheckSelfPermission(Android.Manifest.Permission.PostNotifications) != Permission.Granted)
                {
                    RequestPermissions(new[] { Android.Manifest.Permission.PostNotifications }, 2001);
                }
            }
        }

        private void AndroidEnvironment_UnhandledExceptionRaiser(object sender, Android.Runtime.RaiseThrowableEventArgs e)
        {
            try
            {
                var ex = e.Exception;
                System.Diagnostics.Debug.WriteLine($"[Android] Unhandled: {ex?.Message}\n{ex}");
                try { Log.Error("FileTransferApp", ex?.ToString() ?? "(null exception)"); } catch { }

                // If it's a Resources.NotFoundException, try to resolve the resource name from the ID in message.
                if (ex is Android.Content.Res.Resources.NotFoundException rnfe)
                {
                    try
                    {
                        var msg = rnfe.Message ?? string.Empty;
                        int idx = msg.IndexOf("0x", System.StringComparison.OrdinalIgnoreCase);
                        if (idx >= 0)
                        {
                            int end = idx + 2;
                            while (end < msg.Length && System.Uri.IsHexDigit(msg[end])) end++;
                            var hex = msg.Substring(idx + 2, end - (idx + 2));
                            if (int.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out int resId))
                            {
                                string name = null;
                                try { name = Resources?.GetResourceName(resId); } catch { }
                                System.Diagnostics.Debug.WriteLine($"[Android] NotFound resId=0x{resId:x8} name={name ?? "(unknown)"}");
                                try { Log.Error("FileTransferApp", $"NotFound resId=0x{resId:x8} name={name ?? "(unknown)"}"); } catch { }
                            }
                        }
                    }
                    catch { }
                }
            }
            catch { }
        }

        protected override void OnDestroy()
        {
            base.OnDestroy();
            if (_wifiLock != null && _wifiLock.IsHeld)
            {
                _wifiLock.Release();
                System.Diagnostics.Debug.WriteLine("Android MulticastLock released.");
            }
        }

        protected override void OnNewIntent(Intent? intent)
        {
            base.OnNewIntent(intent);
            Intent = intent;
            HandleIntent(intent);
        }

        protected override void OnActivityResult(int requestCode, Result resultCode, Intent? data)
        {
            base.OnActivityResult(requestCode, resultCode, data);
            if (requestCode == AndroidFileStorage.RequestCodeSaveFolder)
                AndroidFileStorage.OnFolderPickerResult(resultCode, data);
        }

        private void HandleIntent(Intent? intent)
        {
            if (intent == null) return;

            string action = intent.Action;
            string type = intent.Type;

            if ((Intent.ActionSend.Equals(action) || Intent.ActionSendMultiple.Equals(action)) && type != null)
            {
                // کپی فایل‌ها روی thread پس‌زمینه انجام می‌شود ولی پارس intent در thread اصلی
                // تا از race condition و کرش جلوگیری شود
                Task.Run(() =>
                {
                    var files = new List<string>();

                    try
                    {
                        if (Intent.ActionSend.Equals(action))
                        {
                            // Handle single file
                            var uri = intent.GetParcelableExtra(Intent.ExtraStream) as Android.Net.Uri;
                            if (uri != null)
                            {
                                string path = GetFilePathFromUri(uri);
                                if (path != null) files.Add(path);
                            }
                            else
                            {
                                // Handle text share
                                string text = intent.GetStringExtra(Intent.ExtraText);
                                if (!string.IsNullOrWhiteSpace(text))
                                {
                                    try
                                    {
                                        var fileName = "shared_text_" + System.Guid.NewGuid().ToString("N") + ".txt";
                                        var tempPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), fileName);
                                        System.IO.File.WriteAllText(tempPath, text);
                                        files.Add(tempPath);
                                    }
                                    catch { }
                                }
                            }
                        }
                        else if (Intent.ActionSendMultiple.Equals(action))
                        {
                            // Handle multiple files
                            var uris = intent.GetParcelableArrayListExtra(Intent.ExtraStream);
                            if (uris != null)
                            {
                                foreach (var u in uris)
                                {
                                    var uri = u as Android.Net.Uri;
                                    if (uri == null) continue;
                                    string path = GetFilePathFromUri(uri);
                                    if (path != null) files.Add(path);
                                }
                            }
                        }
                    }
                    catch (System.Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[Android] HandleIntent parse error: {ex.Message}");
                    }

                    if (files.Count > 0)
                    {
                        try { App.HandleSharedFiles(files); }
                        catch (System.Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine($"[Android] HandleSharedFiles error: {ex.Message}");
                        }
                    }
                });
            }
        }

        private string GetFilePathFromUri(Android.Net.Uri uri)
        {
            if (uri == null) return null;

            // If it's a file:// URI (rare nowadays)
            if ("file".Equals(uri.Scheme, System.StringComparison.OrdinalIgnoreCase))
            {
                return uri.Path;
            }

            // If it's a content:// URI
            if ("content".Equals(uri.Scheme, System.StringComparison.OrdinalIgnoreCase))
            {
                // Copy the content to a temporary file
                try
                {
                    string fileName = GetFileName(uri);
                    if (string.IsNullOrEmpty(fileName)) fileName = "shared_file_" + System.Guid.NewGuid().ToString();

                    string tempPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), fileName);

                    using (var inputStream = ContentResolver.OpenInputStream(uri))
                    using (var outputStream = System.IO.File.Create(tempPath))
                    {
                        inputStream.CopyTo(outputStream);
                    }
                    return tempPath;
                }
                catch (System.Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Error resolving content URI: {ex.Message}");
                    return null;
                }
            }
            return null;
        }

        private string GetFileName(Android.Net.Uri uri)
        {
            string result = null;
            if (uri.Scheme.Equals("content"))
            {
                using (ICursor cursor = ContentResolver.Query(uri, null, null, null, null))
                {
                    if (cursor != null && cursor.MoveToFirst())
                    {
                        int nameIndex = cursor.GetColumnIndex(OpenableColumns.DisplayName);
                        if (nameIndex >= 0)
                            result = cursor.GetString(nameIndex);
                    }
                }
            }
            if (result == null)
            {
                result = uri.Path;
                int cut = result.LastIndexOf('/');
                if (cut != -1)
                {
                    result = result.Substring(cut + 1);
                }
            }
            return result;
        }
    }
}

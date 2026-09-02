using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Provider;
using AndroidX.Core.Content;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Storage;
using FileTransferApp;

namespace FileTransferApp.Services
{
    // Captures a photo by launching the device camera app via ACTION_IMAGE_CAPTURE intent.
    // This does NOT require the CAMERA permission, so the app keeps a clean manifest.
    public static class AndroidCameraCapture
    {
        public const int RequestCodeCapturePhoto = 4202;

        private static TaskCompletionSource<Result>? _captureTcs;

        public static async Task<FileResult?> CapturePhotoAsync()
        {
            var context = Platform.AppContext;
            var activity = Platform.CurrentActivity;

            if (activity == null)
                throw new FeatureNotSupportedException("Camera capture is not supported.");

            if (!context.PackageManager.HasSystemFeature(PackageManager.FeatureCameraAny))
                throw new FeatureNotSupportedException("No camera available on this device.");

            var dir = context.CacheDir;
            if (dir == null)
                throw new FeatureNotSupportedException("Camera capture is not supported.");

            var fileName = "capture_" + Guid.NewGuid().ToString("N") + ".jpg";
            var file = new Java.IO.File(dir, fileName);

            var outputUri = AndroidX.Core.Content.FileProvider.GetUriForFile(context, MainActivity.FileProviderAuthority, file);

            var intent = new Intent(MediaStore.ActionImageCapture);
            intent.PutExtra(MediaStore.ExtraOutput, outputUri);
            intent.AddFlags(ActivityFlags.GrantReadUriPermission);
            intent.AddFlags(ActivityFlags.GrantWriteUriPermission);

            if (intent.ResolveActivity(context.PackageManager) == null)
                throw new FeatureNotSupportedException("No camera app found on this device.");

            var tcs = new TaskCompletionSource<Result>();
            _captureTcs = tcs;
            MainActivity.ActivityResult += OnActivityResult;

            try
            {
                activity.StartActivityForResult(intent, RequestCodeCapturePhoto);
            }
            catch (ActivityNotFoundException)
            {
                MainActivity.ActivityResult -= OnActivityResult;
                _captureTcs = null;
                throw new FeatureNotSupportedException("No camera app found on this device.");
            }

            var result = await tcs.Task;

            if (result != Result.Ok)
                return null;

            if (!file.Exists() || file.Length() == 0)
                return null;

            return new FileResult(file.AbsolutePath, fileName);
        }

        private static void OnActivityResult(int requestCode, Result resultCode, Intent? data)
        {
            if (requestCode != RequestCodeCapturePhoto)
                return;

            MainActivity.ActivityResult -= OnActivityResult;
            var tcs = _captureTcs;
            _captureTcs = null;
            tcs?.TrySetResult(resultCode);
        }
    }
}
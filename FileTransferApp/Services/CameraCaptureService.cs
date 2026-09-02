using Microsoft.Maui.Storage;

namespace FileTransferApp.Services
{
    public interface ICameraCaptureService
    {
        Task<FileResult?> CapturePhotoAsync();
    }

    public partial class CameraCaptureService : ICameraCaptureService
    {
        public Task<FileResult?> CapturePhotoAsync()
        {
#if ANDROID
            return AndroidCameraCapture.CapturePhotoAsync();
#else
            return MediaPicker.Default.CapturePhotoAsync();
#endif
        }
    }
}
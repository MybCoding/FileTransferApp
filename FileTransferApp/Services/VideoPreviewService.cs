using System;
using System.IO;
using System.Threading.Tasks;

#if ANDROID
using Android.Graphics;
using Android.Media;
#endif

namespace FileTransferApp.Services
{
    public static class VideoPreviewService
    {
        public static Task<byte[]> TryGenerateVideoThumbnailAsync(string filePath, int maxSize = 320)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
                    return Task.FromResult<byte[]>(null);

#if ANDROID
                return Task.Run(() =>
                {
                    MediaMetadataRetriever retriever = null;
                    Bitmap frame = null;
                    Bitmap scaled = null;
                    try
                    {
                        retriever = new MediaMetadataRetriever();
                        retriever.SetDataSource(filePath);

                        frame = retriever.GetFrameAtTime(0);
                        if (frame == null) return null;

                        var w = frame.Width;
                        var h = frame.Height;
                        if (w <= 0 || h <= 0) return null;

                        var scale = Math.Min((double)maxSize / w, (double)maxSize / h);
                        if (scale <= 0) scale = 1;

                        var nw = Math.Max(1, (int)Math.Round(w * scale));
                        var nh = Math.Max(1, (int)Math.Round(h * scale));
                        scaled = Bitmap.CreateScaledBitmap(frame, nw, nh, true);

                        using var ms = new MemoryStream();
                        scaled.Compress(Bitmap.CompressFormat.Jpeg, 85, ms);
                        return ms.ToArray();
                    }
                    catch
                    {
                        return null;
                    }
                    finally
                    {
                        try { scaled?.Recycle(); scaled?.Dispose(); } catch { }
                        try { frame?.Recycle(); frame?.Dispose(); } catch { }
                        try { retriever?.Release(); retriever?.Dispose(); } catch { }
                    }
                });
#else
                return Task.FromResult<byte[]>(null);
#endif
            }
            catch
            {
                return Task.FromResult<byte[]>(null);
            }
        }
    }
}




using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Storage;
using FileTransferApp.Services;
namespace FileTransferApp.Services
{
    public static class PreviewService
    {
        public static async Task<ImageSource> GeneratePreviewAsync(FileResult file)
        {
            try
            {
                if (file == null || !file.ContentType.StartsWith("image/"))
                    return null;

                using var stream = await file.OpenReadAsync();
                byte[] previewData = await GenerateImagePreviewAsync(stream);

                return previewData != null ?
                    ImageSource.FromStream(() => new MemoryStream(previewData)) :
                    null;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Preview generation failed: {ex}");
                return null;
            }
        }

        public static async Task<byte[]> GenerateImagePreviewAsync(Stream imageStream)
        {
            try
            {
                // ایجاد یک نسخه کوچک شده از تصویر
                using var memoryStream = new MemoryStream();
                await imageStream.CopyToAsync(memoryStream);

                // اینجا می‌توانید از کتابخانه‌هایی مانند SkiaSharp برای تغییر سایز تصویر استفاده کنید
                // در این مثال ساده، همان تصویر اصلی را برمی‌گردانیم
                return memoryStream.ToArray();

                // اگر می‌خواهید تصویر را تغییر سایز دهید:
                // return ResizeImage(memoryStream.ToArray(), maxWidth: 300, maxHeight: 300);
            }
            catch
            {
                return null;
            }
        }
        public static async Task<ImageSource> GeneratePreviewFromBytesAsync(byte[] fileData, string fileName)
        {
            if (!Message_Service.IsImageFile(fileName))
                return null;

            try
            {
                byte[] previewData = await GenerateImagePreviewAsync(new MemoryStream(fileData));
                return previewData != null ?
                    ImageSource.FromStream(() => new MemoryStream(previewData)) :
                    null;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Preview generation failed: {ex}");
                return null;
            }
        }

        public static async Task<ImageSource> GeneratePreviewFromFileAsync(string filePath)
        {
            if (!Message_Service.IsImageFile(Path.GetFileName(filePath)))
                return null;

            try
            {
                using var stream = File.OpenRead(filePath);
                byte[] previewData = await GenerateImagePreviewAsync(stream);
                return previewData != null ?
                    ImageSource.FromStream(() => new MemoryStream(previewData)) :
                    null;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Preview generation failed: {ex}");
                return null;
            }
        }

        
        private static byte[] ResizeImage(byte[] imageData, int maxWidth, int maxHeight)
        {
            // پیاده‌سازی تغییر سایز تصویر با SkiaSharp
            // نیاز به نصب NuGet Package: SkiaSharp
            /*
            using var input = new MemoryStream(imageData);
            using var inputStream = new SKManagedStream(input);
            using var original = SKBitmap.Decode(inputStream);
            
            float ratio = Math.Min((float)maxWidth / original.Width, (float)maxHeight / original.Height);
            int width = (int)(original.Width * ratio);
            int height = (int)(original.Height * ratio);
            
            using var resized = original.Resize(new SKImageInfo(width, height), SKFilterQuality.Medium);
            using var image = SKImage.FromBitmap(resized);
            using var output = new MemoryStream();
            
            image.Encode(SKEncodedImageFormat.Jpeg, 80).SaveTo(output);
            return output.ToArray();
            */

            // نسخه ساده (بدون تغییر سایز)
            return imageData;
        }

    }
}
using System;
using System.IO;

namespace FileTransferApp.Services
{
    public static class DiskSpaceValidator
    {
        // Android sets this to a StatFs-based provider (exact cache partition).
        public static Func<long>? CacheFreeSpaceProvider { get; set; }

        // Android sets this to a StatFs-based provider (shared/external storage).
        public static Func<long>? SharedStorageFreeSpaceProvider { get; set; }

        // Windows sets this to the final Downloads folder path.
        public static Func<string>? FinalDownloadPathProvider { get; set; }

        public static long GetAvailableFreeSpace(string? directoryPath)
        {
            if (string.IsNullOrWhiteSpace(directoryPath)) return -1;
            try
            {
                string? root = Path.GetPathRoot(directoryPath);
                if (string.IsNullOrEmpty(root)) return -1;
                var drive = new DriveInfo(root);
                return drive.IsReady ? drive.AvailableFreeSpace : -1;
            }
            catch
            {
                return -1;
            }
        }

        public static long GetCacheFreeSpace(string fallbackDirectory)
        {
            try
            {
                if (CacheFreeSpaceProvider != null)
                {
                    long value = CacheFreeSpaceProvider();
                    if (value >= 0) return value;
                }
            }
            catch { }

            return GetAvailableFreeSpace(fallbackDirectory);
        }

        public static bool HasSufficientSpace(string directoryPath, long requiredBytes, out long availableBytes)
        {
            availableBytes = GetAvailableFreeSpace(directoryPath);
            if (availableBytes < 0) return false;
            return requiredBytes <= availableBytes;
        }

        public static bool TryComputeRequiredSpace(long fileSize, long safetyMargin, out long requiredBytes)
        {
            if (fileSize < 0 || safetyMargin < 0 || fileSize > long.MaxValue - safetyMargin)
            {
                requiredBytes = 0;
                return false;
            }
            requiredBytes = fileSize + safetyMargin;
            return true;
        }

        public static long GetFinalStorageFreeSpace()
        {
            try
            {
                if (FinalDownloadPathProvider != null)
                {
                    string path = FinalDownloadPathProvider();
                    if (!string.IsNullOrWhiteSpace(path))
                    {
                        long free = GetAvailableFreeSpace(path);
                        if (free >= 0) return free;
                    }
                }

                if (SharedStorageFreeSpaceProvider != null)
                    return SharedStorageFreeSpaceProvider();
            }
            catch { }

            return -1;
        }
    }
}

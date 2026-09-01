using System;

namespace FileTransferApp.Services
{
    public static class FileSizeValidator
    {
        public static bool ValidateFileName(string? fileName, out string? reason)
        {
            reason = null;
            if (string.IsNullOrWhiteSpace(fileName))
            {
                reason = "InvalidFileName";
                return false;
            }
            if (fileName.Length > TransferLimits.MaxFileNameLength)
            {
                reason = "FileNameTooLong";
                return false;
            }
            return true;
        }

        public static bool ValidateFileSize(long size, out string? reason)
        {
            reason = null;
            if (size <= 0)
            {
                reason = "InvalidSize";
                return false;
            }
            if (size > TransferLimits.MaxIncomingFileSize)
            {
                reason = "FileTooLarge";
                return false;
            }
            return true;
        }

        // Hard write limit: never lets a stream write more than expectedSize bytes to disk.
        // Returns true when the whole chunk is within the limit; false when the chunk
        // would exceed the declared size (bytesToWrite then holds the allowed portion).
        public static bool TryLimitWriteChunk(int bytesRead, long totalWritten, long expectedSize, out int bytesToWrite)
        {
            bytesToWrite = bytesRead;
            if (bytesRead < 0) return false;
            if (expectedSize < 0 || totalWritten < 0 || totalWritten >= expectedSize)
            {
                bytesToWrite = 0;
                return false;
            }
            long remaining = expectedSize - totalWritten;
            if (bytesRead > remaining)
            {
                bytesToWrite = (int)remaining;
                return false;
            }
            return true;
        }
    }
}

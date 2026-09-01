using System;
using System.IO;
using System.Threading;

namespace FileTransferApp.Services
{
    public static class IncomingTransferGuard
    {
        private static SemaphoreSlim _slotGate =
            new(TransferLimits.MaxConcurrentIncomingTransfers, TransferLimits.MaxConcurrentIncomingTransfers);

        public static void Configure(int maxConcurrentTransfers)
        {
            if (maxConcurrentTransfers < 1) maxConcurrentTransfers = 1;
            var old = _slotGate;
            _slotGate = new SemaphoreSlim(maxConcurrentTransfers, maxConcurrentTransfers);
            try { old?.Dispose(); } catch { }
        }

        // Atomic Check + Reserve (no race between concurrent connections).
        public static bool TryReserveSlot() => _slotGate.Wait(0);

        public static void ReleaseSlot()
        {
            try { _slotGate.Release(); } catch (SemaphoreFullException) { }
        }

        public static string CreateTempPath(string cacheDirectory, string fileName)
            => Path.Combine(cacheDirectory, $"{Guid.NewGuid():N}_{fileName}");
    }
}

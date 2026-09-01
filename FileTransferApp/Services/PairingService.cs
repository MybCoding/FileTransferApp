using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace FileTransferApp.Security
{
    public sealed class PairingMessage
    {
        public string Type = string.Empty;   // PAIR1 / PAIR2 / PAIR_ACCEPT / PAIR_ABORT
        public string DeviceId = string.Empty;
        public string DeviceName = string.Empty;
        public string Sas = string.Empty;     // only present in PAIR2
    }

    /// <summary>
    /// Pairing protocol helpers + pending-confirmation registry.
    /// The SAS is derived from the ECDHE master key + transcript, so both ends
    /// compute the identical 6-digit code without transmitting it; a MITM yields
    /// different codes which the human comparison detects.
    /// </summary>
    public static class PairingService
    {
        public const string Pair1 = "PAIR1";
        public const string Pair2 = "PAIR2";
        public const string PairAccept = "PAIR_ACCEPT";
        public const string PairAbort = "PAIR_ABORT";

        private sealed class PendingConfirmation
        {
            public TaskCompletionSource<bool> Tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public CancellationTokenSource Cts = new();
        }

        private static readonly ConcurrentDictionary<string, PendingConfirmation> _pending =
            new(StringComparer.OrdinalIgnoreCase);

        public static string ComputeSas(byte[] masterKey, byte[] transcriptHash) =>
            Crypto.ComputeSas(masterKey, transcriptHash);

        // ============================ Pending confirmation registry ============================

        public static string RegisterPending()
        {
            var id = Guid.NewGuid().ToString("N");
            _pending[id] = new PendingConfirmation();
            return id;
        }

        /// <summary>Completes a pending confirmation. Returns false if unknown/already completed.</summary>
        public static bool Complete(string sessionId, bool accepted)
        {
            if (string.IsNullOrWhiteSpace(sessionId)) return false;
            if (_pending.TryRemove(sessionId, out var pending))
            {
                pending.Tcs.TrySetResult(accepted);
                return true;
            }
            return false;
        }

        /// <summary>
        /// Removes a pending confirmation, resolving any in-flight waiter to "not accepted"
        /// (used for the prompt-timeout / abort path).
        /// </summary>
        public static void Cleanup(string sessionId)
        {
            if (string.IsNullOrWhiteSpace(sessionId)) return;
            if (_pending.TryRemove(sessionId, out var pending))
            {
                pending.Cts.Cancel();
                pending.Tcs.TrySetResult(false);
                pending.Cts.Dispose();
            }
        }

        /// <summary>
        /// Waits until <see cref="Complete"/> resolves the session or the registry
        /// <see cref="Cleanup"/>s it (prompt timeout / abort) or the token cancels.
        /// Returns true only on explicit user acceptance.
        /// </summary>
        public static async Task<bool> WaitForConfirmationAsync(string sessionId, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(sessionId)) return false;
            if (!_pending.TryGetValue(sessionId, out var pending)) return false;
            var registryDelay = Task.Delay(Timeout.InfiniteTimeSpan, pending.Cts.Token);
            var callerDelay = Task.Delay(Timeout.InfiniteTimeSpan, ct);
            var done = await Task.WhenAny(pending.Tcs.Task, registryDelay, callerDelay).ConfigureAwait(false);
            if (done != pending.Tcs.Task) return false;
            return await pending.Tcs.Task.ConfigureAwait(false);
        }

        // ============================ Message codec ============================

        public static byte[] Encode(PairingMessage m)
        {
            if (m == null) throw new ArgumentNullException(nameof(m));
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms, Encoding.UTF8, true);
            w.Write(m.Type ?? string.Empty);
            w.Write(m.DeviceId ?? string.Empty);
            w.Write(m.DeviceName ?? string.Empty);
            if (m.Type == Pair2) w.Write(m.Sas ?? string.Empty);
            return ms.ToArray();
        }

        public static PairingMessage Decode(byte[] payload)
        {
            if (payload == null) throw new ArgumentNullException(nameof(payload));
            using var ms = new MemoryStream(payload, false);
            using var r = new BinaryReader(ms, Encoding.UTF8, true);
            var m = new PairingMessage
            {
                Type = r.ReadString(),
                DeviceId = r.ReadString(),
                DeviceName = r.ReadString()
            };
            if (m.Type == Pair2) m.Sas = r.ReadString();
            return m;
        }
    }
}

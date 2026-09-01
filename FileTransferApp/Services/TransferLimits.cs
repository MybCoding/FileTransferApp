using System;

namespace FileTransferApp.Services
{
    public enum SecurityPolicy
    {
        // Secure-only: every P2P connection MUST complete the authenticated
        // handshake (ECDHE + ECDSA signature). No plaintext/legacy messages
        // are accepted and there is NO automatic fallback to legacy.
        Enforce = 0,

        // Explicit local opt-in to also accept legacy plaintext peers
        // (TEXT/TEXT2/FILE_OFFER/FILE/STATUS without handshake) using the
        // old deviceId-only trust. Never automatic: Enforce is the default
        // and legacy never gains automatic trust.
        AllowLegacy = 1
    }

    public static class TransferLimits
    {
        public const long DefaultMaxIncomingFileSize = 4L * 1024L * 1024L * 1024L; // 4 GB
        public const int DefaultMaxConcurrentIncomingTransfers = 2;
        public const long DefaultMinimumFreeDiskSpace = 1L * 1024L * 1024L * 1024L; // 1 GB
        public const int DefaultMaxFileNameLength = 180;

        public static long MaxIncomingFileSize { get; set; } = DefaultMaxIncomingFileSize;
        public static int MaxConcurrentIncomingTransfers { get; set; } = DefaultMaxConcurrentIncomingTransfers;
        public static long MinimumFreeDiskSpace { get; set; } = DefaultMinimumFreeDiskSpace;
        public static int MaxFileNameLength { get; set; } = DefaultMaxFileNameLength;

        // Redundant with MaxIncomingFileSize: the temporary file IS the incoming file,
        // so its size limit is identical. Kept for clarity.
        public static long MaxTemporaryFileSize => MaxIncomingFileSize;

        // Timeout waiting for the FILE_STREAM header right after ACCEPT was sent.
        public static TimeSpan IncomingFileStreamTimeout { get; set; } = TimeSpan.FromSeconds(15);

        // Idle timeout while the stream is up: aborts if no bytes arrive for this long.
        public static TimeSpan IncomingIdleTimeout { get; set; } = TimeSpan.FromSeconds(30);

        // Sender-side: wait for the ACCEPT response after sending FILE_OFFER.
        public static TimeSpan OfferResponseTimeout { get; set; } = TimeSpan.FromSeconds(8);

        // Sender-side: wait for the final ACK after streaming completes.
        public static TimeSpan FinalAckTimeout { get; set; } = TimeSpan.FromMinutes(10);

        // ===================== STAGE 5: TRANSPORT SECURITY =====================

        // Default security mode. Secure-only; legacy must be opted into explicitly.
        public static SecurityPolicy SecurityPolicyMode { get; set; } = SecurityPolicy.Enforce;

        // True when the local policy explicitly allows legacy plaintext peers.
        public static bool AllowLegacy => SecurityPolicyMode == SecurityPolicy.AllowLegacy;

        // Time allowed for the ECDHE+ECDSA handshake to complete on a new connection.
        public static TimeSpan HandshakeTimeout { get; set; } = TimeSpan.FromSeconds(10);

        // Total time a pairing session may stay open waiting for the peer/user.
        public static TimeSpan PairingTimeout { get; set; } = TimeSpan.FromSeconds(180);

        // How long the UI prompt waits for the user to accept/reject a pairing.
        public static TimeSpan PairingPromptTimeout { get; set; } = TimeSpan.FromSeconds(120);

        // Hard cap on a single encrypted frame payload (decrypted length).
        public const int MaxFramePayloadSize = 2 * 1024 * 1024; // 2 MB

        // Chunk size used when streaming file bytes over the secure channel.
        public const int FrameChunkSize = 1024 * 1024; // 1 MB
    }
}

using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FileTransferApp.Services;

namespace FileTransferApp.Security
{
    /// <summary>
    /// Stage 5 P2P authenticated + encrypted channel.
    ///
    /// Wire format (all over the same TcpClient.Stream):
    ///   1) HELLO / HELLO_ACK plaintext handshake (ECDHE P-256 + ECDSA P-256 signatures)
    ///   2) Secure frames: AES-256-GCM with per-frame random 96-bit nonce, AAD binding
    ///      (magic, version, sessionId, sequence) and a strict sequence counter (replay
    ///      protection — TCP already guarantees ordering).
    ///
    /// This class is free of MAUI dependencies so it can be linked into the unit test
    /// project and tested end-to-end over in-memory streams.
    /// </summary>
    public static class P2pChannel
    {
        public const byte Version = 1;
        public static readonly byte[] Magic = { (byte)'F', (byte)'T', (byte)'A', (byte)'F' };

        public const int HeaderSize = 4 + 1 + Crypto.SessionIdSize + 4 + Crypto.NonceSize + 4; // 41 bytes

        public const string C2SInfo = "fta-c2s-v1";
        public const string S2CInfo = "fta-s2c-v1";

        // ============================ Handshake results ============================

        public sealed class SessionKeys
        {
            public byte[] Master = Array.Empty<byte>();
            public byte[] C2S = Array.Empty<byte>();
            public byte[] S2C = Array.Empty<byte>();
            public byte[] TranscriptHash = Array.Empty<byte>();
            public byte[] SessionId = Array.Empty<byte>();
        }

        public enum Role { Client, Server }

        public sealed class HandshakeResult
        {
            public Role MyRole;
            public byte[] SessionId = Array.Empty<byte>();
            public string PeerDeviceId = string.Empty;
            public string PeerName = string.Empty;
            public byte[] PeerPublicKeySpki = Array.Empty<byte>();
            public byte[] PeerFingerprint = Array.Empty<byte>();
            public string PeerFingerprintHex = string.Empty;
            public SessionKeys Keys = new();
        }

        // ============================ Transcripts ============================

        /// <summary>HELLO-side transcript (what the initiator signs).</summary>
        public static byte[] BuildInitTranscript(
            byte version,
            string initDeviceId,
            string initName,
            byte[] initLongTermSpki,
            byte[] initEphPub)
        {
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms, Encoding.UTF8, true);
            w.Write(Crypto.Utf8(Crypto.HandshakeDomain));
            w.Write(version);
            w.Write(initDeviceId ?? string.Empty);
            w.Write(initName ?? string.Empty);
            w.Write(initLongTermSpki ?? Array.Empty<byte>());
            w.Write(initEphPub ?? Array.Empty<byte>());
            return ms.ToArray();
        }

        /// <summary>Full transcript (init HELLO + responder HELLO_ACK). Used for HELLO_ACK signature and KDF.</summary>
        public static byte[] BuildFullTranscript(
            byte version,
            string initDeviceId,
            string initName,
            byte[] initLongTermSpki,
            byte[] initEphPub,
            string respDeviceId,
            string respName,
            byte[] respLongTermSpki,
            byte[] respEphPub)
        {
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms, Encoding.UTF8, true);
            w.Write(Crypto.Utf8(Crypto.HandshakeDomain));
            w.Write(version);
            w.Write(initDeviceId ?? string.Empty);
            w.Write(initName ?? string.Empty);
            w.Write(initLongTermSpki ?? Array.Empty<byte>());
            w.Write(initEphPub ?? Array.Empty<byte>());
            w.Write(respDeviceId ?? string.Empty);
            w.Write(respName ?? string.Empty);
            w.Write(respLongTermSpki ?? Array.Empty<byte>());
            w.Write(respEphPub ?? Array.Empty<byte>());
            return ms.ToArray();
        }

        public static SessionKeys DeriveSessionKeys(byte[] myEphPriv, byte[] peerEphPub, byte[] transcriptHash, byte[] sessionId)
        {
            var shared = Crypto.EcdhDeriveSharedSecret(myEphPriv, peerEphPub);
            var master = Crypto.HkdfDerive(
                shared, null,
                Crypto.Concat(Crypto.Utf8(Crypto.KdfDomain), transcriptHash),
                Crypto.KeySize);
            var c2s = Crypto.HkdfDerive(
                master, null,
                Crypto.Concat(Crypto.Utf8(C2SInfo), transcriptHash),
                Crypto.KeySize);
            var s2c = Crypto.HkdfDerive(
                master, null,
                Crypto.Concat(Crypto.Utf8(S2CInfo), transcriptHash),
                Crypto.KeySize);
            return new SessionKeys { Master = master, C2S = c2s, S2C = s2c, TranscriptHash = transcriptHash, SessionId = sessionId };
        }

        public static byte[] BuildAad(byte version, byte[] sessionId, uint seq)
        {
            using var ms = new MemoryStream(HeaderSize);
            using var w = new BinaryWriter(ms, Encoding.UTF8, true);
            w.Write(Magic);
            w.Write(version);
            w.Write(sessionId ?? Array.Empty<byte>());
            WriteUInt32Be(w, seq);
            return ms.ToArray();
        }

        // ============================ Client (initiator) handshake ============================

        public static Task<HandshakeResult> ClientHandshakeAsync(
            Stream stream,
            byte[] myPrivateKeyPkcs8,
            byte[] myLongTermSpki,
            string myDeviceId,
            string myName,
            CancellationToken ct)
        {
            using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
            using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);

            var sessionId = Crypto.GenerateSessionId();
            var (ephPriv, ephPub) = Crypto.GenerateEcdhKeyPair();
            var initTranscript = BuildInitTranscript(Version, myDeviceId, myName, myLongTermSpki, ephPub);
            var sig = Crypto.Sign(myPrivateKeyPkcs8, initTranscript);

            writer.Write("HELLO");
            writer.Write(Version);
            WriteBytesLp(writer, sessionId);
            writer.Write(myDeviceId ?? string.Empty);
            writer.Write(myName ?? string.Empty);
            WriteBytesLp(writer, myLongTermSpki);
            WriteBytesLp(writer, ephPub);
            WriteBytesLp(writer, sig);
            writer.Flush();

            string header = ReadStringTimeout(reader, ct, "HELLO_ACK");
            if (header != "HELLO_ACK") throw new InvalidDataException($"Unexpected handshake header '{header}'");
            byte ver = reader.ReadByte();
            if (ver != Version) throw new InvalidDataException($"Unsupported handshake version {ver}");
            var respSessionId = ReadBytesLp(reader, ct);
            if (!Crypto.ConstantTimeEquals(respSessionId, sessionId))
                throw new InvalidDataException("SessionId mismatch in HELLO_ACK");
            string respDeviceId = ReadStringTimeout(reader, ct, "device id");
            string respName = ReadStringTimeout(reader, ct, "name");
            var respLongTermSpki = ReadBytesLp(reader, ct);
            var respEphPub = ReadBytesLp(reader, ct);
            var respSig = ReadBytesLp(reader, ct);

            var fullTranscript = BuildFullTranscript(
                Version, myDeviceId ?? string.Empty, myName ?? string.Empty, myLongTermSpki, ephPub,
                respDeviceId, respName, respLongTermSpki, respEphPub);
            if (!Crypto.Verify(respLongTermSpki, fullTranscript, respSig))
                throw new InvalidDataException("HELLO_ACK signature verification failed");

            var transcriptHash = Crypto.HashSha256(fullTranscript);
            var keys = DeriveSessionKeys(ephPriv, respEphPub, transcriptHash, sessionId);

            return Task.FromResult(new HandshakeResult
            {
                MyRole = Role.Client,
                SessionId = sessionId,
                PeerDeviceId = respDeviceId,
                PeerName = respName,
                PeerPublicKeySpki = respLongTermSpki,
                PeerFingerprint = Crypto.ComputeFingerprint(respLongTermSpki),
                PeerFingerprintHex = Crypto.ToHex(Crypto.ComputeFingerprint(respLongTermSpki)),
                Keys = keys
            });
        }

        // ============================ Server (responder) handshake ============================

        public static async Task<HandshakeResult> ServerHandshakeAsync(
            Stream stream,
            byte[] myPrivateKeyPkcs8,
            byte[] myLongTermSpki,
            string myDeviceId,
            string myName,
            CancellationToken ct)
        {
            using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
            using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
            var header = reader.ReadString();
            return await ServerHandshakeFromHeaderAsync(
                reader, writer, header, myPrivateKeyPkcs8, myLongTermSpki, myDeviceId, myName, ct);
        }

        /// <summary>
        /// Responder handshake continuing from an already-read header line.
        /// Used when the caller must peek the first string to dispatch between the
        /// secured and legacy protocols.
        /// </summary>
        public static Task<HandshakeResult> ServerHandshakeFromHeaderAsync(
            BinaryReader reader,
            BinaryWriter writer,
            string header,
            byte[] myPrivateKeyPkcs8,
            byte[] myLongTermSpki,
            string myDeviceId,
            string myName,
            CancellationToken ct)
        {
            if (header != "HELLO") throw new InvalidDataException($"Unexpected handshake header '{header}'");
            byte ver = reader.ReadByte();
            if (ver != Version) throw new InvalidDataException($"Unsupported handshake version {ver}");
            var sessionId = ReadBytesLp(reader, ct);
            string initDeviceId = ReadStringTimeout(reader, ct, "device id");
            string initName = ReadStringTimeout(reader, ct, "name");
            var initLongTermSpki = ReadBytesLp(reader, ct);
            var initEphPub = ReadBytesLp(reader, ct);
            var initSig = ReadBytesLp(reader, ct);

            var initTranscript = BuildInitTranscript(Version, initDeviceId, initName, initLongTermSpki, initEphPub);
            if (!Crypto.Verify(initLongTermSpki, initTranscript, initSig))
                throw new InvalidDataException("HELLO signature verification failed");

            var (ephPriv, ephPub) = Crypto.GenerateEcdhKeyPair();
            var fullTranscript = BuildFullTranscript(
                Version, initDeviceId, initName, initLongTermSpki, initEphPub,
                myDeviceId, myName, myLongTermSpki, ephPub);
            var respSig = Crypto.Sign(myPrivateKeyPkcs8, fullTranscript);

            writer.Write("HELLO_ACK");
            writer.Write(Version);
            WriteBytesLp(writer, sessionId);
            writer.Write(myDeviceId ?? string.Empty);
            writer.Write(myName ?? string.Empty);
            WriteBytesLp(writer, myLongTermSpki);
            WriteBytesLp(writer, ephPub);
            WriteBytesLp(writer, respSig);
            writer.Flush();

            var transcriptHash = Crypto.HashSha256(fullTranscript);
            var keys = DeriveSessionKeys(ephPriv, initEphPub, transcriptHash, sessionId);

            return Task.FromResult(new HandshakeResult
            {
                MyRole = Role.Server,
                SessionId = sessionId,
                PeerDeviceId = initDeviceId,
                PeerName = initName,
                PeerPublicKeySpki = initLongTermSpki,
                PeerFingerprint = Crypto.ComputeFingerprint(initLongTermSpki),
                PeerFingerprintHex = Crypto.ToHex(Crypto.ComputeFingerprint(initLongTermSpki)),
                Keys = keys
            });
        }

        // ============================ Low-level reader helpers ============================

        private static string ReadStringTimeout(BinaryReader reader, CancellationToken ct, string what)
        {
            if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);
            return reader.ReadString();
        }

        private static void WriteBytesLp(BinaryWriter writer, byte[] data)
        {
            writer.Write7BitEncodedInt(data?.Length ?? 0);
            if (data != null && data.Length > 0) writer.Write(data);
        }

        private static byte[] ReadBytesLp(BinaryReader reader, CancellationToken ct)
        {
            if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);
            int len = reader.Read7BitEncodedInt();
            if (len < 0 || len > 4 * 1024 * 1024)
                throw new InvalidDataException($"Oversized length-prefixed value {len}");
            var data = reader.ReadBytes(len);
            if (data.Length != len) throw new EndOfStreamException("Truncated length-prefixed value");
            return data;
        }

        private static void WriteUInt32Be(BinaryWriter w, uint value)
        {
            w.Write((byte)((value >> 24) & 0xFF));
            w.Write((byte)((value >> 16) & 0xFF));
            w.Write((byte)((value >> 8) & 0xFF));
            w.Write((byte)(value & 0xFF));
        }
    }

    /// <summary>
    /// A Stream wrapper that transparently encrypts writes into AES-256-GCM frames
    /// and decrypts reads from frames, enforcing a strict per-session sequence
    /// counter and integrity authentication on every frame.
    /// </summary>
    public sealed class SecureFrameStream : Stream
    {
        private readonly Stream _inner;
        private readonly byte[] _sendKey;
        private readonly byte[] _recvKey;
        private readonly byte[] _sessionId;
        private readonly byte _version;

        private readonly MemoryStream _pendingWrite = new();
        private readonly SemaphoreSlim _writeGate = new(1, 1);
        private readonly SemaphoreSlim _readGate = new(1, 1);

        private byte[] _pendingBuf = Array.Empty<byte>();
        private int _pendingStart;
        private int _pendingCount;
        private uint _sendSeq;
        private uint _recvSeq;
        private bool _eof;
        private bool _disposed;

        // Reused AesGcm instances (thread-safe under our semaphores)
        private readonly AesGcm _sendAes;
        private readonly AesGcm _recvAes;

        // Pre-allocated write-side buffers (avoids per-frame allocations)
        private readonly byte[] _sendHeaderBuf = new byte[P2pChannel.HeaderSize];
        private readonly byte[] _sendNonceBuf = new byte[Crypto.NonceSize];
        private readonly byte[] _sendAadBuf = new byte[4 + 1 + Crypto.SessionIdSize + 4]; // 25 bytes
        private byte[] _sendCipherBuf = Array.Empty<byte>(); // resized as needed

        // Pre-allocated read-side buffers
        private readonly byte[] _recvHeaderBuf = new byte[P2pChannel.HeaderSize];
        private readonly byte[] _recvNonceBuf = new byte[Crypto.NonceSize];
        private readonly byte[] _recvAadBuf = new byte[4 + 1 + Crypto.SessionIdSize + 4];

        public SecureFrameStream(Stream inner, byte[] sendKey, byte[] recvKey, byte[] sessionId, byte version)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            if (sendKey == null || sendKey.Length != Crypto.KeySize) throw new ArgumentException("Invalid send key", nameof(sendKey));
            if (recvKey == null || recvKey.Length != Crypto.KeySize) throw new ArgumentException("Invalid recv key", nameof(recvKey));
            if (sessionId == null || sessionId.Length != Crypto.SessionIdSize) throw new ArgumentException("Invalid session id", nameof(sessionId));
            _sendKey = (byte[])sendKey.Clone();
            _recvKey = (byte[])recvKey.Clone();
            _sessionId = (byte[])sessionId.Clone();
            _version = version;
            _sendAes = new AesGcm(_sendKey, Crypto.TagSize);
            _recvAes = new AesGcm(_recvKey, Crypto.TagSize);
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        // ============================ Write path ============================

        public override void Write(byte[] buffer, int offset, int count)
        {
            CheckNotDisposed();
            _writeGate.Wait();
            try
            {
                WriteCore(buffer, offset, count);
            }
            finally { _writeGate.Release(); }
        }

        public override void WriteByte(byte value) => Write(new[] { value }, 0, 1);

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => Task.Run(() => Write(buffer, offset, count), cancellationToken);

        public override void Flush()
        {
            CheckNotDisposed();
            _writeGate.Wait();
            try
            {
                if (_pendingWrite.Length > 0) EmitFrame();
            }
            finally { _writeGate.Release(); }
        }

        public override Task FlushAsync(CancellationToken cancellationToken)
            => Task.Run(() => Flush(), cancellationToken);

        private void WriteCore(byte[] buffer, int offset, int count)
        {
            _pendingWrite.Write(buffer, offset, count);
            if (_pendingWrite.Length >= TransferLimits.FrameChunkSize)
                EmitFrame();
            if (_pendingWrite.Length > TransferLimits.MaxFramePayloadSize)
                throw new InvalidOperationException("Pending frame payload exceeds the allowed maximum");
        }

        private void EmitFrame()
        {
            var plain = _pendingWrite.ToArray();
            _pendingWrite.SetLength(0);
            if (plain.Length == 0) return;

            uint seq = _sendSeq;
            _sendSeq = checked(_sendSeq + 1);

            // Generate nonce (reuse buffer)
            RandomNumberGenerator.Fill(_sendNonceBuf);

            // Build AAD directly into pre-allocated buffer (no MemoryStream)
            _sendAadBuf[0] = P2pChannel.Magic[0];
            _sendAadBuf[1] = P2pChannel.Magic[1];
            _sendAadBuf[2] = P2pChannel.Magic[2];
            _sendAadBuf[3] = P2pChannel.Magic[3];
            _sendAadBuf[4] = _version;
            Buffer.BlockCopy(_sessionId, 0, _sendAadBuf, 5, Crypto.SessionIdSize);
            _sendAadBuf[21] = (byte)((seq >> 24) & 0xFF);
            _sendAadBuf[22] = (byte)((seq >> 16) & 0xFF);
            _sendAadBuf[23] = (byte)((seq >> 8) & 0xFF);
            _sendAadBuf[24] = (byte)(seq & 0xFF);

            // Encrypt in-place (reuse cipher buffer)
            int cipherLen = plain.Length + Crypto.TagSize;
            if (_sendCipherBuf.Length < cipherLen)
                _sendCipherBuf = new byte[cipherLen];
            _sendAes.Encrypt(_sendNonceBuf, plain, _sendCipherBuf.AsSpan(0, plain.Length), _sendCipherBuf.AsSpan(plain.Length, Crypto.TagSize), _sendAadBuf);

            // Build header directly into pre-allocated buffer (no MemoryStream)
            _sendHeaderBuf[0] = P2pChannel.Magic[0];
            _sendHeaderBuf[1] = P2pChannel.Magic[1];
            _sendHeaderBuf[2] = P2pChannel.Magic[2];
            _sendHeaderBuf[3] = P2pChannel.Magic[3];
            _sendHeaderBuf[4] = _version;
            Buffer.BlockCopy(_sessionId, 0, _sendHeaderBuf, 5, Crypto.SessionIdSize);
            _sendHeaderBuf[21] = (byte)((seq >> 24) & 0xFF);
            _sendHeaderBuf[22] = (byte)((seq >> 16) & 0xFF);
            _sendHeaderBuf[23] = (byte)((seq >> 8) & 0xFF);
            _sendHeaderBuf[24] = (byte)(seq & 0xFF);
            Buffer.BlockCopy(_sendNonceBuf, 0, _sendHeaderBuf, 25, Crypto.NonceSize);
            _sendHeaderBuf[37] = (byte)((cipherLen >> 24) & 0xFF);
            _sendHeaderBuf[38] = (byte)((cipherLen >> 16) & 0xFF);
            _sendHeaderBuf[39] = (byte)((cipherLen >> 8) & 0xFF);
            _sendHeaderBuf[40] = (byte)(cipherLen & 0xFF);

            // Single write: header + ciphertext together (1 syscall instead of 3)
            var sendBuf = new byte[P2pChannel.HeaderSize + cipherLen];
            Buffer.BlockCopy(_sendHeaderBuf, 0, sendBuf, 0, P2pChannel.HeaderSize);
            Buffer.BlockCopy(_sendCipherBuf, 0, sendBuf, P2pChannel.HeaderSize, cipherLen);
            _inner.Write(sendBuf, 0, sendBuf.Length);
            _inner.Flush();
        }

        // ============================ Read path ============================

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (count == 0) return 0;
            CheckNotDisposed();
            _readGate.Wait();
            try
            {
                return ReadCore(buffer, offset, count);
            }
            finally { _readGate.Release(); }
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => Task.Run(() => Read(buffer, offset, count), cancellationToken);

        private int ReadCore(byte[] buffer, int offset, int count)
        {
            while (_pendingCount == 0)
            {
                if (_eof) return 0;
                if (!TryReadFrame())
                {
                    _eof = true;
                    if (_pendingCount == 0) return 0;
                    break;
                }
            }

            int toCopy = Math.Min(count, _pendingCount);
            Buffer.BlockCopy(_pendingBuf, _pendingStart, buffer, offset, toCopy);
            _pendingStart += toCopy;
            _pendingCount -= toCopy;
            if (_pendingCount == 0)
            {
                _pendingBuf = Array.Empty<byte>();
                _pendingStart = 0;
            }
            return toCopy;
        }

        private bool TryReadFrame()
        {
            if (ReadFull(_inner, _recvHeaderBuf, 0, _recvHeaderBuf.Length) != _recvHeaderBuf.Length)
                return false;

            int pos = 0;
            for (int i = 0; i < 4; i++)
                if (_recvHeaderBuf[pos++] != P2pChannel.Magic[i])
                    throw new InvalidDataException("Bad frame magic (not a FileTransferApp frame)");
            byte ver = _recvHeaderBuf[pos++];
            if (ver != _version) throw new InvalidDataException($"Bad frame version {ver}");
            for (int i = 0; i < Crypto.SessionIdSize; i++)
                if (_recvHeaderBuf[pos++] != _sessionId[i])
                    throw new InvalidDataException("Bad frame session id");
            uint seq = ReadUInt32Be(_recvHeaderBuf, ref pos);
            if (seq != _recvSeq)
                throw new InvalidDataException($"Frame sequence error: got {seq}, expected {_recvSeq} (replay?)");
            Buffer.BlockCopy(_recvHeaderBuf, pos, _recvNonceBuf, 0, Crypto.NonceSize);
            pos += Crypto.NonceSize;
            int ctLen = (int)ReadUInt32Be(_recvHeaderBuf, ref pos);
            if (ctLen < 0 || ctLen > TransferLimits.MaxFramePayloadSize + Crypto.TagSize)
                throw new InvalidDataException($"Oversized frame payload {ctLen}");

            _recvSeq = checked(_recvSeq + 1);

            var body = new byte[ctLen];
            if (ReadFull(_inner, body, 0, body.Length) != body.Length)
                throw new EndOfStreamException("Truncated frame body");

            // Build AAD directly into pre-allocated buffer
            _recvAadBuf[0] = P2pChannel.Magic[0];
            _recvAadBuf[1] = P2pChannel.Magic[1];
            _recvAadBuf[2] = P2pChannel.Magic[2];
            _recvAadBuf[3] = P2pChannel.Magic[3];
            _recvAadBuf[4] = _version;
            Buffer.BlockCopy(_sessionId, 0, _recvAadBuf, 5, Crypto.SessionIdSize);
            _recvAadBuf[21] = (byte)((seq >> 24) & 0xFF);
            _recvAadBuf[22] = (byte)((seq >> 16) & 0xFF);
            _recvAadBuf[23] = (byte)((seq >> 8) & 0xFF);
            _recvAadBuf[24] = (byte)(seq & 0xFF);

            byte[] plain;
            try
            {
                int ptLen = ctLen - Crypto.TagSize;
                if (ptLen < 0) throw new InvalidDataException("Ciphertext too short");
                plain = new byte[ptLen];
                _recvAes.Decrypt(_recvNonceBuf, body.AsSpan(0, ptLen), body.AsSpan(ptLen, Crypto.TagSize), plain, _recvAadBuf);
            }
            catch (CryptographicException)
            {
                throw new InvalidDataException("Frame authentication failed (tampered data or wrong key)");
            }

            if (_pendingCount == 0)
            {
                _pendingBuf = plain;
                _pendingStart = 0;
                _pendingCount = plain.Length;
            }
            else
            {
                var merged = new byte[_pendingCount + plain.Length];
                Buffer.BlockCopy(_pendingBuf, _pendingStart, merged, 0, _pendingCount);
                Buffer.BlockCopy(plain, 0, merged, _pendingCount, plain.Length);
                _pendingBuf = merged;
                _pendingStart = 0;
                _pendingCount = merged.Length;
            }
            return true;
        }

        // ============================ Stream plumbing ============================

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (_disposed) return;
            _disposed = true;
            if (disposing)
            {
                try { _writeGate.Dispose(); } catch { }
                try { _readGate.Dispose(); } catch { }
                _pendingWrite.Dispose();
                try { _sendAes.Dispose(); } catch { }
                try { _recvAes.Dispose(); } catch { }
            }
            base.Dispose(disposing);
        }

        private void CheckNotDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(SecureFrameStream));
        }

        private static int ReadFull(Stream s, byte[] buffer, int offset, int count)
        {
            int total = 0;
            while (total < count)
            {
                int n = s.Read(buffer, offset + total, count - total);
                if (n <= 0) break;
                total += n;
            }
            return total;
        }

        private static uint ReadUInt32Be(byte[] b, ref int pos)
        {
            uint v = (uint)((b[pos] << 24) | (b[pos + 1] << 16) | (b[pos + 2] << 8) | b[pos + 3]);
            pos += 4;
            return v;
        }

        private static void WriteUInt32Be(BinaryWriter w, uint value)
        {
            w.Write((byte)((value >> 24) & 0xFF));
            w.Write((byte)((value >> 16) & 0xFF));
            w.Write((byte)((value >> 8) & 0xFF));
            w.Write((byte)(value & 0xFF));
        }
    }
}

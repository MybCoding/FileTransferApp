using System;
using System.Buffers;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FileTransferApp.Security;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Storage;
using SkiaSharp;

namespace FileTransferApp.Services
{
    public static class Message_Service
    {
        private const int Port = 4040;
        private const int SocketBufferSize = 1024 * 1024; // 1MB
        private const int FileTransferBufferSize = 1024 * 1024; // 1MB
        private const int FileIoBufferSize = 1024 * 1024; // 1MB

        private static TcpListener _listener;
        private static bool _isListening = false;
        private static CancellationTokenSource _listenerCts = new();

        // Legacy
        public static event Action<string, string, string> TextMessageReceived;                 // (ip, senderName, message)
        public static event Action<string, string, string, string> FileMessageReceived;         // (ip, senderName, fileName, tempPath)

        // New (with DeviceId)
        public static event Action<string, string, string, string> TextMessageReceivedEx;       // (ip, senderName, senderDeviceId, message)
        public static event Action<string, string, string, string, string, long> FileMessageReceivedEx; // (ip, senderName, senderDeviceId, fileName, tempPath, fileSize)
        public static event Action<string, string, string> StatusReceived; // (ip, deviceId, status)
        public static event Action<string, string, string, string, string, long> FileReceivingStartedEx; // (ip, senderName, senderDeviceId, fileName, tempPath, fileSize)
        public static event Action<string, string, string, string, string, long, long> FileReceivingProgressEx; // (ip, senderName, senderDeviceId, fileName, tempPath, bytesReceived, totalBytes)

        // Stage 5: pairing events (raised on the UI thread when possible).
        // PairingStarted = this device is the pairing initiator (SAS shown here too).
        public static event Action<string, string, string, string, string> PairingStarted;      // (ip, peerDeviceId, peerName, sas, sessionId)
        public static event Action<string, string, string, string, string> PairingRequested;    // (ip, peerDeviceId, peerName, sas, sessionId)
        public static event Action<string, string, bool> PairingCompleted;                      // (peerDeviceId, peerName, success)

        // ============================ PAIRING (initiator) ============================

        /// <summary>Starts a pairing session against a discovered device (user-initiated).</summary>
        public static async Task<bool> PairWithAsync(string ipAddress)
        {
            if (string.IsNullOrWhiteSpace(ipAddress)) return false;
            try
            {
                var identity = DeviceIdentity.Current;
                using var client = new TcpClient { NoDelay = true };
                if (!await ConnectAsync(client, ipAddress, Port, TransferLimits.HandshakeTimeout, CancellationToken.None))
                    return false;

                using var stream = client.GetStream();
                var hs = await RunHandshakeWithTimeoutAsync(
                    P2pChannel.ClientHandshakeAsync(
                        stream, identity.PrivateKeyPkcs8, identity.PublicKeySpki,
                        identity.DeviceId, DeviceInfo.Name, CancellationToken.None));

                var sas = PairingService.ComputeSas(hs.Keys.Master, hs.Keys.TranscriptHash);

                using var frame = new SecureFrameStream(stream, hs.Keys.C2S, hs.Keys.S2C, hs.SessionId, P2pChannel.Version);
                using var reader = new BinaryReader(frame, Encoding.UTF8, leaveOpen: true);
                using var writer = new BinaryWriter(frame, Encoding.UTF8, leaveOpen: true);

                writer.Write(PairingService.Pair1);
                WriteBytesLp(writer, PairingService.Encode(new PairingMessage
                {
                    Type = PairingService.Pair1,
                    DeviceId = identity.DeviceId,
                    DeviceName = DeviceInfo.Name
                }));
                await frame.FlushAsync();

                var sessionId = PairingService.RegisterPending();
                try { MainThread.BeginInvokeOnMainThread(() => PairingStarted?.Invoke(ipAddress, hs.PeerDeviceId, hs.PeerName, sas, sessionId)); } catch { }

                using (var promptCts = new CancellationTokenSource(TransferLimits.PairingPromptTimeout))
                {
                    bool initiatorAccepted = await PairingService.WaitForConfirmationAsync(sessionId, promptCts.Token);
                    if (!initiatorAccepted)
                    {
                        writer.Write(PairingService.PairAbort);
                        await frame.FlushAsync();
                        return false;
                    }
                }

                writer.Write(PairingService.Pair2);
                WriteBytesLp(writer, PairingService.Encode(new PairingMessage
                {
                    Type = PairingService.Pair2,
                    DeviceId = identity.DeviceId,
                    DeviceName = DeviceInfo.Name,
                    Sas = sas
                }));
                await frame.FlushAsync();

                PairingMessage resp = null;
                using (var respCts = new CancellationTokenSource(TransferLimits.PairingTimeout))
                {
                    try
                    {
                        var payload = await ReadBytesLpWithTimeoutAsync(reader, respCts.Token);
                        resp = PairingService.Decode(payload);
                    }
                    catch { resp = null; }
                }

                if (resp != null && resp.Type == PairingService.PairAccept)
                {
                    TrustService.TrustAlways(hs.PeerDeviceId, hs.PeerFingerprintHex, hs.PeerPublicKeySpki);
                    try { MainThread.BeginInvokeOnMainThread(() => PairingCompleted?.Invoke(hs.PeerDeviceId, hs.PeerName, true)); } catch { }
                    return true;
                }

                try { MainThread.BeginInvokeOnMainThread(() => PairingCompleted?.Invoke(hs.PeerDeviceId, hs.PeerName, false)); } catch { }
                return false;
            }
            catch (OperationCanceledException) { return false; }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Message_Service] PairWith error: {ex.Message}");
                return false;
            }
        }

        /// <summary>Completes a pending pairing confirmation from the UI.</summary>
        public static void CompletePairing(string sessionId, bool accepted)
        {
            PairingService.Complete(sessionId, accepted);
        }

        // ============================ STATUS ============================

        public static async Task SendStatusAsync(string ipAddress, string status)
        {
            try
            {
                await SendStatusSecuredAsync(ipAddress, status);
                return;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Message_Service] SendStatus secured error: {ex.Message}");
            }

            if (!TransferLimits.AllowLegacy) return;

            try
            {
                using var client = new TcpClient { NoDelay = true };
                if (!await ConnectAsync(client, ipAddress, Port, TimeSpan.FromSeconds(2), CancellationToken.None)) return;

                using var stream = client.GetStream();
                using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);

                writer.Write("STATUS");
                writer.Write(Preferences.Get("DeviceId", string.Empty) ?? string.Empty);
                writer.Write(status ?? string.Empty);
                await stream.FlushAsync();
            }
            catch { }
        }

        private static async Task SendStatusSecuredAsync(string ipAddress, string status)
        {
            using var client = new TcpClient { NoDelay = true };
            if (!await ConnectAsync(client, ipAddress, Port, TimeSpan.FromSeconds(2), CancellationToken.None)) return;

            var (frame, _, writer) = await OpenSecuredSenderAsync(client);
            var identity = DeviceIdentity.Current;
            writer.Write("STATUS");
            writer.Write(identity.DeviceId);
            writer.Write(status ?? string.Empty);
            await frame.FlushAsync();
        }

        // ============================ TEXT MESSAGES ============================

        public static async Task<bool> SendMessageAsync(string ipAddress, string senderName, string senderDeviceId, string message, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(ipAddress) || senderName == null || message == null) return false;

            try
            {
                return await SendMessageSecuredAsync(ipAddress, senderName, senderDeviceId, message, ct);
            }
            catch (OperationCanceledException) { return false; }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Message_Service] SendMessage(TEXT2) secured error: {ex.Message}");
                if (!TransferLimits.AllowLegacy) return false;
                if (await SendMessageLegacyTEXT2Async(ipAddress, senderName, senderDeviceId, message)) return true;
                return await SendMessageAsync(ipAddress, senderName, message);
            }
        }

        private static async Task<bool> SendMessageSecuredAsync(string ipAddress, string senderName, string senderDeviceId, string message, CancellationToken ct)
        {
            using var client = new TcpClient { NoDelay = true, ReceiveBufferSize = 64 * 1024, SendBufferSize = 64 * 1024 };
            if (!await ConnectAsync(client, ipAddress, Port, TimeSpan.FromSeconds(5), ct)) return false;

            var (frame, reader, writer) = await OpenSecuredSenderAsync(client);

            writer.Write("TEXT2");
            writer.Write(senderName ?? string.Empty);
            writer.Write(senderDeviceId ?? string.Empty);
            writer.Write(message ?? string.Empty);
            await frame.FlushAsync(ct);

            var respTask = reader.ReadStringAsync();
            var completed = await Task.WhenAny(respTask, Task.Delay(5000, ct));
            return completed == respTask && respTask.Result == "ACK";
        }

        private static async Task<bool> SendMessageLegacyTEXT2Async(string ipAddress, string senderName, string senderDeviceId, string message)
        {
            try
            {
                using var client = new TcpClient { NoDelay = true, ReceiveBufferSize = 64 * 1024, SendBufferSize = 64 * 1024 };
                if (!await ConnectAsync(client, ipAddress, Port, TimeSpan.FromSeconds(5), CancellationToken.None)) return false;

                using var stream = client.GetStream();
                using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
                using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);

                writer.Write("TEXT2");
                writer.Write(senderName ?? string.Empty);
                writer.Write(senderDeviceId ?? string.Empty);
                writer.Write(message ?? string.Empty);
                await stream.FlushAsync();

                var respTask = reader.ReadStringAsync();
                var completed = await Task.WhenAny(respTask, Task.Delay(5000));
                return completed == respTask && respTask.Result == "ACK";
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Message_Service] SendMessageLegacyTEXT2 error: {ex.Message}");
                return false;
            }
        }

        public static async Task<bool> SendMessageAsync(string ipAddress, string senderName, string message)
        {
            try
            {
                using var client = new TcpClient { NoDelay = true, ReceiveBufferSize = 64 * 1024, SendBufferSize = 64 * 1024 };
                if (!await ConnectAsync(client, ipAddress, Port, TimeSpan.FromSeconds(5), CancellationToken.None)) return false;

                using var stream = client.GetStream();
                using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
                using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);

                writer.Write("TEXT");
                writer.Write(senderName ?? string.Empty);
                writer.Write(message ?? string.Empty);
                await stream.FlushAsync();

                var respTask = reader.ReadStringAsync();
                var completed = await Task.WhenAny(respTask, Task.Delay(5000));
                return completed == respTask && respTask.Result == "ACK";
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Message_Service] SendMessage(TEXT) error: {ex.Message}");
                return false;
            }
        }

        // ============================ FILES ============================

        public static async Task<bool> SendFileAsync(
            string ipAddress,
            string senderName,
            string senderDeviceId,
            string fileName,
            Stream fileStream,
            long fileSize,
            IProgress<double> progress = null,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(ipAddress) || string.IsNullOrEmpty(fileName) || fileStream == null || fileSize <= 0)
                return false;

            if (fileSize > TransferLimits.MaxIncomingFileSize)
            {
                Debug.WriteLine($"[Message_Service] SendFile rejected: '{fileName}' size={fileSize} exceeds MaxIncomingFileSize={TransferLimits.MaxIncomingFileSize}");
                return false;
            }

            if (fileStream.CanSeek) fileStream.Seek(0, SeekOrigin.Begin);

            try
            {
                return await SendFileSecuredAsync(ipAddress, senderName, senderDeviceId, fileName, fileStream, fileSize, progress, ct);
            }
            catch (OperationCanceledException) { return false; }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Message_Service] SendFile(FILE_OFFER/STREAM) secured error: {ex.Message}");
                if (!TransferLimits.AllowLegacy) return false;
                if (fileStream.CanSeek) fileStream.Seek(0, SeekOrigin.Begin);
                return await SendFileLegacyAsync(ipAddress, senderName, fileName, fileStream, fileSize, progress);
            }
        }

        private static async Task<bool> SendFileSecuredAsync(
            string ipAddress,
            string senderName,
            string senderDeviceId,
            string fileName,
            Stream fileStream,
            long fileSize,
            IProgress<double> progress,
            CancellationToken ct)
        {
            using var client = new TcpClient { NoDelay = true, ReceiveBufferSize = SocketBufferSize, SendBufferSize = SocketBufferSize };
            if (!await ConnectAsync(client, ipAddress, Port, TimeSpan.FromSeconds(5), ct)) return false;

            var (frame, reader, writer) = await OpenSecuredSenderAsync(client);

            writer.Write("FILE_OFFER");
            writer.Write(senderName ?? string.Empty);
            writer.Write(senderDeviceId ?? string.Empty);
            writer.Write(Path.GetFileName(fileName) ?? string.Empty);
            writer.Write(fileSize);
            await frame.FlushAsync(ct);

            var offerRespTask = reader.ReadStringAsync();
            var offerCompleted = await Task.WhenAny(offerRespTask, Task.Delay(TransferLimits.OfferResponseTimeout, ct));
            if (offerCompleted != offerRespTask || !offerRespTask.Result.StartsWith("ACCEPT", StringComparison.Ordinal))
                return false;

            writer.Write("FILE_STREAM");
            await frame.FlushAsync(ct);

            var buffer = ArrayPool<byte>.Shared.Rent(FileTransferBufferSize);
            try
            {
                if (fileStream.CanSeek) fileStream.Seek(0, SeekOrigin.Begin);

                long sent = 0;
                int read;
                var sw = Stopwatch.StartNew();
                var lastReport = TimeSpan.Zero;

                while ((read = await fileStream.ReadAsync(buffer, 0, buffer.Length, ct)) > 0)
                {
                    await frame.WriteAsync(buffer, 0, read, ct);
                    sent += read;

                    if (progress != null && (sw.Elapsed - lastReport > TimeSpan.FromMilliseconds(200) || sent == fileSize))
                    {
                        progress.Report(Math.Min(1.0, (double)sent / fileSize));
                        lastReport = sw.Elapsed;
                    }
                }

                await frame.FlushAsync(ct);
            }
            finally { ArrayPool<byte>.Shared.Return(buffer); }

            var finalRespTask = reader.ReadStringAsync();
            // Increased timeout for large files final ACK
            var finalCompleted = await Task.WhenAny(finalRespTask, Task.Delay(TransferLimits.FinalAckTimeout, ct));
            return finalCompleted == finalRespTask && finalRespTask.Result == "ACK";
        }

        public static async Task<bool> SendFileAsync(
            string ipAddress,
            string senderName,
            string fileName,
            Stream fileStream,
            long fileSize,
            IProgress<double> progress = null)
        {
            var deviceId = Preferences.Get("DeviceId", string.Empty);
            using var cts = new CancellationTokenSource();
            return await SendFileAsync(ipAddress, senderName, deviceId, fileName, fileStream, fileSize, progress, cts.Token);
        }

        private static async Task<bool> SendFileLegacyAsync(
            string ipAddress,
            string senderName,
            string fileName,
            Stream fileStream,
            long fileSize,
            IProgress<double> progress = null)
        {
            try
            {
                using var client = new TcpClient { NoDelay = true, ReceiveBufferSize = SocketBufferSize, SendBufferSize = SocketBufferSize };
                if (!await ConnectAsync(client, ipAddress, Port, TimeSpan.FromSeconds(5), CancellationToken.None)) return false;

                using var stream = client.GetStream();
                using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
                using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);

                writer.Write("FILE");
                writer.Write(senderName ?? string.Empty);
                writer.Write(Path.GetFileName(fileName) ?? string.Empty);
                if (fileSize > TransferLimits.MaxIncomingFileSize) return false;
                if (fileSize > int.MaxValue) throw new IOException("Legacy FILE mode does not support files > 2GB");
                writer.Write((int)fileSize);
                await stream.FlushAsync();

                var buffer = ArrayPool<byte>.Shared.Rent(FileTransferBufferSize);
                try
                {
                    if (fileStream.CanSeek) fileStream.Seek(0, SeekOrigin.Begin);
                    long sent = 0;
                    int read;
                    var sw = Stopwatch.StartNew();
                    var lastReport = TimeSpan.Zero;

                    while ((read = await fileStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                    {
                        await stream.WriteAsync(buffer, 0, read);
                        sent += read;
                        if (progress != null && (sw.Elapsed - lastReport > TimeSpan.FromMilliseconds(200) || sent == fileSize))
                        {
                            progress.Report(Math.Min(1.0, (double)sent / fileSize));
                            lastReport = sw.Elapsed;
                        }
                    }
                    await stream.FlushAsync();
                }
                finally { ArrayPool<byte>.Shared.Return(buffer); }

                var respTask = reader.ReadStringAsync();
                var completed = await Task.WhenAny(respTask, Task.Delay(15000));
                return completed == respTask && respTask.Result == "ACK";
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Message_Service] SendFileLegacy(FILE) error: {ex.Message}");
                return false;
            }
        }

        // ============================ LISTENER ============================

        public static async Task StartListenerService()
        {
            if (_isListening) return;

            try
            {
                _listenerCts = new CancellationTokenSource();
                _listener = new TcpListener(IPAddress.Any, Port);
                _listener.Start();
                _isListening = true;

                while (!_listenerCts.Token.IsCancellationRequested)
                {
#if NET7_0_OR_GREATER
                    var client = await _listener.AcceptTcpClientAsync(_listenerCts.Token);
#else
                    var acceptTask = _listener.AcceptTcpClientAsync();
                    var completed = await Task.WhenAny(acceptTask, Task.Delay(-1, _listenerCts.Token));
                    if (completed != acceptTask) break;
                    var client = acceptTask.Result;
#endif
                    _ = Task.Run(() => ProcessClientAsync(client, _listenerCts.Token));
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Message_Service] Listener error: {ex.Message}");
            }
            finally
            {
                StopListener();
            }
        }

        public static void StopListener()
        {
            try
            {
                _listenerCts?.Cancel();
                _listener?.Stop();
                _isListening = false;
            }
            catch { }
            finally
            {
                _listenerCts?.Dispose();
                _listenerCts = null;
            }
        }

        private static async Task ProcessClientAsync(TcpClient client, CancellationToken cancellationToken)
        {
            string senderIp = "N/A";
            try
            {
                senderIp = ((IPEndPoint)client.Client.RemoteEndPoint).Address.ToString();
                client.NoDelay = true;
                client.ReceiveBufferSize = SocketBufferSize;
                client.SendBufferSize = SocketBufferSize;

                using var stream = client.GetStream();
                using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
                using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);

                string messageType = await ReadFirstStringWithTimeoutAsync(reader, cancellationToken);

                // Secured (Stage 5) dispatch: the first line is HELLO.
                if (messageType == "HELLO")
                {
                    await HandleSecuredClientAsync(client, stream, reader, writer, senderIp, cancellationToken);
                    return;
                }

                if (!TransferLimits.AllowLegacy)
                {
                    // Enforce policy: plaintext peers are rejected outright. No fallback.
                    try { writer.Write("NACK|Protocol"); await stream.FlushAsync(cancellationToken); } catch { }
                    return;
                }

                // ==================== LEGACY (only when AllowLegacy) ====================
                if (messageType == "STATUS")
                {
                    string senderDeviceId = reader.ReadString();
                    string status = reader.ReadString();
                    RaiseStatusEvent(senderIp, senderDeviceId, status);
                    return;
                }
                if (messageType == "TEXT2")
                {
                    string senderName = reader.ReadString();
                    string senderDeviceId = reader.ReadString();
                    string message = reader.ReadString();

                    RaiseTextEvents(senderIp, senderName, senderDeviceId, message);
                    writer.Write("ACK");
                    await stream.FlushAsync(cancellationToken);
                    return;
                }
                else if (messageType == "TEXT")
                {
                    string senderName = reader.ReadString();
                    string message = reader.ReadString();

                    RaiseTextEvents(senderIp, senderName, null, message);
                    writer.Write("ACK");
                    await stream.FlushAsync(cancellationToken);
                    return;
                }
                else if (messageType == "FILE_OFFER")
                {
                    string senderName = reader.ReadString();
                    string senderDeviceId = reader.ReadString();
                    string fileNameRaw = reader.ReadString();
                    long fileSize = reader.ReadInt64();

                    string fileName = Path.GetFileName(fileNameRaw) ?? string.Empty;
                    if (!await TryAcceptOffer(reader, writer, stream, senderIp, senderName, senderDeviceId, fileName, fileSize, cancellationToken))
                        return;
                    return;
                }
                else if (messageType == "FILE")
                {
                    string senderName = reader.ReadString();
                    string fileNameRaw = reader.ReadString();
                    int fileSize32 = reader.ReadInt32();
                    long fileSize = fileSize32;

                    string fileName = Path.GetFileName(fileNameRaw) ?? string.Empty;
                    if (!await TryAcceptOffer(reader, writer, stream, senderIp, senderName, null, fileName, fileSize, cancellationToken))
                        return;
                    return;
                }
                else
                {
                    writer.Write("NACK|UnknownType");
                    await stream.FlushAsync(cancellationToken);
                    return;
                }
            }
            catch (OperationCanceledException) { }
            catch (TimeoutException) { }
            catch (EndOfStreamException) { }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Message_Service] ProcessClient error from {senderIp}: {ex.Message}");
            }
            finally
            {
                try { client.Close(); } catch { }
                client.Dispose();
            }
        }

        // ============================ SECURED RESPONDER ============================

        private static async Task HandleSecuredClientAsync(
            TcpClient client,
            Stream stream,
            BinaryReader rawReader,
            BinaryWriter rawWriter,
            string senderIp,
            CancellationToken ct)
        {
            var identity = DeviceIdentity.Current;

            P2pChannel.HandshakeResult hs;
            try
            {
                hs = await RunHandshakeWithTimeoutAsync(
                    P2pChannel.ServerHandshakeFromHeaderAsync(
                        rawReader, rawWriter, "HELLO",
                        identity.PrivateKeyPkcs8, identity.PublicKeySpki,
                        identity.DeviceId, DeviceInfo.Name, CancellationToken.None));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Message_Service] Secured handshake rejected from {senderIp}: {ex.Message}");
                return;
            }

            var peerDeviceId = hs.PeerDeviceId;
            var peerFingerprintHex = hs.PeerFingerprintHex;
            var peerSpki = hs.PeerPublicKeySpki;
            var peerName = hs.PeerName;

            using var frame = new SecureFrameStream(stream, hs.Keys.S2C, hs.Keys.C2S, hs.SessionId, P2pChannel.Version);
            using var reader = new BinaryReader(frame, Encoding.UTF8, leaveOpen: true);
            using var writer = new BinaryWriter(frame, Encoding.UTF8, leaveOpen: true);

            string? pairSessionId = null;
            string? pairSas = null;

            while (!ct.IsCancellationRequested)
            {
                string type;
                try
                {
                    // A pairing session legitimately pauses between PAIR1 and PAIR2
                    // while the humans compare the SAS code (up to the pairing timeout).
                    // Regular frames keep the short handshake idle timeout.
                    var frameTimeout = pairSessionId == null
                        ? TransferLimits.HandshakeTimeout
                        : TransferLimits.PairingTimeout;
                    type = await ReadFrameStringAsync(reader, frameTimeout, ct);
                }
                catch (OperationCanceledException) { return; }
                catch (TimeoutException ex)
                {
                    Debug.WriteLine($"[Message_Service] Secured frame read timed out from {senderIp}: {ex.Message}");
                    return;
                }
                catch (EndOfStreamException) { return; }
                catch (IOException) { return; }
                catch (InvalidDataException ex)
                {
                    Debug.WriteLine($"[Message_Service] Secured frame rejected from {senderIp}: {ex.Message}");
                    return;
                }

                switch (type)
                {
                    case "PAIR1":
                    {
                        byte[] payload = ReadBytesLp(reader);
                        var pm = PairingService.Decode(payload);
                        pairSas = PairingService.ComputeSas(hs.Keys.Master, hs.Keys.TranscriptHash);
                        pairSessionId = PairingService.RegisterPending();
                        try { MainThread.BeginInvokeOnMainThread(() => PairingRequested?.Invoke(senderIp, pm.DeviceId, pm.DeviceName, pairSas, pairSessionId)); } catch { }
                        break;
                    }

                    case "PAIR2":
                    {
                        byte[] payload = ReadBytesLp(reader);
                        var pm = PairingService.Decode(payload);
                        if (string.IsNullOrEmpty(pairSessionId) ||
                            string.IsNullOrEmpty(pairSas) ||
                            !string.Equals(pm.Sas, pairSas, StringComparison.Ordinal))
                        {
                            await WriteBytesAndFlushAsync(writer, frame,
                                PairingService.Encode(new PairingMessage { Type = PairingService.PairAbort }));
                            PairingService.Cleanup(pairSessionId);
                            return;
                        }

                        using (var promptCts = new CancellationTokenSource(TransferLimits.PairingPromptTimeout))
                        {
                            bool accepted = await PairingService.WaitForConfirmationAsync(pairSessionId, promptCts.Token);
                            if (accepted)
                            {
                                TrustService.TrustAlways(peerDeviceId, peerFingerprintHex, peerSpki);
                                await WriteBytesAndFlushAsync(writer, frame,
                                    PairingService.Encode(new PairingMessage
                                    {
                                        Type = PairingService.PairAccept,
                                        DeviceId = identity.DeviceId,
                                        DeviceName = DeviceInfo.Name
                                    }));
                                try { MainThread.BeginInvokeOnMainThread(() => PairingCompleted?.Invoke(peerDeviceId, peerName, true)); } catch { }
                            }
                            else
                            {
                                await WriteBytesAndFlushAsync(writer, frame,
                                    PairingService.Encode(new PairingMessage { Type = PairingService.PairAbort }));
                                try { MainThread.BeginInvokeOnMainThread(() => PairingCompleted?.Invoke(peerDeviceId, peerName, false)); } catch { }
                            }
                        }
                        return;
                    }

                    case "PAIR_ABORT":
                    {
                        PairingService.Cleanup(pairSessionId);
                        try { MainThread.BeginInvokeOnMainThread(() => PairingCompleted?.Invoke(peerDeviceId, peerName, false)); } catch { }
                        return;
                    }

                    case "STATUS":
                    {
                        string deviceId = reader.ReadString();
                        string status = reader.ReadString();
                        RaiseStatusEvent(senderIp, deviceId, status);
                        break;
                    }

                    case "TEXT2":
                    {
                        string senderName = reader.ReadString();
                        string senderDeviceId = reader.ReadString();
                        string message = reader.ReadString();

                        if (!TrustService.IsTrusted(peerDeviceId, peerFingerprintHex))
                        {
                            writer.Write("NACK|Untrusted");
                            await frame.FlushAsync(ct);
                            break;
                        }

                        RaiseTextEvents(senderIp, senderName, senderDeviceId, message);
                        writer.Write("ACK");
                        await frame.FlushAsync(ct);
                        break;
                    }

                    case "FILE_OFFER":
                    {
                        string senderName = reader.ReadString();
                        string senderDeviceId = reader.ReadString();
                        string fileNameRaw = reader.ReadString();
                        long fileSize = reader.ReadInt64();

                        string fileName = Path.GetFileName(fileNameRaw) ?? string.Empty;

                        // Trust Gate: never reserve a slot or touch storage for an untrusted peer.
                        if (!TrustService.IsTrusted(peerDeviceId, peerFingerprintHex))
                        {
                            Debug.WriteLine($"[Message_Service] FILE_OFFER rejected (untrusted) | Ip={senderIp} | DeviceId={peerDeviceId} | File={fileName}");
                            writer.Write("NACK|Untrusted");
                            await frame.FlushAsync(ct);
                            return;
                        }

                        await TryAcceptOfferSecuredAsync(reader, writer, frame, senderIp, senderName, senderDeviceId, fileName, fileSize, ct);
                        return;
                    }

                    default:
                        writer.Write("NACK|Protocol");
                        await frame.FlushAsync(ct);
                        return;
                }
            }
        }

        // ============================ OFFER ACCEPTANCE (shared core) ============================

        /// <summary>Validates and receives a FILE_OFFER/FILE on a RAW (legacy) connection.</summary>
        private static async Task<bool> TryAcceptOffer(
            BinaryReader reader,
            BinaryWriter writer,
            Stream stream,
            string senderIp,
            string senderName,
            string senderDeviceId,
            string fileName,
            long fileSize,
            CancellationToken ct)
        {
            string rejectReason;
            long availableSpace = 0;
            if (!FileSizeValidator.ValidateFileName(fileName, out rejectReason) ||
                !FileSizeValidator.ValidateFileSize(fileSize, out rejectReason))
            {
                LogOfferReject(senderIp, senderName, senderDeviceId, fileName, fileSize, availableSpace, rejectReason);
                writer.Write($"NACK|{rejectReason}");
                await stream.FlushAsync(ct);
                return false;
            }
            if (!ValidateReceiveSpace(fileSize, out rejectReason, out availableSpace))
            {
                LogOfferReject(senderIp, senderName, senderDeviceId, fileName, fileSize, availableSpace, rejectReason);
                writer.Write($"NACK|{rejectReason}");
                await stream.FlushAsync(ct);
                return false;
            }
            if (!IncomingTransferGuard.TryReserveSlot())
            {
                LogOfferReject(senderIp, senderName, senderDeviceId, fileName, fileSize, availableSpace, "Busy");
                writer.Write("NACK|Busy");
                await stream.FlushAsync(ct);
                return false;
            }

            try
            {
                return await ReceiveOfferBodyAsync(reader, writer, stream, senderIp, senderName, senderDeviceId, fileName, fileSize, ct);
            }
            finally
            {
                IncomingTransferGuard.ReleaseSlot();
            }
        }

        /// <summary>Validates and receives a FILE_OFFER on a SECURED connection.</summary>
        private static async Task<bool> TryAcceptOfferSecuredAsync(
            BinaryReader reader,
            BinaryWriter writer,
            SecureFrameStream frame,
            string senderIp,
            string senderName,
            string senderDeviceId,
            string fileName,
            long fileSize,
            CancellationToken ct)
        {
            string rejectReason;
            long availableSpace = 0;
            if (!FileSizeValidator.ValidateFileName(fileName, out rejectReason) ||
                !FileSizeValidator.ValidateFileSize(fileSize, out rejectReason))
            {
                LogOfferReject(senderIp, senderName, senderDeviceId, fileName, fileSize, availableSpace, rejectReason);
                writer.Write($"NACK|{rejectReason}");
                await frame.FlushAsync(ct);
                return false;
            }
            if (!ValidateReceiveSpace(fileSize, out rejectReason, out availableSpace))
            {
                LogOfferReject(senderIp, senderName, senderDeviceId, fileName, fileSize, availableSpace, rejectReason);
                writer.Write($"NACK|{rejectReason}");
                await frame.FlushAsync(ct);
                return false;
            }
            if (!IncomingTransferGuard.TryReserveSlot())
            {
                LogOfferReject(senderIp, senderName, senderDeviceId, fileName, fileSize, availableSpace, "Busy");
                writer.Write("NACK|Busy");
                await frame.FlushAsync(ct);
                return false;
            }

            try
            {
                return await ReceiveOfferBodyAsync(reader, writer, frame, senderIp, senderName, senderDeviceId, fileName, fileSize, ct);
            }
            finally
            {
                IncomingTransferGuard.ReleaseSlot();
            }
        }

        private static async Task<bool> ReceiveOfferBodyAsync(
            BinaryReader reader,
            BinaryWriter writer,
            Stream stream,
            string senderIp,
            string senderName,
            string senderDeviceId,
            string fileName,
            long fileSize,
            CancellationToken ct)
        {
            bool received = false;
            try
            {
                writer.Write("ACCEPT");
                await stream.FlushAsync(ct);

                var readNextTask = reader.ReadStringAsync();
                if (!await TransferTimeout.TryWaitAsync(readNextTask, TransferLimits.IncomingFileStreamTimeout, ct))
                {
                    writer.Write("NACK|Timeout");
                    await stream.FlushAsync(ct);
                    return false;
                }

                string next = await readNextTask;
                if (next != "FILE_STREAM")
                {
                    writer.Write("NACK|Protocol");
                    await stream.FlushAsync(ct);
                    return false;
                }

                string tempPath = IncomingTransferGuard.CreateTempPath(FileSystem.CacheDirectory, fileName);
                try
                {
                    RaiseFileReceivingStarted(senderIp, senderName, senderDeviceId, fileName, tempPath, fileSize);
                    _ = SendStatusAsync(senderIp, GetReceivingStatus(fileName));

                    var result = await ReceiveFileCoreAsync(stream, ct, tempPath, fileSize,
                        (bytes, total) => RaiseFileReceivingProgress(senderIp, senderName, senderDeviceId, fileName, tempPath, bytes, total));

                    if (result == ReceiveResult.Success)
                    {
                        received = true;
                        RaiseFileEvents(senderIp, senderName, senderDeviceId, fileName, tempPath, fileSize);
                        writer.Write("ACK");
                        await stream.FlushAsync(ct);
                    }
                    else
                    {
                        string reason = result switch
                        {
                            ReceiveResult.SizeOverflow => "SizeOverflow",
                            ReceiveResult.Stalled => "Timeout",
                            _ => "SizeMismatch"
                        };
                        writer.Write($"NACK|{reason}");
                        await stream.FlushAsync(ct);
                    }
                }
                finally
                {
                    if (!received) TryDeleteTemp(tempPath);
                    _ = SendStatusAsync(senderIp, null);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Message_Service] ReceiveOfferBody error: {ex.Message}");
                return false;
            }
            return received;
        }

        private enum ReceiveResult
        {
            Success,
            SizeOverflow,
            Stalled,
            SizeMismatch
        }

        private static async Task<ReceiveResult> ReceiveFileCoreAsync(Stream stream, CancellationToken ct, string tempPath, long expectedSize, Action<long, long> onProgress)
        {
            long totalBytes = 0;
            var sw = Stopwatch.StartNew();
            var lastReport = TimeSpan.Zero;
            var buffer = ArrayPool<byte>.Shared.Rent(FileTransferBufferSize);
            try
            {
                using var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, FileIoBufferSize, useAsync: true);
                while (totalBytes < expectedSize)
                {
                    var readTask = stream.ReadAsync(buffer, 0, buffer.Length, ct);
                    if (!await TransferTimeout.TryWaitAsync(readTask, TransferLimits.IncomingIdleTimeout, ct))
                        return ReceiveResult.Stalled;

                    int n = await readTask;
                    if (n <= 0) break;

                    if (!FileSizeValidator.TryLimitWriteChunk(n, totalBytes, expectedSize, out int toWrite))
                        return ReceiveResult.SizeOverflow;

                    await fs.WriteAsync(buffer, 0, toWrite, ct);
                    totalBytes += toWrite;

                    if (sw.Elapsed - lastReport > TimeSpan.FromMilliseconds(200) || totalBytes == expectedSize)
                    {
                        onProgress?.Invoke(totalBytes, expectedSize);
                        lastReport = sw.Elapsed;
                    }
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            return totalBytes == expectedSize ? ReceiveResult.Success : ReceiveResult.SizeMismatch;
        }

        // ============================ SECURED SENDER HELPERS ============================

        private static async Task<(SecureFrameStream frame, BinaryReader reader, BinaryWriter writer)> OpenSecuredSenderAsync(TcpClient client)
        {
            var identity = DeviceIdentity.Current;
            var stream = client.GetStream();
            var hs = await RunHandshakeWithTimeoutAsync(
                P2pChannel.ClientHandshakeAsync(
                    stream, identity.PrivateKeyPkcs8, identity.PublicKeySpki,
                    identity.DeviceId, DeviceInfo.Name, CancellationToken.None));
            var frame = new SecureFrameStream(stream, hs.Keys.C2S, hs.Keys.S2C, hs.SessionId, P2pChannel.Version);
            var reader = new BinaryReader(frame, Encoding.UTF8, leaveOpen: true);
            var writer = new BinaryWriter(frame, Encoding.UTF8, leaveOpen: true);
            return (frame, reader, writer);
        }

        private static async Task<P2pChannel.HandshakeResult> RunHandshakeWithTimeoutAsync(Task<P2pChannel.HandshakeResult> handshakeTask)
        {
            var done = await Task.WhenAny(handshakeTask, Task.Delay(TransferLimits.HandshakeTimeout));
            if (done != handshakeTask) throw new TimeoutException("Handshake timed out");
            return await handshakeTask;
        }

        private static async Task WriteBytesAndFlushAsync(BinaryWriter writer, SecureFrameStream frame, byte[] payload)
        {
            WriteBytesLp(writer, payload);
            await frame.FlushAsync();
        }

        private static async Task<string> ReadFrameStringAsync(BinaryReader reader, TimeSpan timeout, CancellationToken ct)
        {
            var task = Task.Run(reader.ReadString);
            var done = await Task.WhenAny(task, Task.Delay(timeout, ct));
            if (done != task) throw new TimeoutException("Timed out waiting for a secured frame");
            return await task;
        }

        private static async Task<byte[]> ReadBytesLpWithTimeoutAsync(BinaryReader reader, CancellationToken ct)
        {
            var task = Task.Run(() => ReadBytesLp(reader));
            var done = await Task.WhenAny(task, Task.Delay(TimeSpan.FromMinutes(3), ct));
            if (done != task) throw new TimeoutException("Timed out waiting for a secured frame");
            return await task;
        }

        private static void WriteBytesLp(BinaryWriter writer, byte[] payload)
        {
            writer.Write7BitEncodedInt(payload?.Length ?? 0);
            if (payload != null && payload.Length > 0) writer.Write(payload);
        }

        private static byte[] ReadBytesLp(BinaryReader reader)
        {
            int len = reader.Read7BitEncodedInt();
            if (len < 0 || len > 4 * 1024 * 1024)
                throw new InvalidDataException($"Oversized length-prefixed value {len}");
            var data = reader.ReadBytes(len);
            if (data.Length != len) throw new EndOfStreamException("Truncated length-prefixed value");
            return data;
        }

        private static async Task<string> ReadFirstStringWithTimeoutAsync(BinaryReader reader, CancellationToken ct)
        {
            var task = Task.Run(reader.ReadString);
            var done = await Task.WhenAny(task, Task.Delay(TransferLimits.HandshakeTimeout, ct));
            if (done != task) throw new TimeoutException("Timed out waiting for the first message");
            return await task;
        }

        // ============================ VALIDATION / EVENTS (unchanged) ============================

        private static bool ValidateReceiveSpace(long fileSize, out string reason, out long availableBytes)
        {
            reason = null;
            availableBytes = 0;

            long required;
            if (!DiskSpaceValidator.TryComputeRequiredSpace(fileSize, TransferLimits.MinimumFreeDiskSpace, out required))
            {
                reason = "NoSpace";
                return false;
            }

            string cacheDir = FileSystem.CacheDirectory;
            long cacheFree = DiskSpaceValidator.GetCacheFreeSpace(cacheDir);
            if (cacheFree < 0 || cacheFree < required)
            {
                availableBytes = cacheFree;
                reason = "NoSpace";
                return false;
            }

            long finalFree = DiskSpaceValidator.GetFinalStorageFreeSpace();
            if (finalFree >= 0 && finalFree < required)
            {
                availableBytes = finalFree;
                reason = "NoSpace";
                return false;
            }

            availableBytes = cacheFree;
            return true;
        }

        private static void LogOfferReject(string senderIp, string senderName, string senderDeviceId, string fileName, long requestedSize, long availableSpace, string reason)
        {
            Debug.WriteLine($"[Message_Service] FILE_OFFER rejected | Ip={senderIp} | Sender={senderName} | DeviceId={senderDeviceId} | File={fileName} | RequestedSize={requestedSize} | MaxAllowed={TransferLimits.MaxIncomingFileSize} | AvailableSpace={availableSpace} | Reason={reason}");
        }

        private static void TryDeleteTemp(string tempPath)
        {
            if (string.IsNullOrEmpty(tempPath)) return;
            try
            {
                if (File.Exists(tempPath)) File.Delete(tempPath);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Message_Service] Temp file delete error: {ex.Message}");
            }
        }

        private static void RaiseTextEvents(string ip, string senderName, string senderDeviceId, string message)
        {
            try
            {
                MainThread.BeginInvokeOnMainThread(() =>
                {
                    if (TextMessageReceivedEx != null)
                        TextMessageReceivedEx?.Invoke(ip, senderName, senderDeviceId, message);
                    else
                        TextMessageReceived?.Invoke(ip, senderName, message);
                });
            }
            catch { }
        }

        private static void RaiseFileEvents(string ip, string senderName, string senderDeviceId, string fileName, string tempPath, long fileSize)
        {
            try
            {
                MainThread.BeginInvokeOnMainThread(() =>
                {
                    if (FileMessageReceivedEx != null)
                        FileMessageReceivedEx?.Invoke(ip, senderName, senderDeviceId, fileName, tempPath, fileSize);
                    else
                        FileMessageReceived?.Invoke(ip, senderName, fileName, tempPath);
                });
            }
            catch { }
        }

        private static void RaiseStatusEvent(string ip, string deviceId, string status)
        {
            try
            {
                MainThread.BeginInvokeOnMainThread(() =>
                {
                    StatusReceived?.Invoke(ip, deviceId, status);
                });
            }
            catch { }
        }

        private static void RaiseFileReceivingStarted(string ip, string senderName, string senderDeviceId, string fileName, string tempPath, long fileSize)
        {
            try
            {
                MainThread.BeginInvokeOnMainThread(() =>
                {
                    FileReceivingStartedEx?.Invoke(ip, senderName, senderDeviceId, fileName, tempPath, fileSize);
                });
            }
            catch { }
        }

        private static void RaiseFileReceivingProgress(string ip, string senderName, string senderDeviceId, string fileName, string tempPath, long bytesReceived, long totalBytes)
        {
            try
            {
                MainThread.BeginInvokeOnMainThread(() =>
                {
                    FileReceivingProgressEx?.Invoke(ip, senderName, senderDeviceId, fileName, tempPath, bytesReceived, totalBytes);
                });
            }
            catch { }
        }

        private static string GetReceivingStatus(string fileName)
        {
            try
            {
                var ext = Path.GetExtension(fileName)?.ToLowerInvariant();
                return ext switch
                {
                    ".jpg" or ".jpeg" or ".png" or ".gif" or ".bmp" or ".webp" => "RECEIVING_IMAGE",
                    ".mp4" or ".mkv" or ".avi" or ".mov" or ".wmv" => "RECEIVING_VIDEO",
                    _ => "RECEIVING_FILE"
                };
            }
            catch
            {
                return "RECEIVING_FILE";
            }
        }

        private static async Task<bool> ConnectAsync(TcpClient client, string host, int port, TimeSpan timeout, CancellationToken ct)
        {
            var connectTask = client.ConnectAsync(host, port);
            var completed = await Task.WhenAny(connectTask, Task.Delay(timeout, ct));
            return completed == connectTask && client.Connected;
        }

        public static bool IsImageFile(string filePath)
        {
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath)) return false;
            try
            {
                using var stream = File.OpenRead(filePath);
                Span<byte> header = stackalloc byte[4];
                int read = stream.Read(header);
                if (read < 2) return false;
                if (read >= 4 && header[0] == 0x89 && header[1] == 0x50 && header[2] == 0x4E && header[3] == 0x47) return true; // PNG
                if (header[0] == 0xFF && header[1] == 0xD8) return true; // JPEG
                if (read >= 3 && header[0] == 0x47 && header[1] == 0x49 && header[2] == 0x46) return true; // GIF
                if (header[0] == 0x42 && header[1] == 0x4D) return true; // BMP
            }
            catch { }
            return false;
        }

        public static byte[] GenerateImagePreviewFromFile(string filePath)
        {
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath)) return null;
            try
            {
                using var fs = File.OpenRead(filePath);
                using var codec = SKCodec.Create(fs);
                if (codec == null) return null;
                var origin = codec.EncodedOrigin;
                using var original = SKBitmap.Decode(codec);
                if (original == null) return null;

                using var oriented = OrientBitmap(original, origin);
                if (oriented == null) return null;

                int w = 300;
                int h = Math.Max(1, (int)(oriented.Height * (float)w / oriented.Width));
                using var resized = oriented.Resize(new SKImageInfo(w, h), SKFilterQuality.High);
                if (resized == null) return null;

                using var img = SKImage.FromBitmap(resized);
                using var ms = new MemoryStream();
                img.Encode(SKEncodedImageFormat.Jpeg, 80).SaveTo(ms);
                return ms.ToArray();
            }
            catch { return null; }
        }

        public static byte[] GenerateImagePreview(byte[] imageData)
        {
            if (imageData == null || imageData.Length == 0) return null;
            try
            {
                using var ms = new MemoryStream(imageData);
                using var codec = SKCodec.Create(ms);
                if (codec == null) return null;
                var origin = codec.EncodedOrigin;
                using var original = SKBitmap.Decode(codec);
                if (original == null) return null;

                using var oriented = OrientBitmap(original, origin);
                if (oriented == null) return null;

                int w = 300;
                int h = Math.Max(1, (int)(oriented.Height * (float)w / oriented.Width));
                using var resized = oriented.Resize(new SKImageInfo(w, h), SKFilterQuality.High);
                if (resized == null) return null;

                using var img = SKImage.FromBitmap(resized);
                using var os = new MemoryStream();
                img.Encode(SKEncodedImageFormat.Jpeg, 80).SaveTo(os);
                return os.ToArray();
            }
            catch { return null; }
        }

        private static SKBitmap OrientBitmap(SKBitmap bitmap, SKEncodedOrigin origin)
        {
            if (bitmap == null) return null;
            switch (origin)
            {
                case SKEncodedOrigin.TopLeft:
                    return bitmap.Copy();
                case SKEncodedOrigin.RightTop:
                    return RotateBitmap(bitmap, 90);
                case SKEncodedOrigin.BottomRight:
                    return RotateBitmap(bitmap, 180);
                case SKEncodedOrigin.LeftBottom:
                    return RotateBitmap(bitmap, 270);
                default:
                    return bitmap.Copy();
            }
        }

        private static SKBitmap RotateBitmap(SKBitmap source, float degrees)
        {
            var rotated = new SKBitmap(source.Height, source.Width);
            using var canvas = new SKCanvas(rotated);
            canvas.Clear();
            canvas.Translate(rotated.Width / 2f, rotated.Height / 2f);
            canvas.RotateDegrees(degrees);
            canvas.Translate(-source.Width / 2f, -source.Height / 2f);
            canvas.DrawBitmap(source, 0, 0);
            return rotated;
        }
    }

    public static class BinaryReaderExtensions
    {
        public static Task<string> ReadStringAsync(this BinaryReader reader) => Task.Run(reader.ReadString);
        public static Task<int> ReadInt32Async(this BinaryReader reader) => Task.Run(reader.ReadInt32);
        public static Task<long> ReadInt64Async(this BinaryReader reader) => Task.Run(reader.ReadInt64);
    }
}

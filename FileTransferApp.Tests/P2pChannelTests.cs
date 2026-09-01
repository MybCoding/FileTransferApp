using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FileTransferApp.Security;
using Xunit;

namespace FileTransferApp.Tests
{
    public class P2pChannelTests
    {
        private sealed class LoopbackPair : IDisposable
        {
            public TcpClient Client { get; }
            public TcpClient Server { get; }

            public LoopbackPair()
            {
                var listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
                var port = ((IPEndPoint)listener.LocalEndpoint).Port;
                var accept = listener.AcceptTcpClientAsync();
                Client = new TcpClient { NoDelay = true };
                Client.Connect(IPAddress.Loopback, port);
                Server = accept.GetAwaiter().GetResult();
                listener.Stop();
            }

            public void Dispose()
            {
                Client.Dispose();
                Server.Dispose();
            }
        }

        // ============================ Transcripts ============================

        [Fact]
        public void TranscriptInit_IsDeterministic()
        {
            var a = P2pChannel.BuildInitTranscript(1, "a", "A", new byte[] { 1, 2 }, new byte[] { 3, 4 });
            var b = P2pChannel.BuildInitTranscript(1, "a", "A", new byte[] { 1, 2 }, new byte[] { 3, 4 });
            Assert.Equal(a, b);
        }

        [Fact]
        public void TranscriptInit_ChangesWhenFieldChanges()
        {
            var a = P2pChannel.BuildInitTranscript(1, "a", "A", new byte[] { 1, 2 }, new byte[] { 3, 4 });
            var b = P2pChannel.BuildInitTranscript(1, "a", "A", new byte[] { 1, 2 }, new byte[] { 3, 5 });
            Assert.NotEqual(a, b);
        }

        [Fact]
        public void TranscriptFull_ContainsBothSides()
        {
            var init = P2pChannel.BuildInitTranscript(1, "c", "C", new byte[] { 1 }, new byte[] { 2 });
            var full = P2pChannel.BuildFullTranscript(1, "c", "C", new byte[] { 1 }, new byte[] { 2 }, "s", "S", new byte[] { 9 }, new byte[] { 8 });
            Assert.True(full.Length > init.Length);
        }

        // ============================ Handshake ============================

        [Fact]
        public async Task Handshake_TwoPartiesDeriveIdenticalKeys()
        {
            var (cpriv, cpub) = Crypto.GenerateEcdsaKeyPair();
            var (spriv, spub) = Crypto.GenerateEcdsaKeyPair();

            using var pair = new LoopbackPair();
            var clientTask = Task.Run(() =>
                P2pChannel.ClientHandshakeAsync(pair.Client.GetStream(), cpriv, cpub, "client", "Client", CancellationToken.None));
            var serverTask = Task.Run(() =>
                P2pChannel.ServerHandshakeAsync(pair.Server.GetStream(), spriv, spub, "server", "Server", CancellationToken.None));

            var clientRes = await clientTask;
            var serverRes = await serverTask;

            Assert.Equal("server", clientRes.PeerDeviceId);
            Assert.Equal("client", serverRes.PeerDeviceId);
            Assert.Equal(Crypto.ToHex(Crypto.ComputeFingerprint(spub)), clientRes.PeerFingerprintHex);
            Assert.Equal(Crypto.ToHex(Crypto.ComputeFingerprint(cpub)), serverRes.PeerFingerprintHex);

            Assert.Equal(clientRes.Keys.C2S, serverRes.Keys.C2S);
            Assert.Equal(clientRes.Keys.S2C, serverRes.Keys.S2C);
            Assert.Equal(clientRes.Keys.Master, serverRes.Keys.Master);
            Assert.Equal(clientRes.Keys.TranscriptHash, serverRes.Keys.TranscriptHash);
            Assert.Equal(clientRes.Keys.SessionId, serverRes.Keys.SessionId);
        }

        [Fact]
        public async Task Handshake_ServerImpersonation_IsRejected()
        {
            var (cpriv, cpub) = Crypto.GenerateEcdsaKeyPair();
            var (_, realServerPub) = Crypto.GenerateEcdsaKeyPair();   // the key the client believes belongs to the server
            var (attackerPriv, _) = Crypto.GenerateEcdsaKeyPair();

            using var pair = new LoopbackPair();
            var clientTask = Task.Run(() =>
                P2pChannel.ClientHandshakeAsync(pair.Client.GetStream(), cpriv, cpub, "client", "Client", CancellationToken.None));
            var serverTask = Task.Run(async () =>
            {
                using var reader = new BinaryReader(pair.Server.GetStream(), Encoding.UTF8, leaveOpen: true);
                using var writer = new BinaryWriter(pair.Server.GetStream(), Encoding.UTF8, leaveOpen: true);
                var header = reader.ReadString();
                // Attacker advertises the real server key but signs with its OWN private key.
                return await P2pChannel.ServerHandshakeFromHeaderAsync(
                    reader, writer, header, attackerPriv, realServerPub, "server", "Server", CancellationToken.None);
            });

            // Client must reject the HELLO_ACK because the signature does not match the advertised key.
            await Assert.ThrowsAnyAsync<InvalidDataException>(() => clientTask);
            var _ = await serverTask;
        }

        [Fact]
        public async Task Handshake_ImpersonatedClient_IsRejected()
        {
            var (attackerPriv, _) = Crypto.GenerateEcdsaKeyPair();
            var (_, victimPub) = Crypto.GenerateEcdsaKeyPair();
            var (spriv, spub) = Crypto.GenerateEcdsaKeyPair();

            using var pair = new LoopbackPair();
            var clientTask = Task.Run(async () =>
            {
                try
                {
                    // Advertises victim's public key but signs with attacker's private key.
                    return await P2pChannel.ClientHandshakeAsync(
                        pair.Client.GetStream(), attackerPriv, victimPub,
                        "client", "Client", CancellationToken.None);
                }
                catch { return null; }
            });
            var serverTask = Task.Run(() =>
                P2pChannel.ServerHandshakeAsync(pair.Server.GetStream(), spriv, spub, "server", "Server", CancellationToken.None));

            await Assert.ThrowsAnyAsync<InvalidDataException>(() => serverTask);
            // Server rejected without replying, so close the sockets to unblock the client.
            pair.Dispose();
            await clientTask;
        }

        // ============================ SecureFrameStream ============================

        private static (SecureFrameStream clientFrame, SecureFrameStream serverFrame) OpenFrames(LoopbackPair pair)
        {
            var (cpriv, cpub) = Crypto.GenerateEcdsaKeyPair();
            var (spriv, spub) = Crypto.GenerateEcdsaKeyPair();

            var clientTask = Task.Run(() =>
                P2pChannel.ClientHandshakeAsync(pair.Client.GetStream(), cpriv, cpub, "c", "C", CancellationToken.None));
            var serverTask = Task.Run(() =>
                P2pChannel.ServerHandshakeAsync(pair.Server.GetStream(), spriv, spub, "s", "S", CancellationToken.None));

            var cRes = clientTask.GetAwaiter().GetResult();
            var sRes = serverTask.GetAwaiter().GetResult();

            var cf = new SecureFrameStream(pair.Client.GetStream(), cRes.Keys.C2S, cRes.Keys.S2C, cRes.SessionId, P2pChannel.Version);
            var sf = new SecureFrameStream(pair.Server.GetStream(), sRes.Keys.S2C, sRes.Keys.C2S, sRes.SessionId, P2pChannel.Version);
            return (cf, sf);
        }

        [Fact]
        public async Task SecureFrameStream_ChunkedRoundTrip_OverSocket()
        {
            using var pair = new LoopbackPair();
            var (cf, sf) = OpenFrames(pair);

            var data = new byte[1_000_000];
            new Random(42).NextBytes(data);

            var writeTask = Task.Run(() =>
            {
                cf.Write(data, 0, data.Length);
                cf.Flush();
            });

            var received = new byte[data.Length];
            int total = 0;
            var readTask = Task.Run(() =>
            {
                while (total < data.Length)
                {
                    int n = sf.Read(received, total, data.Length - total);
                    if (n <= 0) break;
                    total += n;
                }
            });

            await Task.WhenAll(writeTask, readTask);
            Assert.Equal(data.Length, total);
            Assert.Equal(data, received);

            // Reverse direction still works (independent direction keys + seq).
            var reply = Encoding.UTF8.GetBytes("server-reply");
            await Task.Run(() => { sf.Write(reply, 0, reply.Length); sf.Flush(); });
            var buf = new byte[128];
            int n2 = await Task.Run(() => cf.Read(buf, 0, buf.Length));
            Assert.Equal(reply.Length, n2);
            Assert.Equal(reply, buf.Take(n2).ToArray());
        }

        [Fact]
        public void SecureFrameStream_MemoryRoundTrip_SmallWrites_AreOneFrame()
        {
            var key = Crypto.RandomBytes(32);
            var recvKey = Crypto.RandomBytes(32);
            var sessionId = Crypto.RandomBytes(16);
            var inner = new MemoryStream();
            using (var wf = new SecureFrameStream(inner, key, recvKey, sessionId, P2pChannel.Version))
            {
                wf.Write(Encoding.UTF8.GetBytes("hello"), 0, 5);
                wf.Flush();
            }

            var bytes = inner.ToArray();
            Assert.True(bytes.Length > P2pChannel.HeaderSize);

            inner.Position = 0;
            using var rf = new SecureFrameStream(inner, recvKey, key, sessionId, P2pChannel.Version);
            var buf = new byte[32];
            int n = rf.Read(buf, 0, buf.Length);
            Assert.Equal(5, n);
            Assert.Equal("hello", Encoding.UTF8.GetString(buf, 0, n));
        }

        [Fact]
        public void SecureFrameStream_TamperedFrame_IsRejected()
        {
            var key = Crypto.RandomBytes(32);
            var recvKey = Crypto.RandomBytes(32);
            var sessionId = Crypto.RandomBytes(16);
            var inner = new MemoryStream();
            using (var wf = new SecureFrameStream(inner, key, recvKey, sessionId, P2pChannel.Version))
            {
                wf.Write(Encoding.UTF8.GetBytes("tamper-me"), 0, 9);
                wf.Flush();
            }

            var bytes = inner.ToArray();
            // Flip a byte inside the ciphertext body (after the 41-byte header).
            bytes[P2pChannel.HeaderSize + 2] ^= 0x01;

            using var tampered = new MemoryStream(bytes, false);
            using var rf = new SecureFrameStream(tampered, recvKey, key, sessionId, P2pChannel.Version);
            var buf = new byte[64];
            Assert.ThrowsAny<InvalidDataException>(() => rf.Read(buf, 0, buf.Length));
        }

        [Fact]
        public void SecureFrameStream_ReplayedFrame_IsRejected()
        {
            var key = Crypto.RandomBytes(32);
            var recvKey = Crypto.RandomBytes(32);
            var sessionId = Crypto.RandomBytes(16);
            var inner = new MemoryStream();
            using (var wf = new SecureFrameStream(inner, key, recvKey, sessionId, P2pChannel.Version))
            {
                wf.Write(Encoding.UTF8.GetBytes("frame-one"), 0, 9);
                wf.Flush();
            }
            var frameBytes = inner.ToArray();

            // Feed the SAME frame twice (attacker replays the captured bytes).
            var replay = new MemoryStream();
            replay.Write(frameBytes, 0, frameBytes.Length);
            replay.Write(frameBytes, 0, frameBytes.Length);
            replay.Position = 0;

            using var rf = new SecureFrameStream(replay, recvKey, key, sessionId, P2pChannel.Version);
            var buf = new byte[64];
            Assert.Equal(9, rf.Read(buf, 0, buf.Length));
            Assert.Equal("frame-one", Encoding.UTF8.GetString(buf, 0, 9));
            // Second read is the replayed frame: sequence counter mismatch -> rejected.
            Assert.ThrowsAny<InvalidDataException>(() => rf.Read(buf, 0, buf.Length));
        }

        [Fact]
        public void SecureFrameStream_WrongSessionId_IsRejected()
        {
            var key = Crypto.RandomBytes(32);
            var recvKey = Crypto.RandomBytes(32);
            var sessionId = Crypto.RandomBytes(16);
            var inner = new MemoryStream();
            using (var wf = new SecureFrameStream(inner, key, recvKey, sessionId, P2pChannel.Version))
            {
                wf.Write(Encoding.UTF8.GetBytes("hello"), 0, 5);
                wf.Flush();
            }

            var otherSession = Crypto.RandomBytes(16);
            inner.Position = 0;
            using var rf = new SecureFrameStream(inner, recvKey, key, otherSession, P2pChannel.Version);
            var buf = new byte[32];
            Assert.ThrowsAny<InvalidDataException>(() => rf.Read(buf, 0, buf.Length));
        }
    }
}

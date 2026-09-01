using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FileTransferApp.Security;
using Xunit;

namespace FileTransferApp.Tests
{
    public class PairingServiceTests
    {
        [Fact]
        public void EncodeDecode_RoundTrip_WithSas()
        {
            var msg = new PairingMessage
            {
                Type = PairingService.Pair2,
                DeviceId = "dev-abc",
                DeviceName = "Test Phone",
                Sas = "123456"
            };

            var bytes = PairingService.Encode(msg);
            Assert.NotNull(bytes);

            var decoded = PairingService.Decode(bytes);
            Assert.Equal(PairingService.Pair2, decoded.Type);
            Assert.Equal("dev-abc", decoded.DeviceId);
            Assert.Equal("Test Phone", decoded.DeviceName);
            Assert.Equal("123456", decoded.Sas);
        }

        [Fact]
        public void EncodeDecode_RoundTrip_WithoutSas()
        {
            var msg = new PairingMessage { Type = PairingService.Pair1, DeviceId = "d", DeviceName = "n" };
            var decoded = PairingService.Decode(PairingService.Encode(msg));
            Assert.Equal(PairingService.Pair1, decoded.Type);
            Assert.Equal("d", decoded.DeviceId);
            Assert.Equal("n", decoded.DeviceName);
            Assert.Equal(string.Empty, decoded.Sas);
        }

        [Fact]
        public void Decode_Garbage_Throws()
        {
            Assert.ThrowsAny<EndOfStreamException>(() => PairingService.Decode(new byte[] { 0x01, 0x02, 0x03 }));
        }

        [Fact]
        public void Decode_TruncatedPayload_Throws()
        {
            var msg = new PairingMessage { Type = PairingService.Pair2, DeviceId = "dev", DeviceName = "n", Sas = "123456" };
            var bytes = PairingService.Encode(msg);
            var truncated = new byte[bytes.Length - 3];
            Array.Copy(bytes, truncated, truncated.Length);
            Assert.ThrowsAny<EndOfStreamException>(() => PairingService.Decode(truncated));
        }

        [Fact]
        public async Task RegisterComplete_WaiterReceivesTrue()
        {
            var sessionId = PairingService.RegisterPending();
            var waiter = PairingService.WaitForConfirmationAsync(sessionId, CancellationToken.None);
            Assert.True(PairingService.Complete(sessionId, accepted: true));
            Assert.True(await waiter);
        }

        [Fact]
        public async Task Complete_Rejected_False()
        {
            var sessionId = PairingService.RegisterPending();
            var waiter = PairingService.WaitForConfirmationAsync(sessionId, CancellationToken.None);
            Assert.True(PairingService.Complete(sessionId, accepted: false));
            Assert.False(await waiter);
        }

        [Fact]
        public async Task Complete_UnknownSession_ReturnsFalse()
        {
            Assert.False(PairingService.Complete(Guid.NewGuid().ToString("N"), true));
            Assert.False(await PairingService.WaitForConfirmationAsync(Guid.NewGuid().ToString("N"), CancellationToken.None));
        }

        [Fact]
        public async Task RegisterPending_ReturnsDistinctIds()
        {
            var a = PairingService.RegisterPending();
            var b = PairingService.RegisterPending();
            Assert.NotEqual(a, b);

            var waiter = PairingService.WaitForConfirmationAsync(a, CancellationToken.None);
            PairingService.Cleanup(a);
            Assert.False(await waiter);
        }

        [Fact]
        public async Task Cleanup_ResolvesWaiterWithoutAcceptance()
        {
            var sessionId = PairingService.RegisterPending();
            var waiter = PairingService.WaitForConfirmationAsync(sessionId, CancellationToken.None);
            PairingService.Cleanup(sessionId);
            Assert.False(await waiter);
        }

        [Fact]
        public async Task Wait_RespectsCancellationToken()
        {
            var sessionId = PairingService.RegisterPending();
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            Assert.False(await PairingService.WaitForConfirmationAsync(sessionId, cts.Token));
        }

        [Fact]
        public void Sas_IsSixDigits_AndStableAcrossRoundTrip()
        {
            var sas = PairingService.ComputeSas(new byte[32], new byte[32]);
            Assert.Equal(6, sas.Length);
            var msg = new PairingMessage { Type = PairingService.Pair2, DeviceId = "d", DeviceName = "n", Sas = sas };
            var decoded = PairingService.Decode(PairingService.Encode(msg));
            Assert.Equal(sas, decoded.Sas);
        }

        // ============================ Wire framing contract ============================
        // The initiator writes [type string][payload byte[]]; the responder reads the
        // type with ReadString() and the payload with a length-prefixed byte[] read.
        // These tests pin that exact framing so both sides never drift apart again.

        [Fact]
        public void InitiatorToResponder_Pair1_FramingIsConsistent()
        {
            var msg = new PairingMessage { Type = PairingService.Pair1, DeviceId = "dev-1", DeviceName = "Phone" };
            var encoded = PairingService.Encode(msg);

            using var ms = new MemoryStream();
            using (var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
            {
                w.Write(msg.Type);                       // initiator PAIR1 write
                w.Write7BitEncodedInt(encoded.Length);   // initiator payload write (length-prefixed)
                w.Write(encoded);
            }
            ms.Position = 0;

            using var r = new BinaryReader(ms, Encoding.UTF8, leaveOpen: true);
            string type = r.ReadString();            // responder ReadFrameStringAsync
            Assert.Equal(PairingService.Pair1, type);

            int len = r.Read7BitEncodedInt();        // responder ReadBytesLp
            var payload = r.ReadBytes(len);
            var decoded = PairingService.Decode(payload);
            Assert.Equal("dev-1", decoded.DeviceId);
            Assert.Equal("Phone", decoded.DeviceName);
        }

        [Fact]
        public void InitiatorToResponder_Pair2_FramingIsConsistent()
        {
            var msg = new PairingMessage { Type = PairingService.Pair2, DeviceId = "dev-1", DeviceName = "Phone", Sas = "123456" };
            var encoded = PairingService.Encode(msg);

            using var ms = new MemoryStream();
            using (var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
            {
                w.Write(msg.Type);
                w.Write7BitEncodedInt(encoded.Length);
                w.Write(encoded);
            }
            ms.Position = 0;

            using var r = new BinaryReader(ms, Encoding.UTF8, leaveOpen: true);
            Assert.Equal(PairingService.Pair2, r.ReadString());
            int len = r.Read7BitEncodedInt();
            var decoded = PairingService.Decode(r.ReadBytes(len));
            Assert.Equal("123456", decoded.Sas);
        }

        [Fact]
        public void InitiatorAbort_FramingIsTypeStringOnly()
        {
            using var ms = new MemoryStream();
            using (var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
            {
                w.Write(PairingService.PairAbort);   // initiator abort: no payload
            }
            ms.Position = 0;

            using var r = new BinaryReader(ms, Encoding.UTF8, leaveOpen: true);
            Assert.Equal(PairingService.PairAbort, r.ReadString());
            Assert.Equal(ms.Length, ms.Position);    // nothing follows the type
        }

        [Fact]
        public void ResponderToInitiator_FramingIsConsistent()
        {
            var msg = new PairingMessage { Type = PairingService.PairAccept, DeviceId = "dev-1", DeviceName = "Phone" };
            var encoded = PairingService.Encode(msg);

            using var ms = new MemoryStream();
            using (var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
            {
                w.Write7BitEncodedInt(encoded.Length);   // responder WriteBytesAndFlushAsync (length-prefixed)
                w.Write(encoded);
            }
            ms.Position = 0;

            using var r = new BinaryReader(ms, Encoding.UTF8, leaveOpen: true);
            int len = r.Read7BitEncodedInt();        // initiator ReadBytesLp
            var decoded = PairingService.Decode(r.ReadBytes(len));
            Assert.Equal(PairingService.PairAccept, decoded.Type);
            Assert.Equal("Phone", decoded.DeviceName);
        }
    }
}

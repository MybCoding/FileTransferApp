using System.IO;
using FileTransferApp.Services;
using Xunit;

namespace FileTransferApp.Tests
{
    public class IncomingTransferLimitTests
    {
        [Fact]
        public void Reserve_UpToLimit_Allowed()
        {
            IncomingTransferGuard.Configure(2);
            Assert.True(IncomingTransferGuard.TryReserveSlot());
            Assert.True(IncomingTransferGuard.TryReserveSlot());
            IncomingTransferGuard.ReleaseSlot();
            IncomingTransferGuard.ReleaseSlot();
        }

        [Fact]
        public void ThirdConcurrentTransfer_Rejected()
        {
            IncomingTransferGuard.Configure(2);
            try
            {
                Assert.True(IncomingTransferGuard.TryReserveSlot());
                Assert.True(IncomingTransferGuard.TryReserveSlot());
                Assert.False(IncomingTransferGuard.TryReserveSlot());
            }
            finally
            {
                IncomingTransferGuard.ReleaseSlot();
                IncomingTransferGuard.ReleaseSlot();
            }
        }

        [Fact]
        public void Slot_Released_CanBeReservedAgain()
        {
            IncomingTransferGuard.Configure(1);
            Assert.True(IncomingTransferGuard.TryReserveSlot());
            IncomingTransferGuard.ReleaseSlot();
            Assert.True(IncomingTransferGuard.TryReserveSlot());
            IncomingTransferGuard.ReleaseSlot();
        }

        [Fact]
        public void Configure_ClampsToAtLeastOne()
        {
            IncomingTransferGuard.Configure(0);
            Assert.True(IncomingTransferGuard.TryReserveSlot());
            IncomingTransferGuard.ReleaseSlot();
        }

        [Fact]
        public void CreateTempPath_InsideGivenDirectory()
        {
            string dir = Path.GetTempPath();
            string path = IncomingTransferGuard.CreateTempPath(dir, "doc.pdf");
            Assert.StartsWith(dir, path);
            Assert.EndsWith("_doc.pdf", path);
        }
    }
}

using System.IO;
using FileTransferApp.Services;
using Xunit;

namespace FileTransferApp.Tests
{
    public class DiskSpaceValidatorTests
    {
        [Fact]
        public void TryComputeRequiredSpace_Normal()
        {
            Assert.True(DiskSpaceValidator.TryComputeRequiredSpace(1000, 500, out long required));
            Assert.Equal(1500, required);
        }

        [Fact]
        public void TryComputeRequiredSpace_Overflow_ReturnsFalse()
        {
            Assert.False(DiskSpaceValidator.TryComputeRequiredSpace(long.MaxValue, 1, out _));
        }

        [Fact]
        public void TryComputeRequiredSpace_Negative_ReturnsFalse()
        {
            Assert.False(DiskSpaceValidator.TryComputeRequiredSpace(-1, 10, out _));
            Assert.False(DiskSpaceValidator.TryComputeRequiredSpace(10, -1, out _));
        }

        [Fact]
        public void HasSufficientSpace_OnTempDir_ZeroRequired_True()
        {
            Assert.True(DiskSpaceValidator.HasSufficientSpace(Path.GetTempPath(), 0, out _));
        }

        [Fact]
        public void HasSufficientSpace_HugeRequirement_False()
        {
            Assert.False(DiskSpaceValidator.HasSufficientSpace(Path.GetTempPath(), long.MaxValue, out _));
        }

        [Fact]
        public void GetAvailableFreeSpace_NullOrEmpty_ReturnsMinusOne()
        {
            Assert.Equal(-1, DiskSpaceValidator.GetAvailableFreeSpace(null));
            Assert.Equal(-1, DiskSpaceValidator.GetAvailableFreeSpace("   "));
        }

        [Fact]
        public void GetCacheFreeSpace_UsesProvider_WhenSet()
        {
            DiskSpaceValidator.CacheFreeSpaceProvider = () => 12345;
            try
            {
                Assert.Equal(12345, DiskSpaceValidator.GetCacheFreeSpace(Path.GetTempPath()));
            }
            finally
            {
                DiskSpaceValidator.CacheFreeSpaceProvider = null;
            }
        }

        [Fact]
        public void GetFinalStorageFreeSpace_UsesSharedStorageProvider()
        {
            DiskSpaceValidator.SharedStorageFreeSpaceProvider = () => 42;
            DiskSpaceValidator.FinalDownloadPathProvider = null;
            try
            {
                Assert.Equal(42, DiskSpaceValidator.GetFinalStorageFreeSpace());
            }
            finally
            {
                DiskSpaceValidator.SharedStorageFreeSpaceProvider = null;
            }
        }

        [Fact]
        public void GetFinalStorageFreeSpace_PrefersDownloadPathProvider()
        {
            DiskSpaceValidator.FinalDownloadPathProvider = () => Path.GetTempPath();
            DiskSpaceValidator.SharedStorageFreeSpaceProvider = () => -1;
            try
            {
                long value = DiskSpaceValidator.GetFinalStorageFreeSpace();
                Assert.True(value >= 0, $"Expected a non-negative free space, got {value}");
            }
            finally
            {
                DiskSpaceValidator.FinalDownloadPathProvider = null;
                DiskSpaceValidator.SharedStorageFreeSpaceProvider = null;
            }
        }
    }
}

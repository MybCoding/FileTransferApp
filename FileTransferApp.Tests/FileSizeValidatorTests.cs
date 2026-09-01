using FileTransferApp.Services;
using Xunit;

namespace FileTransferApp.Tests
{
    public class FileSizeValidatorTests
    {
        [Fact]
        public void Size_Zero_Rejected()
        {
            Assert.False(FileSizeValidator.ValidateFileSize(0, out string? reason));
            Assert.Equal("InvalidSize", reason);
        }

        [Fact]
        public void Size_Negative_Rejected()
        {
            Assert.False(FileSizeValidator.ValidateFileSize(-5, out string? reason));
            Assert.Equal("InvalidSize", reason);
        }

        [Fact]
        public void Size_OverMax_Rejected()
        {
            Assert.False(FileSizeValidator.ValidateFileSize(TransferLimits.MaxIncomingFileSize + 1, out string? reason));
            Assert.Equal("FileTooLarge", reason);
        }

        [Fact]
        public void Size_EqualToMax_Accepted()
        {
            Assert.True(FileSizeValidator.ValidateFileSize(TransferLimits.MaxIncomingFileSize, out _));
        }

        [Fact]
        public void Size_LongMaxValue_Rejected_NoOverflow()
        {
            Assert.False(FileSizeValidator.ValidateFileSize(long.MaxValue, out string? reason));
            Assert.Equal("FileTooLarge", reason);
        }

        [Fact]
        public void FileName_Empty_Rejected()
        {
            Assert.False(FileSizeValidator.ValidateFileName(string.Empty, out string? r1));
            Assert.False(FileSizeValidator.ValidateFileName(null, out string? r2));
            Assert.Equal("InvalidFileName", r1);
            Assert.Equal("InvalidFileName", r2);
        }

        [Fact]
        public void FileName_TooLong_Rejected()
        {
            string name = new string('a', TransferLimits.MaxFileNameLength + 1);
            Assert.False(FileSizeValidator.ValidateFileName(name, out string? reason));
            Assert.Equal("FileNameTooLong", reason);
        }

        [Fact]
        public void FileName_ExactlyMax_Accepted()
        {
            string name = new string('a', TransferLimits.MaxFileNameLength);
            Assert.True(FileSizeValidator.ValidateFileName(name, out _));
        }

        [Fact]
        public void FileName_Valid_Accepted()
        {
            Assert.True(FileSizeValidator.ValidateFileName("report.pdf", out _));
        }

        [Fact]
        public void LimitChunk_ExactFit_AllowsWrite()
        {
            Assert.True(FileSizeValidator.TryLimitWriteChunk(100, 900, 1000, out int toWrite));
            Assert.Equal(100, toWrite);
        }

        [Fact]
        public void LimitChunk_ExceedingExpected_FlagsOverflow_Clamps()
        {
            Assert.False(FileSizeValidator.TryLimitWriteChunk(500, 700, 1000, out int toWrite));
            Assert.Equal(300, toWrite);
        }

        [Fact]
        public void LimitChunk_AfterCompletion_FlagsOverflow_NoWrite()
        {
            Assert.False(FileSizeValidator.TryLimitWriteChunk(10, 1000, 1000, out int toWrite));
            Assert.Equal(0, toWrite);
        }

        [Fact]
        public void LimitChunk_ZeroBytes_Allowed()
        {
            Assert.True(FileSizeValidator.TryLimitWriteChunk(0, 0, 1000, out int toWrite));
            Assert.Equal(0, toWrite);
        }
    }
}

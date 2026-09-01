using System;
using System.Threading;
using System.Threading.Tasks;
using FileTransferApp.Services;
using Xunit;

namespace FileTransferApp.Tests
{
    public class TransferTimeoutTests
    {
        [Fact]
        public async Task CompletedTask_ReturnsTrue()
        {
            bool ok = await TransferTimeout.TryWaitAsync(Task.CompletedTask, TimeSpan.FromSeconds(1), CancellationToken.None);
            Assert.True(ok);
        }

        [Fact]
        public async Task TaskCompletingWithinTimeout_ReturnsTrue()
        {
            var task = Task.Delay(50);
            bool ok = await TransferTimeout.TryWaitAsync(task, TimeSpan.FromSeconds(2), CancellationToken.None);
            Assert.True(ok);
        }

        [Fact]
        public async Task NeverCompletingTask_TimesOut_ReturnsFalse()
        {
            var task = new TaskCompletionSource<bool>().Task;
            bool ok = await TransferTimeout.TryWaitAsync(task, TimeSpan.FromMilliseconds(50), CancellationToken.None);
            Assert.False(ok);
        }

        [Fact]
        public async Task ZeroTimeout_ReturnsFalseImmediately()
        {
            var task = new TaskCompletionSource<bool>().Task;
            bool ok = await TransferTimeout.TryWaitAsync(task, TimeSpan.Zero, CancellationToken.None);
            Assert.False(ok);
        }

        [Fact]
        public async Task CancelledToken_ReturnsFalse()
        {
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            var task = new TaskCompletionSource<bool>().Task;
            bool ok = await TransferTimeout.TryWaitAsync(task, TimeSpan.FromSeconds(5), cts.Token);
            Assert.False(ok);
        }

        [Fact]
        public async Task FaultedTask_RethrowsFault()
        {
            var task = Task.FromException(new InvalidOperationException("boom"));
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => TransferTimeout.TryWaitAsync(task, TimeSpan.FromSeconds(1), CancellationToken.None));
        }

        [Fact]
        public async Task NullTask_ReturnsFalse()
        {
            bool ok = await TransferTimeout.TryWaitAsync(null!, TimeSpan.FromSeconds(1), CancellationToken.None);
            Assert.False(ok);
        }

        [Fact]
        public void TransferLimits_DefaultTimeouts()
        {
            Assert.Equal(TimeSpan.FromSeconds(15), TransferLimits.IncomingFileStreamTimeout);
            Assert.Equal(TimeSpan.FromSeconds(30), TransferLimits.IncomingIdleTimeout);
            Assert.Equal(TimeSpan.FromSeconds(8), TransferLimits.OfferResponseTimeout);
            Assert.Equal(TimeSpan.FromMinutes(10), TransferLimits.FinalAckTimeout);
        }

        [Fact]
        public async Task AbandonedTaskFault_IsObserved_NotUnhandled()
        {
            // Simulates the real stall: the read task is abandoned after the timeout
            // fires, then faults when the socket is closed. Must not crash the process.
            var abandoned = new TaskCompletionSource<bool>();
            var check = TransferTimeout.TryWaitAsync(abandoned.Task, TimeSpan.FromMilliseconds(20), CancellationToken.None);

            bool ok = await check;
            Assert.False(ok);

            abandoned.SetException(new System.IO.IOException("socket closed"));
            await Task.Delay(100);
        }
    }
}

using System;
using System.Threading;
using System.Threading.Tasks;

namespace FileTransferApp.Services
{
    // Pure, testable timeout helper. Never leaves the underlying task unobserved:
    // on a timeout/cancellation the abandoned task's eventual fault is observed.
    public static class TransferTimeout
    {
        // Returns true when `task` completes within `timeout`.
        // Throws the task's own fault when it fails before the timeout elapses
        // (same as awaiting the task directly).
        public static async Task<bool> TryWaitAsync(Task task, TimeSpan timeout, CancellationToken ct)
        {
            if (task == null) return false;

            try
            {
                await task.WaitAsync(timeout, ct);
                return true;
            }
            catch (TimeoutException)
            {
                ObserveAbandoned(task);
                return false;
            }
            catch (OperationCanceledException)
            {
                ObserveAbandoned(task);
                return false;
            }
        }

        private static void ObserveAbandoned(Task task)
        {
            _ = task.ContinueWith(
                t => _ = t.Exception,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
        }
    }
}

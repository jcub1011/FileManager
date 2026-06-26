using FileManager.Core.Execution;
using Xunit;

namespace FileManager.Core.Tests;

/// <summary>
/// Verifies the §5.4 same-file path lock: FIFO serialization on a shared path, concurrency on distinct
/// paths, and deadlock-freedom with crossing key-sets.
/// </summary>
public sealed class PathLockManagerTests
{
    private static string P(string name) => System.IO.Path.Combine(System.IO.Path.GetTempPath(), name);

    [Fact]
    public async Task SamePath_SecondContenderWaits_NoOverlap()
    {
        var mgr = new PathLockManager();
        string path = P("shared.txt");

        int concurrent = 0;
        int peak = 0;
        var sync = new object();

        void Body()
        {
            using IDisposable handle = mgr.Acquire(path);
            lock (sync)
            {
                concurrent++;
                peak = Math.Max(peak, concurrent);
            }

            Thread.Sleep(20); // hold the lock so an overlap would be observed if locking were broken

            lock (sync)
                concurrent--;
        }

        await Task.WhenAll(Task.Run(Body), Task.Run(Body));

        // Two Jobs touching the same absolute path serialize ⇒ peak concurrency is exactly 1.
        Assert.Equal(1, peak);
    }

    [Fact]
    public async Task DistinctPaths_RunConcurrently()
    {
        var mgr = new PathLockManager();
        var barrier = new Barrier(2);
        bool bothInside = false;

        void Body(string path)
        {
            using IDisposable handle = mgr.Acquire(path);
            // If distinct-path locks did NOT run concurrently this barrier would deadlock (the second
            // task could never arrive while the first holds a lock). SignalAndWait proves overlap.
            if (barrier.SignalAndWait(TimeSpan.FromSeconds(5)))
                bothInside = true;
        }

        await Task.WhenAll(Task.Run(() => Body(P("a.txt"))), Task.Run(() => Body(P("b.txt"))));

        Assert.True(bothInside);
    }

    [Fact]
    public async Task CrossingKeySets_DoNotDeadlock()
    {
        var mgr = new PathLockManager();
        string x = P("x.txt");
        string y = P("y.txt");

        // Job A locks [x, y]; Job B locks [y, x]. A naive per-key acquire in argument order would
        // deadlock; the manager sorts keys so both acquire in the same canonical order.
        var a = Task.Run(() =>
        {
            for (int i = 0; i < 200; i++)
            {
                using IDisposable h = mgr.Acquire(x, y);
                Thread.Yield();
            }
        });
        var b = Task.Run(() =>
        {
            for (int i = 0; i < 200; i++)
            {
                using IDisposable h = mgr.Acquire(y, x);
                Thread.Yield();
            }
        });

        Task work = Task.WhenAll(a, b);
        Task done = await Task.WhenAny(work, Task.Delay(TimeSpan.FromSeconds(10)));
        Assert.True(done == work, "crossing key-sets deadlocked");
        await work; // surface any exception
    }

    [Fact]
    public async Task AcquireAsync_SamePath_GrantsInStrictArrivalOrder()
    {
        var mgr = new PathLockManager();
        string path = P("fifo.txt");
        var order = new List<int>();
        var orderGate = new object();

        // Hold the lock so every contender must queue behind it.
        IDisposable held = mgr.Acquire(path);

        // Establish a KNOWN arrival order: AcquireAsync enqueues its waiter synchronously (before its
        // first await), so calling it in this loop on one thread queues ids 0,1,2,3,4 in exactly that
        // order — no timing/staggering needed. Each continuation records the id when its lock is granted
        // and then releases, handing off to the next FIFO waiter.
        const int n = 5;
        var waiterTasks = new Task[n];
        for (int i = 0; i < n; i++)
        {
            int id = i;
            Task<IDisposable> acquire = mgr.AcquireAsync(new[] { path });
            waiterTasks[i] = acquire.ContinueWith(t =>
            {
                lock (orderGate)
                    order.Add(id);
                t.Result.Dispose();
            }, TaskContinuationOptions.ExecuteSynchronously);
        }

        // Release the initial holder; the queue must drain head-first in arrival order.
        held.Dispose();
        await Task.WhenAll(waiterTasks);

        // STRICT FIFO: granted in the exact order they arrived.
        Assert.Equal(new[] { 0, 1, 2, 3, 4 }, order);
    }

    [Fact]
    public async Task AcquireAsync_Cancelled_DoesNotLeakLock()
    {
        var mgr = new PathLockManager();
        string path = P("cancel.txt");

        using IDisposable held = mgr.Acquire(path);

        using var cts = new CancellationTokenSource();
        Task waiter = mgr.AcquireAsync(new[] { path }, cts.Token);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiter);

        // Release the original holder; a fresh acquire must succeed immediately (the cancelled waiter
        // was withdrawn from the FIFO queue and returned its ref-count, so it never wins the lock).
        held.Dispose();
        using IDisposable again = mgr.Acquire(path);
        Assert.NotNull(again);
    }
}

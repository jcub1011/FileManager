using FileManager.Core.Execution;
using FileManager.Core.Jobs;
using FileManager.Core.Profiles;
using Xunit;

namespace FileManager.Core.Tests;

/// <summary>
/// Verifies the §5.4 bounded worker pool: it saturates up to <c>MaxWorkers</c> Jobs, never exceeds it,
/// applies backpressure, and drains gracefully.
/// </summary>
public sealed class WorkerPoolTests
{
    private static readonly IngestionContext Ctx =
        new() { Now = new DateTimeOffset(2026, 6, 25, 12, 0, 0, TimeSpan.Zero) };

    private static JobRequest Request(string source)
    {
        Profile p = TestProfiles.Build(new[] { @"C:\src" }, new[] { @"C:\dst" });
        return new JobRequest { Profile = p, SourcePath = source, Context = Ctx };
    }

    [Fact]
    public async Task NeverExceedsMaxWorkers_AndSaturates()
    {
        const int maxWorkers = 4;
        const int jobCount = 40;
        var queue = new JobQueue(capacity: 1024);

        int concurrent = 0;
        int peak = 0;
        var sync = new object();

        JobResult Handler(JobRequest req, CancellationToken ct)
        {
            lock (sync)
            {
                concurrent++;
                peak = Math.Max(peak, concurrent);
            }

            Thread.Sleep(15); // overlap window so the peak can actually reach MaxWorkers

            lock (sync)
                concurrent--;

            return new JobResult { JobId = "j", State = JobState.Closed, SourcePath = req.SourcePath };
        }

        await using var pool = new WorkerPool(queue, maxWorkers, Handler);

        for (int i = 0; i < jobCount; i++)
            await queue.EnqueueAsync(Request($"f{i}"));

        await pool.DrainAsync();

        Assert.True(peak <= maxWorkers, $"peak {peak} exceeded MaxWorkers {maxWorkers}");
        Assert.Equal(maxWorkers, peak); // saturated: reached the bound
        Assert.Equal(0, concurrent);
    }

    [Fact]
    public async Task DrainAsync_CompletesAllInFlightJobs()
    {
        var queue = new JobQueue();
        int processed = 0;

        JobResult Handler(JobRequest req, CancellationToken ct)
        {
            Interlocked.Increment(ref processed);
            return new JobResult { JobId = "j", State = JobState.Closed, SourcePath = req.SourcePath };
        }

        await using var pool = new WorkerPool(queue, maxWorkers: 3, Handler);

        const int count = 25;
        for (int i = 0; i < count; i++)
            await queue.EnqueueAsync(Request($"f{i}"));

        await pool.DrainAsync();

        Assert.Equal(count, processed);
    }

    [Fact]
    public async Task BoundedQueue_AppliesBackpressure()
    {
        var queue = new JobQueue(capacity: 2);

        // With no consumers, a third TryEnqueue past the bound fails (backpressure rather than growth).
        Assert.True(queue.TryEnqueue(Request("a")));
        Assert.True(queue.TryEnqueue(Request("b")));
        Assert.False(queue.TryEnqueue(Request("c")));

        await Task.CompletedTask;
    }

    [Fact]
    public async Task HandlerException_DoesNotTearDownPool()
    {
        var queue = new JobQueue();
        int processed = 0;
        int errors = 0;

        JobResult Handler(JobRequest req, CancellationToken ct)
        {
            if (req.SourcePath.EndsWith("bad"))
                throw new InvalidOperationException("boom");
            Interlocked.Increment(ref processed);
            return new JobResult { JobId = "j", State = JobState.Closed, SourcePath = req.SourcePath };
        }

        await using var pool = new WorkerPool(
            queue, maxWorkers: 2, Handler, onError: (_, _) => Interlocked.Increment(ref errors));

        await queue.EnqueueAsync(Request("ok1"));
        await queue.EnqueueAsync(Request("bad"));
        await queue.EnqueueAsync(Request("ok2"));

        await pool.DrainAsync();

        Assert.Equal(2, processed);
        Assert.Equal(1, errors);
    }
}

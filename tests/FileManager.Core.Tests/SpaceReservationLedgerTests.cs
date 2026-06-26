using System.Collections.Concurrent;
using FileManager.Core.IO;
using FileManager.Core.Safety;
using Xunit;

namespace FileManager.Core.Tests;

/// <summary>
/// Unit tests for the concurrency-aware <see cref="SpaceReservationLedger"/>: outstanding reservations
/// make a second over-committing request fail, releasing frees the bytes again, the margin is honored,
/// multi-volume reservations are all-or-nothing, and concurrent reserve/release never over-commits.
/// </summary>
public sealed class SpaceReservationLedgerTests
{
    // Distinct synthetic volume roots; the fake probe matches paths by longest prefix.
    private const string VolA = @"C:\volA";
    private const string VolB = @"C:\volB";

    private static FakeFreeSpaceProbe Probe(params (string vol, long free)[] vols)
    {
        var map = new Dictionary<string, long>();
        foreach ((string vol, long free) in vols)
            map[vol] = free;
        return new FakeFreeSpaceProbe(map);
    }

    [Fact]
    public void TwoReservationsExceedingCapacity_FirstSucceeds_SecondFailsCitingReserved()
    {
        // Capacity 100 on one volume; each Job wants 70 → only one fits at a time (the concurrency case).
        var ledger = new SpaceReservationLedger(Probe((VolA, 100)));

        ReservationResult first = ledger.TryReserve(new[] { new SpaceRequest(VolA + @"\f1", 70) });
        Assert.True(first.Ok);

        ReservationResult second = ledger.TryReserve(new[] { new SpaceRequest(VolA + @"\f2", 70) });
        Assert.False(second.Ok);
        Assert.NotNull(second.Reason);
        // The reason cites the already-reserved figure — what makes a concurrent second Job fail.
        Assert.Contains("reserved 70", second.Reason);
    }

    [Fact]
    public void ReleasingFirstReservation_ThenReReserving_Succeeds()
    {
        var ledger = new SpaceReservationLedger(Probe((VolA, 100)));

        ReservationResult first = ledger.TryReserve(new[] { new SpaceRequest(VolA + @"\f1", 70) });
        Assert.True(first.Ok);
        Assert.False(ledger.TryReserve(new[] { new SpaceRequest(VolA + @"\f2", 70) }).Ok);

        // Release the first; the bytes return to the pool.
        first.Handle!.Dispose();

        ReservationResult third = ledger.TryReserve(new[] { new SpaceRequest(VolA + @"\f3", 70) });
        Assert.True(third.Ok);
    }

    [Fact]
    public void Margin_IsHonored()
    {
        // 100 free, 30 margin ⇒ usable 70. A 71-byte request must fail; a 70-byte one fits exactly.
        var ledger = new SpaceReservationLedger(Probe((VolA, 100)), marginBytes: 30);

        Assert.False(ledger.TryReserve(new[] { new SpaceRequest(VolA + @"\big", 71) }).Ok);
        Assert.True(ledger.TryReserve(new[] { new SpaceRequest(VolA + @"\fits", 70) }).Ok);
    }

    [Fact]
    public void MultiVolume_AllOrNothing_WholeReservationFails_NothingReserved()
    {
        // VolA fits (need 50 of 100); VolB does not (need 200 of 100) ⇒ the whole reservation fails and
        // VolA's bytes must NOT have been reserved.
        var ledger = new SpaceReservationLedger(Probe((VolA, 100), (VolB, 100)));

        ReservationResult result = ledger.TryReserve(new[]
        {
            new SpaceRequest(VolA + @"\a", 50),
            new SpaceRequest(VolB + @"\b", 200),
        });

        Assert.False(result.Ok);
        Assert.Contains("volB", result.Reason);

        // Proof nothing was reserved on VolA: a fresh full-capacity request on VolA now succeeds.
        Assert.True(ledger.TryReserve(new[] { new SpaceRequest(VolA + @"\c", 100) }).Ok);
    }

    [Fact]
    public void ZeroOrNegativeRequests_AreIgnored_AndGrantedTrivially()
    {
        var ledger = new SpaceReservationLedger(Probe((VolA, 10)));

        ReservationResult result = ledger.TryReserve(new[]
        {
            new SpaceRequest(VolA + @"\zero", 0),
            new SpaceRequest(VolA + @"\neg", -5),
        });

        Assert.True(result.Ok);
        // A subsequent full-capacity request still fits — nothing was consumed.
        Assert.True(ledger.TryReserve(new[] { new SpaceRequest(VolA + @"\full", 10) }).Ok);
    }

    [Fact]
    public void UnconstrainedVolume_NeverFails()
    {
        // An unresolved volume reports long.MaxValue; even an enormous request is granted.
        var ledger = new SpaceReservationLedger(FakeFreeSpaceProbe.Unconstrained());

        ReservationResult result = ledger.TryReserve(new[] { new SpaceRequest(VolA + @"\huge", long.MaxValue / 2) });
        Assert.True(result.Ok);
    }

    [Fact]
    public void ParallelReserve_NeverOverCommits_GrantsExactlyCapacity()
    {
        // Capacity 1000, 100 per hold ⇒ exactly 10 holds fit. Many threads race to reserve and HOLD
        // (no release during the run), so the granted count is timing-independent: a correct ledger
        // grants exactly 10 and refuses the rest. This proves no over-commit (never an 11th) without
        // depending on observing transient concurrency — a racy counter could only undercount.
        const long capacity = 1000;
        const int unit = 100;
        var ledger = new SpaceReservationLedger(Probe((VolA, capacity)));

        var granted = new ConcurrentBag<SpaceReservation>();
        Parallel.For(0, 200, new ParallelOptions { MaxDegreeOfParallelism = 16 }, i =>
        {
            ReservationResult r = ledger.TryReserve(new[] { new SpaceRequest(VolA + @"\p" + i, unit) });
            if (r.Ok)
                granted.Add(r.Handle!);
        });

        // Never more than capacity/unit grants (no over-commit), and exactly that many since 200
        // threads each wanted a unit and none released mid-run.
        Assert.Equal((int)(capacity / unit), granted.Count);
        // With all 10 still held, a further request must be refused.
        Assert.False(ledger.TryReserve(new[] { new SpaceRequest(VolA + @"\more", unit) }).Ok);

        // After releasing every hold, the full capacity is reservable again in one shot.
        foreach (SpaceReservation handle in granted)
            handle.Dispose();
        Assert.True(ledger.TryReserve(new[] { new SpaceRequest(VolA + @"\final", capacity) }).Ok);
    }
}

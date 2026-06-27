using System.IO;
using FileManager.Contracts.Messages;
using Xunit;

namespace FileManager.Service.Tests;

/// <summary>
/// Covers the length-prefixed framing helper and the source-generated Contracts (de)serialization:
/// every DTO round-trips, a clean EOF returns null, and an oversized length prefix is rejected.
/// </summary>
public sealed class FramingAndContractsTests
{
    private static readonly DateTimeOffset T0 = new(2026, 6, 26, 12, 0, 0, TimeSpan.Zero);

    public static IEnumerable<object[]> AllMessages()
    {
        yield return new object[] { IpcMessage.ForSubmit(new SubmitPayload(@"C:\src\a.txt", "p1", true)) };
        yield return new object[] { IpcMessage.ForSubmitResult(SubmitPayloadResult.Ok(new[] { "p1:a", "p1:b" })) };
        yield return new object[] { IpcMessage.ForSubmitResult(SubmitPayloadResult.Rejected("no profile")) };
        yield return new object[] { IpcMessage.ForStateQuery() };
        yield return new object[] { IpcMessage.ForState(new EngineState(3, 1, 4, 2, 10, 5, 1)) };
        yield return new object[] { IpcMessage.ForSubscribe() };
        yield return new object[] { IpcMessage.ForEvent(new JobEvent("p:a", "p", "Closed", "COMPLETED", "done", T0)) };
        yield return new object[] { IpcMessage.ForListProfiles() };
        yield return new object[]
        {
            IpcMessage.ForProfileList(new ProfileList(new[]
            {
                new ProfileSummary("p1", "First", true),
                new ProfileSummary("p2", "Second", false),
            })),
        };
        yield return new object[] { IpcMessage.ForReloadProfiles() };
        yield return new object[] { IpcMessage.ForReloadResult(new ReloadResult(2, new[] { "bad.json: oops" })) };
        yield return new object[] { IpcMessage.ForDryRun(new DryRunRequest(@"C:\src", null, false)) };
        yield return new object[] { IpcMessage.ForDryRunReport(DryRunReport.NotImplemented()) };
    }

    [Theory]
    [MemberData(nameof(AllMessages))]
    public void EveryDto_RoundTripsViaSourceGenContext(IpcMessage message)
    {
        byte[] bytes = ContractsSerializer.SerializeToUtf8Bytes(message);

        Assert.True(ContractsSerializer.TryDeserialize(bytes, out IpcMessage? back, out string? error), error);
        Assert.NotNull(back);
        Assert.Equal(message.Kind, back!.Kind);
        // Record equality on collection members is reference equality (a deserialized List != the source
        // array), so we compare the re-serialized JSON instead: identical JSON proves every nested
        // payload field round-tripped faithfully.
        Assert.Equal(
            ContractsSerializer.Serialize(message),
            ContractsSerializer.Serialize(back));
    }

    [Fact]
    public async Task Framing_WriteThenRead_RoundTripsPayload()
    {
        byte[] payload = ContractsSerializer.SerializeToUtf8Bytes(
            IpcMessage.ForSubmit(new SubmitPayload("/src/a", null, false)));

        using var stream = new MemoryStream();
        await Framing.WriteMessageAsync(stream, payload);
        stream.Position = 0;

        byte[]? read = await Framing.ReadMessageAsync(stream);
        Assert.NotNull(read);
        Assert.Equal(payload, read);
    }

    [Fact]
    public async Task Framing_MultipleFrames_ReadInOrder()
    {
        using var stream = new MemoryStream();
        await Framing.WriteMessageAsync(stream, new byte[] { 1, 2, 3 });
        await Framing.WriteMessageAsync(stream, new byte[] { 4, 5 });
        stream.Position = 0;

        Assert.Equal(new byte[] { 1, 2, 3 }, await Framing.ReadMessageAsync(stream));
        Assert.Equal(new byte[] { 4, 5 }, await Framing.ReadMessageAsync(stream));
        Assert.Null(await Framing.ReadMessageAsync(stream)); // clean EOF after the last frame.
    }

    [Fact]
    public async Task Framing_CleanEof_ReturnsNull()
    {
        using var stream = new MemoryStream();
        Assert.Null(await Framing.ReadMessageAsync(stream));
    }

    [Fact]
    public async Task Framing_OversizedLengthPrefix_Rejected()
    {
        // A prefix claiming far more than MaxFrameBytes must be rejected, not allocated.
        using var stream = new MemoryStream();
        Span<byte> prefix = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(prefix, Framing.MaxFrameBytes + 1);
        stream.Write(prefix);
        stream.Position = 0;

        await Assert.ThrowsAsync<InvalidDataException>(async () => await Framing.ReadMessageAsync(stream));
    }

    [Fact]
    public async Task Framing_NegativeLengthPrefix_Rejected()
    {
        using var stream = new MemoryStream();
        Span<byte> prefix = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(prefix, -1);
        stream.Write(prefix);
        stream.Position = 0;

        await Assert.ThrowsAsync<InvalidDataException>(async () => await Framing.ReadMessageAsync(stream));
    }

    [Fact]
    public async Task Framing_TornPayload_Rejected()
    {
        // Prefix says 10 bytes but only 3 follow ⇒ torn frame.
        using var stream = new MemoryStream();
        Span<byte> prefix = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(prefix, 10);
        stream.Write(prefix);
        stream.Write(new byte[] { 1, 2, 3 });
        stream.Position = 0;

        await Assert.ThrowsAsync<InvalidDataException>(async () => await Framing.ReadMessageAsync(stream));
    }
}

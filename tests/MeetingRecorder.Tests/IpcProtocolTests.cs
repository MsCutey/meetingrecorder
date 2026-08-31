using MeetingRecorder.Core.Models;
using MeetingRecorder.Core.Protocol;
using System.Buffers.Binary;

namespace MeetingRecorder.Tests;

public sealed class IpcProtocolTests
{
    [Fact]
    public void CaptureProtectionBuild_UsesProtocolVersionTwo() => Assert.Equal(2, IpcProtocol.Version);

    [Fact]
    public async Task ControlMessage_IsLengthFramedAndRoundTrips()
    {
        await using var stream = new MemoryStream();
        var expected = IpcControlMessage.Create(IpcMessageTypes.State, new WorkerStateUpdate(RecordingState.Paused), "request-1");
        await PipeFrameCodec.WriteControlAsync(stream, expected);
        stream.Position = 0;

        var frame = Assert.IsType<IpcControlFrame>(await PipeFrameCodec.ReadAsync(stream));
        Assert.Equal("request-1", frame.Message.RequestId);
        Assert.Equal(RecordingState.Paused, frame.Message.ReadPayload<WorkerStateUpdate>().State);
    }

    [Fact]
    public async Task PreviewFrame_UsesBinaryPayloadAndRoundTrips()
    {
        await using var stream = new MemoryStream();
        var expected = new PreviewFrame(2, 1, 8, [1, 2, 3, 4, 5, 6, 7, 8]);
        await PipeFrameCodec.WritePreviewAsync(stream, expected);
        stream.Position = 0;

        var frame = Assert.IsType<IpcPreviewFrame>(await PipeFrameCodec.ReadAsync(stream));
        Assert.Equal(expected.Width, frame.Preview.Width);
        Assert.Equal(expected.Height, frame.Preview.Height);
        Assert.Equal(expected.Stride, frame.Preview.Stride);
        Assert.Equal(expected.Bgra32, frame.Preview.Bgra32);
    }

    [Fact]
    public void StartMessage_RoundTripsPreviewDecision()
    {
        var request = new WorkerRecordingRequest
        {
            SessionId = "session",
            Mode = RecordingMode.VideoAndAudio,
            PartialPath = "recording.partial.mp4",
            EngineLogPath = "engine.log",
            EnablePreview = true
        };
        var message = IpcControlMessage.Create(IpcMessageTypes.Start, request);

        Assert.True(message.ReadPayload<WorkerRecordingRequest>().EnablePreview);
    }

    [Fact]
    public async Task IncompatibleProtocolVersion_IsRejectedBeforeWriting()
    {
        await using var stream = new MemoryStream();
        var message = IpcControlMessage.Empty(IpcMessageTypes.Stop) with { ProtocolVersion = IpcProtocol.Version + 1 };
        await Assert.ThrowsAsync<InvalidDataException>(async () => await PipeFrameCodec.WriteControlAsync(stream, message));
        Assert.Equal(0, stream.Length);
    }

    [Fact]
    public async Task PreviewWithMismatchedPixelLength_IsRejected()
    {
        await using var stream = new MemoryStream();
        var header = new byte[5];
        header[0] = 2;
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(1), 16);
        await stream.WriteAsync(header);
        var payload = new byte[16];
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(0), 2);
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(4), 2);
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(8), 8);
        await stream.WriteAsync(payload);
        stream.Position = 0;

        await Assert.ThrowsAsync<InvalidDataException>(async () => await PipeFrameCodec.ReadAsync(stream));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MicrophoneToggle_RoundTripsEnabledFlag(bool enabled)
    {
        await using var stream = new MemoryStream();
        var expected = IpcControlMessage.Create(IpcMessageTypes.Microphone, new WorkerMicrophoneUpdate(enabled), "mic-1");
        await PipeFrameCodec.WriteControlAsync(stream, expected);
        stream.Position = 0;

        var frame = Assert.IsType<IpcControlFrame>(await PipeFrameCodec.ReadAsync(stream));
        Assert.Equal(IpcMessageTypes.Microphone, frame.Message.Type);
        Assert.Equal("mic-1", frame.Message.RequestId);
        Assert.Equal(enabled, frame.Message.ReadPayload<WorkerMicrophoneUpdate>().Enabled);
    }

    [Fact]
    public void StartMessage_CarriesMicrophoneMuteIntent()
    {
        var request = new WorkerRecordingRequest
        {
            SessionId = "session",
            Mode = RecordingMode.VideoAndAudio,
            PartialPath = "recording.partial.mp4",
            EngineLogPath = "engine.log",
            StartMicrophoneMuted = true
        };
        var message = IpcControlMessage.Create(IpcMessageTypes.Start, request);

        Assert.True(message.ReadPayload<WorkerRecordingRequest>().StartMicrophoneMuted);
    }
}

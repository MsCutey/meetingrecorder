using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;
using MeetingRecorder.Core.Models;

namespace MeetingRecorder.Core.Protocol;

public static class IpcProtocol
{
    public const int Version = 2;
    public const int MaximumControlBytes = 1024 * 1024;
    public const int MaximumPreviewBytes = 64 * 1024 * 1024;
}

public static class IpcMessageTypes
{
    public const string Hello = "hello";
    public const string HelloAccepted = "hello-accepted";
    public const string Discover = "discover";
    public const string DiscoveryResult = "discovery-result";
    public const string Start = "start";
    public const string Started = "started";
    public const string Pause = "pause";
    public const string Resume = "resume";
    public const string Stop = "stop";
    public const string State = "state";
    public const string Progress = "progress";
    public const string Completed = "completed";
    public const string Error = "error";
}

public static class IpcJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };
}

public sealed record IpcControlMessage
{
    public int ProtocolVersion { get; init; } = IpcProtocol.Version;
    public required string Type { get; init; }
    public string? RequestId { get; init; }
    public JsonElement Payload { get; init; }

    public static IpcControlMessage Create<T>(string type, T payload, string? requestId = null) => new()
    {
        Type = type,
        RequestId = requestId,
        Payload = JsonSerializer.SerializeToElement(payload, IpcJson.Options)
    };

    public static IpcControlMessage Empty(string type, string? requestId = null) =>
        Create(type, new { }, requestId);

    public T ReadPayload<T>() =>
        Payload.Deserialize<T>(IpcJson.Options)
        ?? throw new InvalidDataException($"IPC 消息 {Type} 缺少有效负载。");

    public void ValidateVersion()
    {
        if (ProtocolVersion != IpcProtocol.Version)
        {
            throw new InvalidDataException($"IPC 协议版本不兼容：主程序为 {IpcProtocol.Version}，收到 {ProtocolVersion}。");
        }
    }
}

public abstract record IpcFrame;
public sealed record IpcControlFrame(IpcControlMessage Message) : IpcFrame;
public sealed record IpcPreviewFrame(PreviewFrame Preview) : IpcFrame;

public static class PipeFrameCodec
{
    private const byte ControlKind = 1;
    private const byte PreviewKind = 2;
    private const int HeaderSize = 5;
    private const int PreviewMetadataSize = 12;

    public static async ValueTask WriteControlAsync(Stream stream, IpcControlMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(message);
        message.ValidateVersion();
        var payload = JsonSerializer.SerializeToUtf8Bytes(message, IpcJson.Options);
        if (payload.Length > IpcProtocol.MaximumControlBytes) throw new InvalidDataException("IPC 控制消息过大。");
        await WriteFrameAsync(stream, ControlKind, payload, cancellationToken).ConfigureAwait(false);
    }

    public static async ValueTask WritePreviewAsync(Stream stream, PreviewFrame preview, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(preview);
        if (preview.Bgra32.Length > IpcProtocol.MaximumPreviewBytes - PreviewMetadataSize)
        {
            throw new InvalidDataException("IPC 预览帧过大。");
        }

        var payload = new byte[PreviewMetadataSize + preview.Bgra32.Length];
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(0, 4), preview.Width);
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(4, 4), preview.Height);
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(8, 4), preview.Stride);
        preview.Bgra32.CopyTo(payload, PreviewMetadataSize);
        await WriteFrameAsync(stream, PreviewKind, payload, cancellationToken).ConfigureAwait(false);
    }

    public static async ValueTask<IpcFrame?> ReadAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var header = new byte[HeaderSize];
        var first = await stream.ReadAsync(header.AsMemory(0, 1), cancellationToken).ConfigureAwait(false);
        if (first == 0) return null;
        await stream.ReadExactlyAsync(header.AsMemory(1, HeaderSize - 1), cancellationToken).ConfigureAwait(false);

        var kind = header[0];
        var length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(1, 4));
        if (kind is not (ControlKind or PreviewKind))
        {
            throw new InvalidDataException($"未知的 IPC 帧类型：{kind}。");
        }
        var maximum = kind == ControlKind ? IpcProtocol.MaximumControlBytes : IpcProtocol.MaximumPreviewBytes;
        if (length <= 0 || length > maximum) throw new InvalidDataException($"无效的 IPC 帧长度：{length}。");
        var payload = new byte[length];
        await stream.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);

        if (kind == ControlKind)
        {
            var message = JsonSerializer.Deserialize<IpcControlMessage>(payload, IpcJson.Options)
                          ?? throw new InvalidDataException("IPC 控制消息为空。");
            message.ValidateVersion();
            return new IpcControlFrame(message);
        }
        if (length <= PreviewMetadataSize)
        {
            throw new InvalidDataException("预览帧负载已损坏。");
        }

        var width = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(0, 4));
        var height = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(4, 4));
        var stride = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(8, 4));
        if (width <= 0 || height <= 0 || stride == 0) throw new InvalidDataException("预览帧尺寸无效。");
        var expectedBytes = checked(Math.Abs((long)stride) * height);
        if (expectedBytes != length - PreviewMetadataSize)
        {
            throw new InvalidDataException("预览帧像素长度与尺寸不匹配。");
        }
        return new IpcPreviewFrame(new PreviewFrame(width, height, stride, payload[PreviewMetadataSize..]));
    }

    private static async ValueTask WriteFrameAsync(Stream stream, byte kind, byte[] payload, CancellationToken cancellationToken)
    {
        var header = new byte[HeaderSize];
        header[0] = kind;
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(1, 4), payload.Length);
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
}

public sealed class PipeConnection(Stream stream) : IAsyncDisposable
{
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    public async ValueTask SendAsync(IpcControlMessage message, CancellationToken cancellationToken = default)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await PipeFrameCodec.WriteControlAsync(stream, message, cancellationToken).ConfigureAwait(false); }
        finally { _writeGate.Release(); }
    }

    public async ValueTask SendPreviewAsync(PreviewFrame preview, CancellationToken cancellationToken = default)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await PipeFrameCodec.WritePreviewAsync(stream, preview, cancellationToken).ConfigureAwait(false); }
        finally { _writeGate.Release(); }
    }

    public ValueTask<IpcFrame?> ReceiveAsync(CancellationToken cancellationToken = default) =>
        PipeFrameCodec.ReadAsync(stream, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        _writeGate.Dispose();
        await stream.DisposeAsync().ConfigureAwait(false);
    }
}

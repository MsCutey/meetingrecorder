using NAudio.CoreAudioApi;
using NAudio.Wave;
using MeetingRecorder.Engine.Abstractions;

namespace MeetingRecorder.Engine.Audio;

internal sealed class AudioCaptureSource : IDisposable
{
    private static readonly WaveFormat TargetFormat = new(48_000, 16, 2);
    private readonly object _readGate = new();
    private readonly MMDeviceEnumerator _enumerator = new();
    private readonly MMDevice _device;
    private readonly IWaveIn _capture;
    private readonly BufferedWaveProvider _buffer;
    private MediaFoundationResampler _resampler;
    private volatile bool _accepting;
    private volatile bool _stopping;
    private int _faultRaised;

    public AudioCaptureSource(string? endpointId, DataFlow flow)
    {
        _device = string.IsNullOrWhiteSpace(endpointId)
            ? _enumerator.GetDefaultAudioEndpoint(flow, Role.Multimedia)
            : _enumerator.GetDevice(endpointId);
        _capture = flow == DataFlow.Render
            ? new WasapiLoopbackCapture(_device)
            : CreateMicrophoneCapture(_device);
        _buffer = new BufferedWaveProvider(_capture.WaveFormat)
        {
            BufferDuration = TimeSpan.FromSeconds(2),
            DiscardOnBufferOverflow = false,
            ReadFully = true
        };
        _resampler = CreateResampler();
        _capture.DataAvailable += OnDataAvailable;
        _capture.RecordingStopped += OnRecordingStopped;
    }

    public event EventHandler<Exception>? Faulted;

    public void Start()
    {
        _accepting = true;
        try { _capture.StartRecording(); }
        catch (Exception error)
        {
            _accepting = false;
            throw new InvalidOperationException(
                $"无法启动音频端点“{_device.FriendlyName}”（{_device.ID}），设备格式：{_capture.WaveFormat}；{DescribeEndpointFormat()}。",
                error);
        }
    }

    public void Pause()
    {
        _accepting = false;
        lock (_readGate)
        {
            _buffer.ClearBuffer();
            _resampler.Dispose();
            _resampler = CreateResampler();
        }
    }

    public void Resume()
    {
        lock (_readGate) _buffer.ClearBuffer();
        _accepting = true;
    }

    public void Stop()
    {
        _accepting = false;
        _stopping = true;
        _capture.StopRecording();
    }

    public int Read(byte[] destination, int count)
    {
        lock (_readGate)
        {
            var read = _resampler.Read(destination, 0, count);
            if (read < count) destination.AsSpan(read, count - read).Clear();
            return count;
        }
    }

    private MediaFoundationResampler CreateResampler() => new(_buffer, TargetFormat) { ResamplerQuality = 60 };

    private static IWaveIn CreateMicrophoneCapture(MMDevice device)
    {
        return new WasapiCapture(device, useEventSync: false, audioBufferMillisecondsLength: 100);
    }

    private string DescribeEndpointFormat()
    {
        try
        {
            using var client = _device.AudioClient;
            var mix = client.MixFormat;
            var supported = client.IsFormatSupported(AudioClientShareMode.Shared, mix);
            return $"原生格式：{mix}（{mix.GetType().Name}，共享支持={supported}），默认周期={client.DefaultDevicePeriod}";
        }
        catch (Exception error)
        {
            return $"无法查询端点格式：{error.Message}";
        }
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs eventArgs)
    {
        if (!_accepting || eventArgs.BytesRecorded <= 0) return;
        try { _buffer.AddSamples(eventArgs.Buffer, 0, eventArgs.BytesRecorded); }
        catch (Exception error) { RaiseFault(new AudioBufferOverflowException($"音频采集缓冲区已满：{error.Message}")); }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs eventArgs)
    {
        if (!_stopping && eventArgs.Exception is not null) RaiseFault(eventArgs.Exception);
    }

    private void RaiseFault(Exception error)
    {
        if (Interlocked.Exchange(ref _faultRaised, 1) == 0) Faulted?.Invoke(this, error);
    }

    public void Dispose()
    {
        _accepting = false;
        _capture.DataAvailable -= OnDataAvailable;
        _capture.RecordingStopped -= OnRecordingStopped;
        try
        {
            if (!_stopping) _capture.StopRecording();
        }
        catch { }
        lock (_readGate) _resampler.Dispose();
        _capture.Dispose();
        _device.Dispose();
        _enumerator.Dispose();
    }
}

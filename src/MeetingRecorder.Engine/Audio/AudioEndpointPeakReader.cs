using NAudio.CoreAudioApi;

namespace MeetingRecorder.Engine.Audio;

internal sealed class AudioEndpointPeakReader : IDisposable
{
    private readonly MMDeviceEnumerator _enumerator = new();
    private readonly MMDevice? _device;

    public AudioEndpointPeakReader(string? endpointId, DataFlow flow)
    {
        try
        {
            _device = string.IsNullOrWhiteSpace(endpointId)
                ? _enumerator.GetDefaultAudioEndpoint(flow, Role.Multimedia)
                : _enumerator.GetDevice(endpointId);
        }
        catch
        {
            _device = null;
        }
    }

    public double ReadPeak()
    {
        try { return Math.Clamp(_device?.AudioMeterInformation.MasterPeakValue ?? 0, 0, 1); }
        catch { return 0; }
    }

    public void Dispose()
    {
        _device?.Dispose();
        _enumerator.Dispose();
    }
}

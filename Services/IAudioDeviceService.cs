namespace AudioPanel.Services;

public sealed class EndpointVolumeChangedEventArgs(string endpointId, double volume, bool muted) : EventArgs
{
    public string EndpointId { get; } = endpointId;
    public double Volume { get; } = volume;
    public bool Muted { get; } = muted;
}

public interface IAudioDeviceService : IAudioEndpointController, IDisposable
{
    event EventHandler? DevicesChanged;
    event EventHandler<EndpointVolumeChangedEventArgs>? VolumeChanged;
    AudioSnapshot GetSnapshot();
    void ObserveVolume(string? endpointId);
    double GetVolume(string endpointId);
    bool GetMute(string endpointId);
    void SetVolume(string endpointId, double value);
    void SetMute(string endpointId, bool muted);
    double GetPeakValue(string endpointId);
    void ReleaseMeter();
}

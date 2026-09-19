using AudioPanel.Models;

namespace AudioPanel.Services;

public interface IAudioEndpointController
{
    string? TryGetDefaultEndpointId(DeviceDirection direction, AudioEndpointRole role);
    bool IsEndpointActive(string endpointId, DeviceDirection direction);
    void SetDefaultEndpoint(string endpointId, AudioEndpointRole role);
}

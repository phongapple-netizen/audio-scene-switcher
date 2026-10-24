using AudioPanel.Models;
using Windows.Devices.Enumeration;

namespace AudioPanel.Services;

public interface ICameraDeviceSource
{
    Task<IReadOnlyList<DeviceInfo>> GetCamerasAsync(CancellationToken cancellationToken = default);
}

public sealed class CameraDeviceService : ICameraDeviceSource
{
    public async Task<IReadOnlyList<DeviceInfo>> GetCamerasAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var devices = await DeviceInformation.FindAllAsync(DeviceClass.VideoCapture);
        cancellationToken.ThrowIfCancellationRequested();
        return devices
            .Select(device => new DeviceInfo(
                device.Id,
                device.Name,
                DeviceDirection.Camera,
                device.IsEnabled ? DeviceState.Active : DeviceState.Disabled))
            .OrderByDescending(device => device.IsAvailable)
            .ThenBy(device => device.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }
}

using AudioPanel.Models;
using Windows.Devices.Enumeration;

namespace AudioPanel.Services;

public sealed class CameraDeviceService
{
    public async Task<IReadOnlyList<DeviceInfo>> GetCamerasAsync()
    {
        var devices = await DeviceInformation.FindAllAsync(DeviceClass.VideoCapture);
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

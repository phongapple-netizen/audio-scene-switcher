using System.Text.Json;
using AudioPanel.Models;
using AudioPanel.Services;

using var audio = new AudioDeviceService();
var cameraService = new CameraDeviceService();
var snapshot = audio.GetSnapshot();
var cameras = await cameraService.GetCamerasAsync();

if (args.Contains("--probe-switch", StringComparer.OrdinalIgnoreCase))
{
    var probes = new List<object>();
    var successful = true;
    foreach (var (direction, devices) in new[]
    {
        (DeviceDirection.Output, snapshot.Outputs),
        (DeviceDirection.Input, snapshot.Inputs)
    })
    {
        var original = snapshot.GetDefaultId(direction, AudioEndpointRole.Multimedia);
        var alternative = devices.FirstOrDefault(device => device.Id != original);
        if (original is null || alternative is null)
        {
            Console.Error.WriteLine($"无法执行 {direction} 切换探针：缺少原端点或备用端点");
            return 2;
        }

        var switched = false;
        var restored = false;
        try
        {
            audio.SetDefaultEndpoint(alternative.Id, AudioEndpointRole.Multimedia);
            switched = audio.TryGetDefaultEndpointId(
                direction, AudioEndpointRole.Multimedia) == alternative.Id;
        }
        finally
        {
            audio.SetDefaultEndpoint(original, AudioEndpointRole.Multimedia);
            restored = audio.TryGetDefaultEndpointId(
                direction, AudioEndpointRole.Multimedia) == original;
        }

        successful &= switched && restored;
        probes.Add(new
        {
            role = $"{direction}/Multimedia",
            original,
            alternative = alternative.Id,
            switched,
            restored
        });
    }

    Console.WriteLine(JsonSerializer.Serialize(
        new { probes }, new JsonSerializerOptions { WriteIndented = true }));
    return successful ? 0 : 3;
}

var report = new
{
    generatedAt = DateTimeOffset.Now,
    outputs = snapshot.Outputs.Select(Device),
    inputs = snapshot.Inputs.Select(Device),
    cameras = cameras.Select(Device),
    defaults = new
    {
        output = Roles(DeviceDirection.Output),
        input = Roles(DeviceDirection.Input)
    }
};

Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions
{
    WriteIndented = true
}));
return 0;

object Device(DeviceInfo device) => new
{
    device.Id,
    device.DisplayName,
    state = device.State.ToString(),
    device.IsDefault,
    device.IsDefaultCommunications,
    device.IsVirtual
};

object Roles(DeviceDirection direction) => new
{
    console = snapshot.GetDefaultId(direction, AudioEndpointRole.Console),
    multimedia = snapshot.GetDefaultId(direction, AudioEndpointRole.Multimedia),
    communications = snapshot.GetDefaultId(direction, AudioEndpointRole.Communications)
};

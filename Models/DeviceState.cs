namespace AudioPanel.Models;

/// <summary>
/// Device state as reported by the underlying Windows device layer.
/// </summary>
public enum DeviceState
{
    Active,
    Disabled,
    Unplugged,
    NotPresent,
    Unknown
}

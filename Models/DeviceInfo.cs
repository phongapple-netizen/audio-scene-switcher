using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace AudioPanel.Models;

/// <summary>
/// A displayable audio or camera device. It contains identifiers only; it never
/// opens, changes, or probes the device itself.
/// </summary>
public sealed class DeviceInfo : INotifyPropertyChanged
{
    private DeviceState _state;
    private bool _isDefault;
    private bool _isDefaultCommunications;

    public DeviceInfo(
        string id,
        string displayName,
        DeviceDirection direction,
        DeviceState state = DeviceState.Unknown)
    {
        Id = id ?? throw new ArgumentNullException(nameof(id));
        DisplayName = displayName ?? throw new ArgumentNullException(nameof(displayName));
        Direction = direction;
        _state = state;
    }

    public string Id { get; }

    public string DisplayName { get; }

    public DeviceDirection Direction { get; }

    public DeviceState State
    {
        get => _state;
        set
        {
            if (!SetField(ref _state, value))
            {
                return;
            }

            OnPropertyChanged(nameof(IsAvailable));
            OnPropertyChanged(nameof(StatusText));
        }
    }

    public bool IsDefault
    {
        get => _isDefault;
        set
        {
            if (SetField(ref _isDefault, value))
            {
                OnPropertyChanged(nameof(DisplayLabel));
            }
        }
    }

    public bool IsDefaultCommunications
    {
        get => _isDefaultCommunications;
        set
        {
            if (SetField(ref _isDefaultCommunications, value))
            {
                OnPropertyChanged(nameof(DisplayLabel));
            }
        }
    }

    /// <summary>
    /// Optional PnP information used for re-binding after an endpoint ID changes.
    /// It is not used as a replacement for the Core Audio endpoint ID.
    /// </summary>
    public string? ContainerId { get; init; }

    public string? PnpInstanceId { get; init; }

    public bool IsVirtual { get; init; }

    public bool IsAvailable => State == DeviceState.Active;

    public string DisplayLabel
    {
        get
        {
            var role = IsDefaultCommunications
                ? IsDefault ? "（默认、通信）" : "（通信）"
                : IsDefault ? "（默认）" : string.Empty;
            return DisplayName + role;
        }
    }

    public string StatusText => State switch
    {
        DeviceState.Active => "可用",
        DeviceState.Disabled => "已禁用",
        DeviceState.Unplugged => "已拔出",
        DeviceState.NotPresent => "不存在",
        _ => "未知"
    };

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

using System.ComponentModel;
using System.Text.Json.Serialization;

namespace AudioPanel.Models;

public sealed class CameraPreference
{
    public string? DeviceId { get; set; }
    public string? DisplayName { get; set; }
    public bool Enabled { get; set; } = true;
}

public sealed class SceneFallback
{
    public bool Enabled { get; set; } = true;
    public string? OutputEndpointId { get; set; }
    public string? InputEndpointId { get; set; }
    public string? CameraDeviceId { get; set; }
}

public sealed class AudioScene : INotifyPropertyChanged
{
    private bool _isSelected, _isApplied, _isReady;
    private string _stateText = "正在读取设备", _name = string.Empty;
    public const int MaxNameLength = 12;
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get => _name; set => Set(ref _name, value ?? string.Empty, nameof(Name)); }
    public string Description { get; set; } = string.Empty;
    public AudioEndpointSelection Output { get; set; } = new();
    public AudioEndpointSelection Input { get; set; } = new();
    public CameraPreference Camera { get; set; } = new();
    public SceneFallback Fallback { get; set; } = new();

    [JsonIgnore] public bool IsSelected => _isSelected;
    [JsonIgnore] public bool IsApplied => _isApplied;
    [JsonIgnore] public bool IsReady => _isReady;
    [JsonIgnore] public string StateText => _stateText;

    public void SetPresentation(bool selected, bool applied, bool ready, string stateText)
    {
        Set(ref _isSelected, selected, nameof(IsSelected));
        Set(ref _isApplied, applied, nameof(IsApplied));
        Set(ref _isReady, ready, nameof(IsReady));
        Set(ref _stateText, stateText, nameof(StateText));
    }

    public AudioScene Clone() => new()
    {
        Id = Id, Name = Name, Description = Description,
        Output = Output.Clone(), Input = Input.Clone(),
        Camera = new() { DeviceId = Camera.DeviceId, DisplayName = Camera.DisplayName, Enabled = Camera.Enabled },
        Fallback = new()
        {
            Enabled = Fallback.Enabled, OutputEndpointId = Fallback.OutputEndpointId,
            InputEndpointId = Fallback.InputEndpointId, CameraDeviceId = Fallback.CameraDeviceId
        }
    };

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Set<T>(ref T field, T value, string name)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

public sealed class AudioPanelConfiguration
{
    public const int CurrentVersion = 1;
    public int Version { get; set; } = CurrentVersion;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public bool FollowCommunicationEndpoint { get; set; } = true;
    public string? LastSceneId { get; set; }
    public AudioEndpointSelection CurrentOutput { get; set; } = new();
    public AudioEndpointSelection CurrentInput { get; set; } = new();
    public CameraPreference CurrentCamera { get; set; } = new();
    public List<AudioScene> Scenes { get; set; } = CreateDefaultScenes();

    public static AudioPanelConfiguration CreateDefault()
    {
        return new AudioPanelConfiguration { Scenes = CreateDefaultScenes() };
    }

    private static List<AudioScene> CreateDefaultScenes()
    {
        return
        [
            new() { Id = "independent", Name = "独立声卡", Description = "独立声卡或外置声卡" },
            new() { Id = "monitor", Name = "显示器", Description = "HDMI / DisplayPort 显示器音频" },
            new() { Id = "meeting", Name = "视频会议", Description = "摄像头麦克风与会议输出" },
            new() { Id = "fallback", Name = "备用设备", Description = "设备不可用时的回退选择" }
        ];
    }
}

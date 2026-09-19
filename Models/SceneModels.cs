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

public sealed class AudioScene
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public AudioEndpointSelection Output { get; set; } = new();
    public AudioEndpointSelection Input { get; set; } = new();
    public CameraPreference Camera { get; set; } = new();
    public SceneFallback Fallback { get; set; } = new();
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
            new() { Id = "independent", Name = "独立声卡", Description = "Audigy 或其他独立声卡" },
            new() { Id = "monitor", Name = "显示器", Description = "HDMI/显示器音频" },
            new() { Id = "meeting", Name = "视频会议", Description = "摄像头麦克风与会议输出" },
            new() { Id = "fallback", Name = "备用设备", Description = "设备不可用时的回退选择" }
        ];
    }
}

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace AudioPanel.Models;

public enum PanelStatusKind { Info, Success, Warning, Error }

public sealed class AudioPanelViewModel : INotifyPropertyChanged
{
    private DeviceInfo? _selectedOutput, _selectedInput, _selectedCamera;
    private DeviceInfo? _selectedCommunicationOutput, _selectedCommunicationInput;
    private AudioScene? _selectedScene;
    private bool _followCommunicationEndpoint = true, _isApplyingScene, _isInitialized, _isInitializing, _isMuted, _isEditorOpen;
    private int _sceneLoadDepth;
    private double _volume, _inputLevel;
    private string _statusText = "正在读取设备", _deviceSummaryText = "", _appliedSceneText = "正在读取";
    private string _currentOutputText = "正在读取", _currentInputText = "正在读取", _currentCameraText = "偏好未设置";
    private PanelStatusKind _statusKind;
    private IReadOnlyDictionary<(DeviceDirection, AudioEndpointRole), string?> _defaults =
        new Dictionary<(DeviceDirection, AudioEndpointRole), string?>();

    public ObservableCollection<DeviceInfo> OutputDevices { get; } = [];
    public ObservableCollection<DeviceInfo> InputDevices { get; } = [];
    public ObservableCollection<DeviceInfo> CameraDevices { get; } = [];
    public ObservableCollection<AudioScene> Scenes { get; } = [];
    public bool IsLoadingScene => _sceneLoadDepth > 0;
    public DeviceInfo? SelectedOutput
    {
        get => _selectedOutput;
        set { if (SetField(ref _selectedOutput, value) && FollowCommunicationEndpoint) SelectedCommunicationOutput = value; }
    }
    public DeviceInfo? SelectedInput
    {
        get => _selectedInput;
        set { if (SetField(ref _selectedInput, value) && FollowCommunicationEndpoint) SelectedCommunicationInput = value; }
    }
    public DeviceInfo? SelectedCamera { get => _selectedCamera; set => SetField(ref _selectedCamera, value); }
    public DeviceInfo? SelectedCommunicationOutput { get => _selectedCommunicationOutput; set => SetField(ref _selectedCommunicationOutput, value); }
    public DeviceInfo? SelectedCommunicationInput { get => _selectedCommunicationInput; set => SetField(ref _selectedCommunicationInput, value); }
    public AudioScene? SelectedScene { get => _selectedScene; private set => SetField(ref _selectedScene, value); }
    public bool FollowCommunicationEndpoint
    {
        get => _followCommunicationEndpoint;
        set
        {
            if (SetField(ref _followCommunicationEndpoint, value) && value)
            {
                SelectedCommunicationOutput = SelectedOutput;
                SelectedCommunicationInput = SelectedInput;
            }
        }
    }
    public bool IsApplyingScene
    {
        get => _isApplyingScene;
        set { if (SetField(ref _isApplyingScene, value)) NotifyAvailability(); }
    }
    public bool IsInitialized
    {
        get => _isInitialized;
        set { if (SetField(ref _isInitialized, value)) NotifyAvailability(); }
    }
    public bool IsInitializing
    {
        get => _isInitializing;
        set { if (SetField(ref _isInitializing, value)) NotifyAvailability(); }
    }
    public bool IsEditorOpen { get => _isEditorOpen; set => SetField(ref _isEditorOpen, value); }
    public bool CanEdit => IsInitialized && !IsInitializing && !IsApplyingScene;
    public bool CanRefresh => !IsInitializing && !IsApplyingScene;
    public bool CanApplySelectedScene => CanEdit && SelectedScene?.IsReady == true;
    public bool CanAdjustVolume => CanEdit &&
        (_defaults.GetValueOrDefault((DeviceDirection.Output, AudioEndpointRole.Console)) is not null ||
         _defaults.GetValueOrDefault((DeviceDirection.Output, AudioEndpointRole.Multimedia)) is not null);
    public string ApplyButtonText => IsApplyingScene ? "正在切换…" : $"应用“{SelectedScene?.Name ?? "选中"}”场景";
    public string SelectedSceneText => $"编辑场景：{SelectedScene?.Name ?? "未选择"}";
    public string SelectedSceneStateText => SelectedScene?.StateText ?? "请先选择场景";
    public bool SelectedSceneNeedsAttention => SelectedScene?.IsReady != true;
    public double Volume
    {
        get => _volume;
        set { if (SetField(ref _volume, Math.Clamp(value, 0, 1))) OnPropertyChanged(nameof(VolumeText)); }
    }
    public bool IsMuted
    {
        get => _isMuted;
        set { if (SetField(ref _isMuted, value)) { OnPropertyChanged(nameof(VolumeText)); OnPropertyChanged(nameof(MuteButtonText)); } }
    }
    public string VolumeText => IsMuted ? $"静音 · {Volume:P0}" : $"{Volume:P0}";
    public string MuteButtonText => IsMuted ? "取消静音" : "静音";
    public double InputLevel { get => _inputLevel; set { if (SetField(ref _inputLevel, Math.Clamp(value, 0, 1))) OnPropertyChanged(nameof(InputLevelText)); } }
    public string InputLevelText => $"{InputLevel:P0}";
    public string StatusText { get => _statusText; private set => SetField(ref _statusText, value); }
    public PanelStatusKind StatusKind { get => _statusKind; private set => SetField(ref _statusKind, value); }
    public string DeviceSummaryText { get => _deviceSummaryText; set => SetField(ref _deviceSummaryText, value); }
    public string AppliedSceneText { get => _appliedSceneText; private set => SetField(ref _appliedSceneText, value); }
    public string CurrentOutputText { get => _currentOutputText; private set => SetField(ref _currentOutputText, value); }
    public string CurrentInputText { get => _currentInputText; private set => SetField(ref _currentInputText, value); }
    public string CurrentCameraText { get => _currentCameraText; private set => SetField(ref _currentCameraText, value); }

    public void ReplaceDevices(IEnumerable<DeviceInfo> outputs, IEnumerable<DeviceInfo> inputs, IEnumerable<DeviceInfo> cameras)
    {
        _sceneLoadDepth++;
        try
        {
            Replace(OutputDevices, outputs); Replace(InputDevices, inputs); Replace(CameraDevices, cameras);
            SelectScene(SelectedScene);
        }
        finally { _sceneLoadDepth--; }
    }
    public void ReplaceScenes(IEnumerable<AudioScene> scenes, string? selectedSceneId)
    {
        _sceneLoadDepth++;
        try
        {
            Replace(Scenes, scenes);
            SelectScene(Scenes.FirstOrDefault(scene => scene.Id == selectedSceneId) ?? Scenes.FirstOrDefault());
        }
        finally { _sceneLoadDepth--; }
    }
    public void SelectScene(AudioScene? scene)
    {
        // Binding updates raise the same WPF events as user edits. Guard the whole load.
        _sceneLoadDepth++;
        try
        {
            SelectedScene = scene;
            FollowCommunicationEndpoint = scene is not null && scene.Output.FollowNormalForCommunications && scene.Input.FollowNormalForCommunications;
            SelectedOutput = Find(OutputDevices, scene?.Output.NormalEndpointId);
            SelectedInput = Find(InputDevices, scene?.Input.NormalEndpointId);
            SelectedCamera = Find(CameraDevices, scene?.Camera.DeviceId);
            SelectedCommunicationOutput = Find(OutputDevices, scene?.Output.GetEndpointId(AudioEndpointRole.Communications));
            SelectedCommunicationInput = Find(InputDevices, scene?.Input.GetEndpointId(AudioEndpointRole.Communications));
            SetCameraPreference(SelectedCamera);
        }
        finally { _sceneLoadDepth--; }
        RefreshSceneStates();
    }
    public void SetCurrentDefaults(DeviceInfo? output, DeviceInfo? input,
        IReadOnlyDictionary<(DeviceDirection, AudioEndpointRole), string?> defaults)
    {
        _defaults = defaults;
        CurrentOutputText = output?.DisplayName ?? "没有可用的默认输出";
        CurrentInputText = input?.DisplayName ?? "没有可用的默认输入";
        RefreshSceneStates();
    }
    /// <summary>Breaks ties when several scenes hold the same devices: the last one applied stays lit.</summary>
    public string? LastAppliedSceneId { get; set; }

    public void RefreshSceneStates()
    {
        var states = Scenes.Select(scene =>
        {
            var issues = new List<string>();
            Check("输出", scene.Output.NormalEndpointId, OutputDevices, issues);
            Check("输入", scene.Input.NormalEndpointId, InputDevices, issues);
            if (!scene.Output.FollowNormalForCommunications) Check("通信输出", scene.Output.CommunicationsEndpointId, OutputDevices, issues);
            if (!scene.Input.FollowNormalForCommunications) Check("通信输入", scene.Input.CommunicationsEndpointId, InputDevices, issues);
            var matches = issues.Count == 0 && new[] { DeviceDirection.Output, DeviceDirection.Input }.All(direction =>
                Enum.GetValues<AudioEndpointRole>().All(role =>
                    _defaults.GetValueOrDefault((direction, role)) ==
                    (direction == DeviceDirection.Output ? scene.Output : scene.Input).GetEndpointId(role)));
            return (scene, issues, matches);
        }).ToArray();
        var active = states.Where(state => state.matches).Select(state => state.scene).ToArray();
        var primary = active.FirstOrDefault(scene => scene.Id == LastAppliedSceneId) ?? active.FirstOrDefault();
        foreach (var (scene, issues, matches) in states)
        {
            var applied = scene == primary;
            scene.SetPresentation(scene == SelectedScene, applied, issues.Count == 0,
                issues.Count > 0 ? string.Join("；", issues)
                : applied ? "正在使用"
                : matches ? "设备相同"
                : "点击切换");
        }
        var matching = Scenes.Where(scene => scene.IsApplied).Select(scene => scene.Name).ToArray();
        AppliedSceneText = matching.Length == 0 ? "当前设备不属于任何场景" : $"正在使用：{string.Join("、", matching)}";
        OnPropertyChanged(nameof(SelectedSceneText)); OnPropertyChanged(nameof(SelectedSceneStateText));
        OnPropertyChanged(nameof(SelectedSceneNeedsAttention)); NotifyAvailability();
    }
    public void SetCameraPreference(DeviceInfo? camera) => CurrentCameraText = camera is null ? "未设置摄像头偏好" : $"偏好：{camera.DisplayName}";
    public void SetInputLevel(double value) => InputLevel = value;
    public void SetStatus(string message, PanelStatusKind kind = PanelStatusKind.Info) { StatusText = message; StatusKind = kind; }
    public event PropertyChangedEventHandler? PropertyChanged;
    private static void Check(string label, string? id, IEnumerable<DeviceInfo> devices, List<string> issues)
    {
        if (string.IsNullOrWhiteSpace(id)) issues.Add($"待配置{label}");
        else if (!devices.Any(device => device.Id == id && device.IsAvailable)) issues.Add($"{label}设备离线");
    }
    private void NotifyAvailability()
    {
        OnPropertyChanged(nameof(CanEdit)); OnPropertyChanged(nameof(CanRefresh)); OnPropertyChanged(nameof(CanApplySelectedScene));
        OnPropertyChanged(nameof(CanAdjustVolume)); OnPropertyChanged(nameof(ApplyButtonText));
    }
    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> values)
    {
        var snapshot = values.ToArray();
        target.Clear(); foreach (var value in snapshot) target.Add(value);
    }
    private static DeviceInfo? Find(IEnumerable<DeviceInfo> devices, string? id) => id is null ? null : devices.FirstOrDefault(device => device.Id == id);
    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value; OnPropertyChanged(name); return true;
    }
    private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

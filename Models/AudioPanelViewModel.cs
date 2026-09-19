using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace AudioPanel.Models;

public sealed class AudioPanelViewModel : INotifyPropertyChanged
{
    private DeviceInfo? _selectedOutput;
    private DeviceInfo? _selectedInput;
    private DeviceInfo? _selectedCamera;
    private DeviceInfo? _selectedCommunicationOutput;
    private DeviceInfo? _selectedCommunicationInput;
    private AudioScene? _selectedScene;
    private bool _followCommunicationEndpoint = true;
    private double _volume = 0.8;
    private double _inputLevel;
    private string _statusText = "原型：等待音频服务接入";
    private string _currentOutputText = "正在读取";
    private string _currentInputText = "正在读取";
    private string _currentCameraText = "偏好未设置";

    public AudioPanelViewModel()
    {
        var config = AudioPanelConfiguration.CreateDefault();
        Scenes = new ObservableCollection<AudioScene>(config.Scenes);
        SelectedScene = Scenes.FirstOrDefault();
    }

    public ObservableCollection<DeviceInfo> OutputDevices { get; } = [];
    public ObservableCollection<DeviceInfo> InputDevices { get; } = [];
    public ObservableCollection<DeviceInfo> CameraDevices { get; } = [];
    public ObservableCollection<AudioScene> Scenes { get; }

    public DeviceInfo? SelectedOutput
    {
        get => _selectedOutput;
        set
        {
            if (SetField(ref _selectedOutput, value) && FollowCommunicationEndpoint)
            {
                SelectedCommunicationOutput = value;
            }
        }
    }

    public DeviceInfo? SelectedInput
    {
        get => _selectedInput;
        set
        {
            if (SetField(ref _selectedInput, value) && FollowCommunicationEndpoint)
            {
                SelectedCommunicationInput = value;
            }
        }
    }

    public DeviceInfo? SelectedCamera
    {
        get => _selectedCamera;
        set { if (SetField(ref _selectedCamera, value)) OnPropertyChanged(nameof(CurrentCameraText)); }
    }

    public DeviceInfo? SelectedCommunicationOutput
    {
        get => _selectedCommunicationOutput;
        set => SetField(ref _selectedCommunicationOutput, value);
    }

    public DeviceInfo? SelectedCommunicationInput
    {
        get => _selectedCommunicationInput;
        set => SetField(ref _selectedCommunicationInput, value);
    }

    public AudioScene? SelectedScene
    {
        get => _selectedScene;
        set => SetField(ref _selectedScene, value);
    }

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

    public double Volume
    {
        get => _volume;
        set { if (SetField(ref _volume, Math.Clamp(value, 0, 1))) OnPropertyChanged(nameof(VolumeText)); }
    }

    public double InputLevel
    {
        get => _inputLevel;
        set { if (SetField(ref _inputLevel, Math.Clamp(value, 0, 1))) OnPropertyChanged(nameof(InputLevelText)); }
    }

    public string StatusText
    {
        get => _statusText;
        set => SetField(ref _statusText, value);
    }

    public string CurrentOutputText
    {
        get => _currentOutputText;
        private set => SetField(ref _currentOutputText, value);
    }

    public string CurrentInputText
    {
        get => _currentInputText;
        private set => SetField(ref _currentInputText, value);
    }

    public string CurrentCameraText
    {
        get => _currentCameraText;
        private set => SetField(ref _currentCameraText, value);
    }
    public string VolumeText => $"{Volume:P0}";
    public string InputLevelText => $"{InputLevel:P0}";

    public void ReplaceDevices(IEnumerable<DeviceInfo> outputs, IEnumerable<DeviceInfo> inputs, IEnumerable<DeviceInfo> cameras)
    {
        Replace(OutputDevices, outputs);
        Replace(InputDevices, inputs);
        Replace(CameraDevices, cameras);
        SelectedOutput = Find(OutputDevices, SelectedOutput?.Id);
        SelectedInput = Find(InputDevices, SelectedInput?.Id);
        SelectedCamera = Find(CameraDevices, SelectedCamera?.Id);
        SelectedCommunicationOutput = Find(OutputDevices, SelectedCommunicationOutput?.Id);
        SelectedCommunicationInput = Find(InputDevices, SelectedCommunicationInput?.Id);
    }

    public void ReplaceScenes(IEnumerable<AudioScene> scenes, string? selectedSceneId)
    {
        Replace(Scenes, scenes);
        SelectedScene = Scenes.FirstOrDefault(scene => scene.Id == selectedSceneId)
            ?? Scenes.FirstOrDefault();
        SelectScene(SelectedScene);
    }

    public void SelectScene(AudioScene? scene)
    {
        SelectedScene = scene;
        if (scene is null)
        {
            return;
        }

        SelectedOutput = Find(OutputDevices, scene.Output.NormalEndpointId);
        SelectedInput = Find(InputDevices, scene.Input.NormalEndpointId);
        SelectedCamera = Find(CameraDevices, scene.Camera.DeviceId);
        SelectedCommunicationOutput = Find(
            OutputDevices, scene.Output.GetEndpointId(AudioEndpointRole.Communications));
        SelectedCommunicationInput = Find(
            InputDevices, scene.Input.GetEndpointId(AudioEndpointRole.Communications));
        FollowCommunicationEndpoint =
            scene.Output.FollowNormalForCommunications &&
            scene.Input.FollowNormalForCommunications;
        CurrentCameraText = SelectedCamera is null
            ? "偏好未设置"
            : $"偏好：{SelectedCamera.DisplayName}";
    }

    public void SetCurrentDefaults(DeviceInfo? output, DeviceInfo? input)
    {
        CurrentOutputText = output?.DisplayName ?? "没有可用的默认输出";
        CurrentInputText = input?.DisplayName ?? "没有可用的默认输入";
    }

    public void SetCameraPreference(DeviceInfo? camera)
    {
        CurrentCameraText = camera is null
            ? "偏好未设置"
            : $"偏好：{camera.DisplayName}";
    }

    public void SetInputLevel(double value)
    {
        InputLevel = value;
        OnPropertyChanged(nameof(InputLevelText));
    }

    public void SetStatus(string message) => StatusText = message;

    public event PropertyChangedEventHandler? PropertyChanged;

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> values)
    {
        target.Clear();
        foreach (var value in values) target.Add(value);
    }

    private static DeviceInfo? Find(IEnumerable<DeviceInfo> devices, string? id) =>
        id is null ? null : devices.FirstOrDefault(d => d.Id == id);

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value; OnPropertyChanged(name); return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

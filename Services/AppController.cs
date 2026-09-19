using System.Windows.Threading;
using AudioPanel.Models;

namespace AudioPanel.Services;

public sealed class AppController : IDisposable
{
    private readonly MainWindow _window;
    private readonly ConfigurationService _configurationService = new();
    private readonly AudioDeviceService _audio = new();
    private readonly CameraDeviceService _cameras = new();
    private readonly OperationLogService _log = new();
    private readonly SceneApplyService _sceneApply;
    private readonly DispatcherTimer _meterTimer;
    private readonly DispatcherTimer _refreshTimer;
    private AudioPanelConfiguration _configuration = AudioPanelConfiguration.CreateDefault();
    private string? _currentOutputId;
    private string? _currentInputId;
    private bool _updatingUi;
    private bool _disposed;

    public AppController(MainWindow window)
    {
        _window = window;
        _sceneApply = new SceneApplyService(_audio);

        _meterTimer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(150),
            DispatcherPriority.Background,
            MeterTimer_Tick,
            window.Dispatcher);
        _refreshTimer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(350),
            DispatcherPriority.Background,
            RefreshTimer_Tick,
            window.Dispatcher)
        {
            IsEnabled = false
        };

        _window.SceneApplyRequested += Window_SceneApplyRequested;
        _window.DeviceSelectionChanged += Window_DeviceSelectionChanged;
        _window.VolumeChanged += Window_VolumeChanged;
        _window.CommunicationFollowChanged += Window_CommunicationFollowChanged;
        _window.RefreshRequested += Window_RefreshRequested;
        _audio.DevicesChanged += Audio_DevicesChanged;
    }

    public async Task<bool> InitializeAsync()
    {
        try
        {
            _window.SetStatus("正在读取 Core Audio 端点和摄像头");
            _configuration = await _configurationService.LoadAsync();
            await RefreshAsync(initializeScenes: true);
            _meterTimer.Start();
            return true;
        }
        catch (Exception exception)
        {
            _window.SetStatus($"初始化失败：{exception.Message}");
            await _log.WriteAsync("initialize-failed", exception.ToString());
            return false;
        }
    }

    private async Task RefreshAsync(bool initializeScenes = false)
    {
        if (_disposed)
        {
            return;
        }

        var selectedSceneId = _window.ViewModel.SelectedScene?.Id
            ?? _configuration.LastSceneId;
        var snapshot = _audio.GetSnapshot();
        IReadOnlyList<DeviceInfo> cameras;
        try
        {
            cameras = await _cameras.GetCamerasAsync();
        }
        catch (Exception exception)
        {
            cameras = [];
            await _log.WriteAsync("camera-enumeration-failed", exception.Message);
        }

        var defaultOutput = snapshot.GetNormalDefault(DeviceDirection.Output);
        var defaultInput = snapshot.GetNormalDefault(DeviceDirection.Input);
        _currentOutputId = defaultOutput?.Id;
        _currentInputId = defaultInput?.Id;

        if (initializeScenes)
        {
            InitializeDefaultScenes(snapshot, cameras);
        }

        _updatingUi = true;
        try
        {
            _window.ReplaceDevices(snapshot.Outputs, snapshot.Inputs, cameras);
            _window.ReplaceScenes(_configuration.Scenes, selectedSceneId);
            _window.ViewModel.SetCurrentDefaults(defaultOutput, defaultInput);

            if (_currentOutputId is not null)
            {
                _window.ViewModel.Volume = _audio.GetVolume(_currentOutputId);
            }
        }
        finally
        {
            _updatingUi = false;
        }

        var communicationDifferences = CountCommunicationDifferences(snapshot);
        var suffix = communicationDifferences == 0
            ? "普通与通信角色一致"
            : $"{communicationDifferences} 个通信角色使用不同端点";
        _window.SetStatus(
            $"已读取 {snapshot.Outputs.Count} 个输出、{snapshot.Inputs.Count} 个输入、{cameras.Count} 个摄像头；{suffix}");
    }

    private void InitializeDefaultScenes(
        AudioSnapshot snapshot,
        IReadOnlyList<DeviceInfo> cameras)
    {
        if (_configuration.Scenes.Any(scene =>
                scene.Output.NormalEndpointId is not null ||
                scene.Input.NormalEndpointId is not null ||
                scene.Camera.DeviceId is not null))
        {
            return;
        }

        var defaultOutput = snapshot.GetNormalDefault(DeviceDirection.Output);
        var defaultInput = snapshot.GetNormalDefault(DeviceDirection.Input);
        var audigyOutput = Find(snapshot.Outputs, "Sound Blaster", "Audigy");
        var audigyInput = Find(snapshot.Inputs, "Sound Blaster", "Audigy");
        var monitorOutput = Find(snapshot.Outputs, "AMD HDMI", "High Definition Audio");
        var emeetInput = Find(snapshot.Inputs, "EMEET");
        var emeetCamera = Find(cameras, "EMEET");

        foreach (var scene in _configuration.Scenes)
        {
            switch (scene.Id)
            {
                case "independent":
                    AssignAudio(scene, audigyOutput, audigyInput);
                    break;
                case "monitor":
                    AssignAudio(scene, monitorOutput, audigyInput ?? defaultInput);
                    break;
                case "meeting":
                    AssignAudio(scene, audigyOutput ?? defaultOutput, emeetInput);
                    scene.Camera.DeviceId = emeetCamera?.Id;
                    scene.Camera.DisplayName = emeetCamera?.DisplayName;
                    break;
                case "fallback":
                    AssignAudio(scene, defaultOutput, defaultInput);
                    break;
            }
        }

        _configuration.LastSceneId ??= "independent";
        _ = SaveConfigurationAsync();
    }

    private static void AssignAudio(
        AudioScene scene,
        DeviceInfo? output,
        DeviceInfo? input)
    {
        scene.Output.NormalEndpointId = output?.Id;
        scene.Output.CommunicationsEndpointId = output?.Id;
        scene.Output.FollowNormalForCommunications = true;
        scene.Input.NormalEndpointId = input?.Id;
        scene.Input.CommunicationsEndpointId = input?.Id;
        scene.Input.FollowNormalForCommunications = true;
    }

    private static DeviceInfo? Find(
        IEnumerable<DeviceInfo> devices,
        params string[] nameParts) =>
        devices.FirstOrDefault(device =>
            nameParts.Any(part =>
                device.DisplayName.Contains(part, StringComparison.OrdinalIgnoreCase)));

    private static int CountCommunicationDifferences(AudioSnapshot snapshot)
    {
        var count = 0;
        foreach (var direction in new[] { DeviceDirection.Output, DeviceDirection.Input })
        {
            var normal = snapshot.GetDefaultId(direction, AudioEndpointRole.Console);
            var communications = snapshot.GetDefaultId(direction, AudioEndpointRole.Communications);
            if (normal != communications)
            {
                count++;
            }
        }

        return count;
    }

    private async void Window_SceneApplyRequested(
        object? sender,
        SceneApplyRequestedEventArgs e)
    {
        try
        {
            _window.SetStatus($"正在应用场景“{e.Scene.Name}”并逐项回读");
            var result = await _sceneApply.ApplyAsync(e.Scene);
            _window.SetStatus(result.Message);
            await _log.WriteAsync($"scene-{result.Status}", result.Message);

            if (result.Status == SceneApplyStatus.Succeeded)
            {
                _configuration.LastSceneId = e.Scene.Id;
                await SaveConfigurationAsync();
            }

            await RefreshAsync();
        }
        catch (Exception exception)
        {
            _window.SetStatus($"场景执行失败：{exception.Message}");
            await _log.WriteAsync("scene-unhandled-failed", exception.ToString());
        }
    }

    private async void Window_DeviceSelectionChanged(
        object? sender,
        DeviceSelectionChangedEventArgs e)
    {
        if (_updatingUi || e.Device is null || _window.ViewModel.SelectedScene is not { } scene)
        {
            return;
        }

        switch (e.Direction)
        {
            case DeviceDirection.Output:
                if (e.Role == AudioEndpointRole.Communications)
                {
                    scene.Output.CommunicationsEndpointId = e.Device.Id;
                }
                else
                {
                    scene.Output.NormalEndpointId = e.Device.Id;
                    if (scene.Output.FollowNormalForCommunications)
                    {
                        scene.Output.CommunicationsEndpointId = e.Device.Id;
                    }
                }
                break;
            case DeviceDirection.Input:
                if (e.Role == AudioEndpointRole.Communications)
                {
                    scene.Input.CommunicationsEndpointId = e.Device.Id;
                }
                else
                {
                    scene.Input.NormalEndpointId = e.Device.Id;
                    if (scene.Input.FollowNormalForCommunications)
                    {
                        scene.Input.CommunicationsEndpointId = e.Device.Id;
                    }
                }
                break;
            case DeviceDirection.Camera:
                scene.Camera.DeviceId = e.Device.Id;
                scene.Camera.DisplayName = e.Device.DisplayName;
                _window.ViewModel.SetCameraPreference(e.Device);
                break;
        }

        _window.SetStatus($"场景“{scene.Name}”已更新；点击“应用选中场景”执行切换");
        await SaveConfigurationAsync();
    }

    private async void Window_CommunicationFollowChanged(object? sender, EventArgs e)
    {
        if (_updatingUi || _window.ViewModel.SelectedScene is not { } scene)
        {
            return;
        }

        var follow = _window.ViewModel.FollowCommunicationEndpoint;
        scene.Output.FollowNormalForCommunications = follow;
        scene.Input.FollowNormalForCommunications = follow;
        if (follow)
        {
            scene.Output.CommunicationsEndpointId = scene.Output.NormalEndpointId;
            scene.Input.CommunicationsEndpointId = scene.Input.NormalEndpointId;
        }

        await SaveConfigurationAsync();
    }

    private async void Window_VolumeChanged(object? sender, VolumeChangedEventArgs e)
    {
        if (_updatingUi || _currentOutputId is null)
        {
            return;
        }

        try
        {
            _audio.SetVolume(_currentOutputId, e.Volume);
        }
        catch (Exception exception)
        {
            _window.SetStatus($"音量调整失败：{exception.Message}");
            await _log.WriteAsync("volume-failed", exception.Message);
        }
    }

    private async void Window_RefreshRequested(object? sender, EventArgs e)
    {
        try
        {
            await RefreshAsync();
        }
        catch (Exception exception)
        {
            _window.SetStatus($"刷新失败：{exception.Message}");
            await _log.WriteAsync("refresh-failed", exception.ToString());
        }
    }

    private void Audio_DevicesChanged(object? sender, EventArgs e)
    {
        if (_disposed || _window.Dispatcher.HasShutdownStarted)
        {
            return;
        }

        _window.Dispatcher.BeginInvoke(() =>
        {
            _refreshTimer.Stop();
            _refreshTimer.Start();
        });
    }

    private async void RefreshTimer_Tick(object? sender, EventArgs e)
    {
        _refreshTimer.Stop();
        try
        {
            await RefreshAsync();
        }
        catch (Exception exception)
        {
            _window.SetStatus($"设备变化后刷新失败：{exception.Message}");
            await _log.WriteAsync("device-refresh-failed", exception.ToString());
        }
    }

    private void MeterTimer_Tick(object? sender, EventArgs e)
    {
        if (_currentInputId is null)
        {
            _window.ViewModel.SetInputLevel(0);
            return;
        }

        try
        {
            _window.ViewModel.SetInputLevel(_audio.GetPeakValue(_currentInputId));
        }
        catch
        {
            _window.ViewModel.SetInputLevel(0);
        }
    }

    private async Task SaveConfigurationAsync()
    {
        try
        {
            await _configurationService.SaveAsync(_configuration);
        }
        catch (Exception exception)
        {
            _window.SetStatus($"配置保存失败：{exception.Message}");
            await _log.WriteAsync("configuration-save-failed", exception.ToString());
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _meterTimer.Stop();
        _refreshTimer.Stop();
        _audio.DevicesChanged -= Audio_DevicesChanged;
        _audio.Dispose();
    }
}

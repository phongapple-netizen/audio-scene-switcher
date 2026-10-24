using System.Windows;
using System.Windows.Threading;
using AudioPanel.Models;

namespace AudioPanel.Services;

public sealed class SceneApplyCompletedEventArgs(AudioScene scene, SceneApplyResult result, bool fromTray) : EventArgs
{
    public AudioScene Scene { get; } = scene;
    public SceneApplyResult Result { get; } = result;
    public bool FromTray { get; } = fromTray;
}

public sealed class AppController : IDisposable
{
    private readonly MainWindow _window;
    private readonly ConfigurationService _configurationService;
    private readonly IAudioDeviceService _audio;
    private readonly ICameraDeviceSource _cameras;
    private readonly OperationLogService _log;
    private readonly SceneApplyService _sceneApply;
    private readonly DispatcherTimer _meterTimer, _refreshTimer;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private AudioPanelConfiguration _configuration = AudioPanelConfiguration.CreateDefault();
    private string? _currentOutputId, _currentInputId;
    private double _knownVolume;
    private bool _updatingUi, _disposed, _isExiting, _refreshPending;
    private Task<bool> _lastSaveTask = Task.FromResult(true);
    private Task _sceneOperation = Task.CompletedTask;

    public AppController(MainWindow window, ConfigurationService? configurationService = null,
        IAudioDeviceService? audio = null, ICameraDeviceSource? cameras = null, OperationLogService? log = null)
    {
        _window = window;
        _configurationService = configurationService ?? new();
        _audio = audio ?? new AudioDeviceService();
        _cameras = cameras ?? new CameraDeviceService();
        _log = log ?? new();
        _sceneApply = new(_audio);
        _meterTimer = new DispatcherTimer(DispatcherPriority.Background, window.Dispatcher) { Interval = TimeSpan.FromMilliseconds(150) };
        _refreshTimer = new DispatcherTimer(DispatcherPriority.Background, window.Dispatcher) { Interval = TimeSpan.FromMilliseconds(350) };
        _meterTimer.Tick += MeterTimer_Tick;
        _refreshTimer.Tick += RefreshTimer_Tick;
        _window.SceneApplyRequested += Window_SceneApplyRequested;
        _window.DeviceSelectionChanged += Window_DeviceSelectionChanged;
        _window.SceneRenameRequested += Window_SceneRenameRequested;
        _window.VolumeChanged += Window_VolumeChanged;
        _window.MuteRequested += Window_MuteRequested;
        _window.CommunicationFollowChanged += Window_CommunicationFollowChanged;
        _window.RefreshRequested += Window_RefreshRequested;
        _window.IsVisibleChanged += Window_VisibilityChanged;
        _window.StateChanged += Window_StateChanged;
        _audio.DevicesChanged += Audio_DevicesChanged;
        _audio.VolumeChanged += Audio_VolumeChanged;
    }

    public event EventHandler<SceneApplyCompletedEventArgs>? SceneApplyCompleted;

    public async Task<bool> InitializeAsync()
    {
        if (_disposed || _isExiting || _window.ViewModel.IsInitializing) return false;
        _window.ViewModel.IsInitializing = true;
        try
        {
            _window.SetStatus("正在读取音频设备和摄像头");
            _configuration = await _configurationService.LoadAsync(_lifetime.Token);
            await RefreshAsync(initializeScenes: true);
            if (_disposed || _isExiting) return false;
            _window.SetStatus("点击场景直接切换；铅笔图标可编辑设备");
            return true;
        }
        catch (OperationCanceledException) when (_disposed || _isExiting) { return false; }
        catch (Exception exception)
        {
            _window.SetStatus($"初始化失败：{exception.Message}", PanelStatusKind.Error);
            await _log.WriteAsync("initialize-failed", exception.ToString());
            return false;
        }
        finally
        {
            _window.ViewModel.IsInitializing = false;
            if (_refreshPending && !_disposed && !_isExiting)
            {
                _refreshPending = false;
                _refreshTimer.Start();
            }
        }
    }

    private async Task RefreshAsync(bool initializeScenes = false)
    {
        if (_disposed || _isExiting) return;
        await _refreshGate.WaitAsync(_lifetime.Token);
        try
        {
            if (_disposed || _isExiting) return;
            var selectedSceneId = initializeScenes ? _configuration.LastSceneId : _window.ViewModel.SelectedScene?.Id;
            var snapshot = _audio.GetSnapshot();
            IReadOnlyList<DeviceInfo> cameras;
            try { cameras = await _cameras.GetCamerasAsync(_lifetime.Token); }
            catch (OperationCanceledException) { throw; }
            catch (Exception exception)
            {
                cameras = [];
                await _log.WriteAsync("camera-enumeration-failed", exception.Message);
            }
            if (_disposed || _isExiting) return;
            var output = snapshot.GetNormalDefault(DeviceDirection.Output);
            var input = snapshot.GetNormalDefault(DeviceDirection.Input);
            _currentOutputId = output?.Id;
            _currentInputId = input?.Id;
            if (initializeScenes && DefaultSceneDetector.Apply(_configuration, snapshot, cameras)) await QueueSaveAsync();
            _updatingUi = true;
            try
            {
                _window.ViewModel.LastAppliedSceneId = _configuration.LastSceneId;
                _window.ReplaceDevices(snapshot.Outputs, snapshot.Inputs, cameras);
                _window.ReplaceScenes(_configuration.Scenes, selectedSceneId ?? _configuration.LastSceneId);
                _window.ViewModel.SetCurrentDefaults(output, input, snapshot.Defaults);
                _audio.ObserveVolume(_currentOutputId);
                _knownVolume = _currentOutputId is null ? 0 : _audio.GetVolume(_currentOutputId);
                _window.ViewModel.Volume = _knownVolume;
                _window.ViewModel.IsMuted = _currentOutputId is not null && _audio.GetMute(_currentOutputId);
                _window.ViewModel.IsInitialized = true;
            }
            finally { _updatingUi = false; }
            var differences = CountCommunicationDifferences(snapshot);
            _window.ViewModel.DeviceSummaryText =
                $"{snapshot.Outputs.Count} 个输出 · {snapshot.Inputs.Count} 个输入 · {cameras.Count} 个摄像头" +
                (differences == 0 ? " · 通信设备跟随普通设备" : $" · {differences} 个通信设备不同");
            UpdateMeterActivity();
        }
        finally { _refreshGate.Release(); }
    }

    private static int CountCommunicationDifferences(AudioSnapshot snapshot) =>
        new[] { DeviceDirection.Output, DeviceDirection.Input }.Count(direction =>
            snapshot.GetDefaultId(direction, AudioEndpointRole.Console) != snapshot.GetDefaultId(direction, AudioEndpointRole.Communications) ||
            snapshot.GetDefaultId(direction, AudioEndpointRole.Multimedia) != snapshot.GetDefaultId(direction, AudioEndpointRole.Communications));

    private void Window_SceneApplyRequested(object? sender, SceneApplyRequestedEventArgs e)
    {
        if (_disposed || _isExiting || _window.ViewModel.IsApplyingScene) return;
        _sceneOperation = ApplySceneAsync(e.Scene.Clone(), e.FromTray);
    }
    private async Task ApplySceneAsync(AudioScene scene, bool fromTray)
    {
        var vm = _window.ViewModel;
        vm.IsApplyingScene = true;
        UpdateMeterActivity();
        SceneApplyResult result;
        try
        {
            _window.SetStatus($"正在应用“{scene.Name}”并核验设备");
            await Dispatcher.Yield(DispatcherPriority.Background);
            result = await _sceneApply.ApplyAsync(scene);
            _window.SetStatus(result.Message, result.Status == SceneApplyStatus.Succeeded ? PanelStatusKind.Success : PanelStatusKind.Error);
            await _log.WriteAsync($"scene-{result.Status}", result.Message + " | " + string.Join(" | ",
                result.Roles.Where(role => !role.Verified).Select(role => $"{role.Direction}/{role.Role}: {role.Message}")));
            if (result.Status == SceneApplyStatus.Succeeded)
            {
                _configuration.LastSceneId = scene.Id;
                await QueueSaveAsync();
            }
            // Refresh only changes the device summary, never the operation result.
            try { await RefreshAsync(); }
            catch (Exception exception)
            {
                vm.DeviceSummaryText = $"设备列表刷新失败：{exception.Message}";
                await _log.WriteAsync("post-scene-refresh-failed", exception.ToString());
            }
        }
        catch (Exception exception)
        {
            result = new() { Status = SceneApplyStatus.Failed, Message = $"场景执行失败：{exception.Message}", Roles = [] };
            _window.SetStatus(result.Message, PanelStatusKind.Error);
            await _log.WriteAsync("scene-unhandled-failed", exception.ToString());
        }
        finally { vm.IsApplyingScene = false; UpdateMeterActivity(); }
        SceneApplyCompleted?.Invoke(this, new(scene, result, fromTray));
    }

    private bool CanAcceptEdit => !_disposed && !_isExiting && !_updatingUi && !_window.ViewModel.IsLoadingScene && _window.ViewModel.CanEdit;
    private async void Window_DeviceSelectionChanged(object? sender, DeviceSelectionChangedEventArgs e)
    {
        if (!CanAcceptEdit || e.Device is null || _window.ViewModel.SelectedScene is not { } scene) return;
        if (e.Direction == DeviceDirection.Camera)
        {
            scene.Camera.DeviceId = e.Device.Id; scene.Camera.DisplayName = e.Device.DisplayName;
            _window.ViewModel.SetCameraPreference(e.Device);
        }
        else
        {
            var selection = e.Direction == DeviceDirection.Output ? scene.Output : scene.Input;
            if (e.Role == AudioEndpointRole.Communications) selection.CommunicationsEndpointId = e.Device.Id;
            else
            {
                selection.NormalEndpointId = e.Device.Id;
                if (selection.FollowNormalForCommunications) selection.CommunicationsEndpointId = e.Device.Id;
            }
        }
        _window.ViewModel.RefreshSceneStates();
        _window.SetStatus($"“{scene.Name}”已保存；应用场景后才会切换音频设备");
        await QueueSaveAsync();
    }
    private async void Window_SceneRenameRequested(object? sender, SceneRenameRequestedEventArgs e)
    {
        if (!CanAcceptEdit) return;
        var name = e.Name.Trim();
        if (name.Length == 0) return;
        if (name.Length > AudioScene.MaxNameLength) name = name[..AudioScene.MaxNameLength];
        var oldName = e.Scene.Name;
        e.Scene.Name = name;
        _window.ViewModel.RefreshSceneStates();
        _window.SetStatus($"“{oldName}”已改名为“{name}”");
        await QueueSaveAsync();
    }
    private async void Window_CommunicationFollowChanged(object? sender, EventArgs e)
    {
        if (!CanAcceptEdit || _window.ViewModel.SelectedScene is not { } scene) return;
        var follow = _window.ViewModel.FollowCommunicationEndpoint;
        scene.Output.FollowNormalForCommunications = scene.Input.FollowNormalForCommunications = follow;
        if (follow)
        {
            scene.Output.CommunicationsEndpointId = scene.Output.NormalEndpointId;
            scene.Input.CommunicationsEndpointId = scene.Input.NormalEndpointId;
        }
        _window.ViewModel.RefreshSceneStates();
        await QueueSaveAsync();
    }
    private async void Window_VolumeChanged(object? sender, VolumeChangedEventArgs e)
    {
        if (!CanAcceptEdit || _currentOutputId is null || Math.Abs(e.Volume - _knownVolume) < 0.000001) return;
        try { _audio.SetVolume(_currentOutputId, e.Volume); _knownVolume = e.Volume; }
        catch (Exception exception)
        {
            _window.SetStatus($"音量调整失败：{exception.Message}", PanelStatusKind.Error);
            await _log.WriteAsync("volume-failed", exception.Message);
        }
    }
    private async void Window_MuteRequested(object? sender, EventArgs e)
    {
        if (!CanAcceptEdit || _currentOutputId is null) return;
        try
        {
            var muted = !_audio.GetMute(_currentOutputId);
            _audio.SetMute(_currentOutputId, muted);
            _window.ViewModel.IsMuted = muted;
        }
        catch (Exception exception)
        {
            _window.SetStatus($"静音调整失败：{exception.Message}", PanelStatusKind.Error);
            await _log.WriteAsync("mute-failed", exception.Message);
        }
    }
    private void Audio_VolumeChanged(object? sender, EndpointVolumeChangedEventArgs e)
    {
        if (_disposed || _isExiting || _window.Dispatcher.HasShutdownStarted) return;
        _window.Dispatcher.BeginInvoke(() =>
        {
            if (_disposed || _isExiting || e.EndpointId != _currentOutputId) return;
            _updatingUi = true;
            try { _knownVolume = e.Volume; _window.ViewModel.Volume = e.Volume; _window.ViewModel.IsMuted = e.Muted; }
            finally { _updatingUi = false; }
        });
    }
    private async void Window_RefreshRequested(object? sender, EventArgs e)
    {
        if (_disposed || _isExiting || _window.ViewModel.IsInitializing || _window.ViewModel.IsApplyingScene) return;
        try
        {
            if (!_window.ViewModel.IsInitialized) await InitializeAsync();
            else await RefreshAsync();
        }
        catch (OperationCanceledException) when (_disposed || _isExiting) { }
        catch (Exception exception)
        {
            _window.ViewModel.DeviceSummaryText = $"刷新失败：{exception.Message}";
            await _log.WriteAsync("refresh-failed", exception.ToString());
        }
    }
    private void Audio_DevicesChanged(object? sender, EventArgs e)
    {
        if (_disposed || _isExiting || _window.Dispatcher.HasShutdownStarted) return;
        _window.Dispatcher.BeginInvoke(() =>
        {
            if (_disposed || _isExiting) return;
            if (_window.ViewModel.IsInitializing) { _refreshPending = true; return; }
            _refreshTimer.Stop(); _refreshTimer.Start();
        });
    }
    private async void RefreshTimer_Tick(object? sender, EventArgs e)
    {
        _refreshTimer.Stop();
        if (_window.ViewModel.IsInitializing) { _refreshPending = true; return; }
        try
        {
            if (!_window.ViewModel.IsInitialized) await InitializeAsync();
            else await RefreshAsync();
        }
        catch (OperationCanceledException) when (_disposed || _isExiting) { }
        catch (Exception exception)
        {
            _window.ViewModel.DeviceSummaryText = $"设备变化后刷新失败：{exception.Message}";
            await _log.WriteAsync("device-refresh-failed", exception.ToString());
        }
    }
    private void Window_VisibilityChanged(object sender, DependencyPropertyChangedEventArgs e) => UpdateMeterActivity();
    private void Window_StateChanged(object? sender, EventArgs e) => UpdateMeterActivity();
    private void UpdateMeterActivity()
    {
        var active = !_disposed && !_isExiting && _window.IsVisible && _window.WindowState != WindowState.Minimized &&
            _window.ViewModel.IsInitialized && !_window.ViewModel.IsApplyingScene && _currentInputId is not null;
        if (active) _meterTimer.Start();
        else { _meterTimer.Stop(); _audio.ReleaseMeter(); _window.ViewModel.SetInputLevel(0); }
    }
    private void MeterTimer_Tick(object? sender, EventArgs e)
    {
        try { _window.ViewModel.SetInputLevel(_currentInputId is null ? 0 : _audio.GetPeakValue(_currentInputId)); }
        catch { _audio.ReleaseMeter(); _window.ViewModel.SetInputLevel(0); }
    }
    private Task<bool> QueueSaveAsync() => _lastSaveTask = SaveConfigurationAsync();
    private async Task<bool> SaveConfigurationAsync()
    {
        try { await _configurationService.SaveAsync(_configuration); return true; }
        catch (Exception exception)
        {
            _window.SetStatus($"配置保存失败：{exception.Message}", PanelStatusKind.Error);
            await _log.WriteAsync("configuration-save-failed", exception.ToString());
            return false;
        }
    }
    public async Task PrepareForExitAsync()
    {
        _isExiting = true;
        _window.ViewModel.IsInitialized = false;
        UpdateMeterActivity(); _refreshTimer.Stop();
        await _sceneOperation;
        await _lastSaveTask;
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _lifetime.Cancel();
        _meterTimer.Stop(); _refreshTimer.Stop();
        _meterTimer.Tick -= MeterTimer_Tick; _refreshTimer.Tick -= RefreshTimer_Tick;
        _window.SceneApplyRequested -= Window_SceneApplyRequested;
        _window.DeviceSelectionChanged -= Window_DeviceSelectionChanged;
        _window.SceneRenameRequested -= Window_SceneRenameRequested;
        _window.VolumeChanged -= Window_VolumeChanged;
        _window.MuteRequested -= Window_MuteRequested;
        _window.CommunicationFollowChanged -= Window_CommunicationFollowChanged;
        _window.RefreshRequested -= Window_RefreshRequested;
        _window.IsVisibleChanged -= Window_VisibilityChanged;
        _window.StateChanged -= Window_StateChanged;
        _audio.DevicesChanged -= Audio_DevicesChanged;
        _audio.VolumeChanged -= Audio_VolumeChanged;
        _audio.Dispose();
    }
}

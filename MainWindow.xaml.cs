using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using AudioPanel.Models;

namespace AudioPanel;

public sealed class SceneApplyRequestedEventArgs(AudioScene scene) : EventArgs
{
    public AudioScene Scene { get; } = scene;
}

public sealed class DeviceSelectionChangedEventArgs(
    DeviceDirection direction,
    AudioEndpointRole role,
    DeviceInfo? device) : EventArgs
{
    public DeviceDirection Direction { get; } = direction;
    public AudioEndpointRole Role { get; } = role;
    public DeviceInfo? Device { get; } = device;
}

public sealed class VolumeChangedEventArgs(double volume) : EventArgs
{
    public double Volume { get; } = volume;
}

/// <summary>
/// Presentation shell only. The parent agent can subscribe to the events and
/// feed device snapshots/status back through the public view-model methods.
/// </summary>
public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContext = ViewModel;
    }

    public AudioPanelViewModel ViewModel { get; } = new();

    public event EventHandler<SceneApplyRequestedEventArgs>? SceneApplyRequested;
    public event EventHandler<DeviceSelectionChangedEventArgs>? DeviceSelectionChanged;
    public event EventHandler<VolumeChangedEventArgs>? VolumeChanged;
    public event EventHandler? CommunicationFollowChanged;
    public event EventHandler? RefreshRequested;

    public void ReplaceDevices(IEnumerable<DeviceInfo> outputs, IEnumerable<DeviceInfo> inputs, IEnumerable<DeviceInfo> cameras) =>
        ViewModel.ReplaceDevices(outputs, inputs, cameras);

    public void SetStatus(string message) => ViewModel.SetStatus(message);

    public void ReplaceScenes(IEnumerable<AudioScene> scenes, string? selectedSceneId) =>
        ViewModel.ReplaceScenes(scenes, selectedSceneId);

    private void SceneButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button { Tag: AudioScene scene })
        {
            ViewModel.SelectScene(scene);
            SetStatus($"已选择场景“{scene.Name}”，可调整设备后应用");
        }
    }

    private void ApplySceneButton_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedScene is null)
        {
            SetStatus("请先选择场景");
            return;
        }

        SetStatus("已发送场景应用请求，等待音频服务响应");
        SceneApplyRequested?.Invoke(this, new SceneApplyRequestedEventArgs(ViewModel.SelectedScene));
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e) =>
        RefreshRequested?.Invoke(this, EventArgs.Empty);

    private void Output_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        RaiseSelection(DeviceDirection.Output, AudioEndpointRole.Console, e);

    private void Input_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        RaiseSelection(DeviceDirection.Input, AudioEndpointRole.Console, e);

    private void Camera_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        RaiseSelection(DeviceDirection.Camera, AudioEndpointRole.Console, e);

    private void CommunicationOutput_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        RaiseSelection(DeviceDirection.Output, AudioEndpointRole.Communications, e);

    private void CommunicationInput_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        RaiseSelection(DeviceDirection.Input, AudioEndpointRole.Communications, e);

    private void RaiseSelection(
        DeviceDirection direction,
        AudioEndpointRole role,
        SelectionChangedEventArgs e)
    {
        if (!IsInitialized || e.AddedItems.Count == 0) return;
        DeviceSelectionChanged?.Invoke(this, new DeviceSelectionChangedEventArgs(
            direction, role, e.AddedItems[0] as DeviceInfo));
    }

    private void FollowCommunication_Changed(object sender, RoutedEventArgs e)
    {
        if (IsInitialized) CommunicationFollowChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Volume_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (IsInitialized) VolumeChanged?.Invoke(this, new VolumeChangedEventArgs(e.NewValue));
    }
}

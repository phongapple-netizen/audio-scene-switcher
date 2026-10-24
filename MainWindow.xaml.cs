using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using System.Windows.Controls.Primitives;
using AudioPanel.Models;
using AudioPanel.Services;

namespace AudioPanel;

public sealed class SceneApplyRequestedEventArgs(AudioScene scene, bool fromTray = false) : EventArgs
{
    public AudioScene Scene { get; } = scene;
    public bool FromTray { get; } = fromTray;
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
/// Presentation shell: binding updates never count as scene edits.
/// </summary>
public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        MaxHeight = Math.Max(360, SystemParameters.WorkArea.Height - 16);
        MinHeight = Math.Min(MinHeight, MaxHeight);
        Icon = AppIconFactory.CreateWindowIcon();
        DataContext = ViewModel;
        Loaded += (_, _) => PositionNearTray();
        SizeChanged += (_, _) => { if (_anchorToTray) PositionNearTray(); };
    }

    private bool _anchorToTray = true, _positioning;

    /// <summary>Docks the panel above the notification area; it stays there until the user drags it away.</summary>
    public void AnchorToTray()
    {
        _anchorToTray = true;
        if (IsLoaded) PositionNearTray();
    }

    private void PositionNearTray()
    {
        if (!_anchorToTray || ActualWidth <= 0) return;
        var area = SystemParameters.WorkArea;
        _positioning = true;
        try
        {
            Left = area.Right - ActualWidth - 12;
            Top = Math.Max(area.Top + 12, area.Bottom - ActualHeight - 12);
        }
        finally { _positioning = false; }
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            // The user started dragging the window: stop snapping it back to the tray corner.
            HwndSource.FromHwnd(hwnd)?.AddHook((IntPtr _hwnd, int msg, IntPtr _wParam, IntPtr _lParam, ref bool _handled) =>
            {
                if (msg == 0x0231 && !_positioning) _anchorToTray = false; // WM_ENTERSIZEMOVE
                return IntPtr.Zero;
            });
            var dark = 1;
            // DWMWA_USE_IMMERSIVE_DARK_MODE: 20 on Windows 10 20H1+ / 11, 19 on older builds.
            if (DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int)) != 0)
                DwmSetWindowAttribute(hwnd, 19, ref dark, sizeof(int));
            // DWMWA_CAPTION_COLOR (Windows 11): match the window background #0F1218 (COLORREF is 0x00BBGGRR).
            var caption = 0x0018120F;
            DwmSetWindowAttribute(hwnd, 35, ref caption, sizeof(int));
        }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    public AudioPanelViewModel ViewModel { get; } = new();

    public event EventHandler<SceneApplyRequestedEventArgs>? SceneApplyRequested;
    public event EventHandler<DeviceSelectionChangedEventArgs>? DeviceSelectionChanged;
    public event EventHandler<VolumeChangedEventArgs>? VolumeChanged;
    public event EventHandler? CommunicationFollowChanged;
    public event EventHandler? RefreshRequested;
    public event EventHandler? MuteRequested;

    public void ReplaceDevices(IEnumerable<DeviceInfo> outputs, IEnumerable<DeviceInfo> inputs, IEnumerable<DeviceInfo> cameras) =>
        ViewModel.ReplaceDevices(outputs, inputs, cameras);

    public void SetStatus(string message, PanelStatusKind kind = PanelStatusKind.Info) => ViewModel.SetStatus(message, kind);

    public void ReplaceScenes(IEnumerable<AudioScene> scenes, string? selectedSceneId) =>
        ViewModel.ReplaceScenes(scenes, selectedSceneId);

    public bool RequestSceneApply(string sceneId)
    {
        if (!ViewModel.CanEdit) return false;
        var scene = ViewModel.Scenes.FirstOrDefault(candidate => candidate.Id == sceneId);
        if (scene is null)
        {
            SetStatus("找不到请求的场景");
            return false;
        }

        ViewModel.SelectScene(scene);
        if (!scene.IsReady)
        {
            SetStatus($"场景“{scene.Name}”：{scene.StateText}", PanelStatusKind.Warning);
            return false;
        }
        SceneApplyRequested?.Invoke(this, new SceneApplyRequestedEventArgs(scene, fromTray: true));
        return true;
    }

    public void RequestRefresh() => RefreshRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>One click switches: select the scene and apply it right away.</summary>
    private void SceneButton_Click(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.CanEdit || sender is not System.Windows.Controls.Button { Tag: AudioScene scene }) return;
        ViewModel.SelectScene(scene);
        if (!scene.IsReady)
        {
            OpenEditor();
            SetStatus($"“{scene.Name}”还不能切换：{scene.StateText}", PanelStatusKind.Warning);
            return;
        }
        SceneApplyRequested?.Invoke(this, new SceneApplyRequestedEventArgs(scene));
    }

    private void EditSceneButton_Click(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.CanEdit || sender is not System.Windows.Controls.Button { Tag: AudioScene scene }) return;
        var reopen = ViewModel.IsEditorOpen && ViewModel.SelectedScene == scene;
        ViewModel.SelectScene(scene);
        if (reopen) { ViewModel.IsEditorOpen = false; return; }
        OpenEditor();
        SetStatus($"正在编辑“{scene.Name}”，修改会自动保存");
    }

    private void OpenEditor()
    {
        ViewModel.IsEditorOpen = true;
        Dispatcher.InvokeAsync(() => EditorCard.BringIntoView(), DispatcherPriority.Loaded);
    }

    private void ApplySceneButton_Click(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.CanApplySelectedScene || ViewModel.SelectedScene is null)
        {
            SetStatus("请先选择场景");
            return;
        }

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
        if (!IsInitialized || ViewModel.IsLoadingScene || !ViewModel.CanEdit || e.AddedItems.Count == 0) return;
        DeviceSelectionChanged?.Invoke(this, new DeviceSelectionChangedEventArgs(
            direction, role, e.AddedItems[0] as DeviceInfo));
    }

    private void FollowCommunication_Changed(object sender, RoutedEventArgs e)
    {
        if (IsInitialized && !ViewModel.IsLoadingScene && ViewModel.CanEdit) CommunicationFollowChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Volume_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (IsInitialized && !ViewModel.IsLoadingScene) VolumeChanged?.Invoke(this, new VolumeChangedEventArgs(e.NewValue));
    }

    private void MuteButton_Click(object sender, RoutedEventArgs e) => MuteRequested?.Invoke(this, EventArgs.Empty);
}

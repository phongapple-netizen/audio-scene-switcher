using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AudioPanel.Services;
using Forms = System.Windows.Forms;
using DrawingColor = System.Drawing.Color;
using DrawingFontStyle = System.Drawing.FontStyle;

namespace AudioPanel;

/// <summary>
/// Owns the single instance, tray UI, window visibility and application lifetime.
/// </summary>
public partial class App : System.Windows.Application
{
    private Forms.NotifyIcon? _notifyIcon;
    private Icon? _trayIcon;
    private bool _exitRequested;
    private bool _exiting;
    private AppController? _controller;
    private SingleInstanceService? _instance;
    private readonly StartupService _startup = new();
    private readonly List<Forms.ToolStripMenuItem> _sceneMenuItems = [];
    private Font? _menuOpenFont;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var renderSmoke = e.Args.Contains("--render-smoke", StringComparer.OrdinalIgnoreCase);
        var smoke = renderSmoke || e.Args.Contains("--smoke", StringComparer.OrdinalIgnoreCase);
        var startInTray = e.Args.Contains("--startup", StringComparer.OrdinalIgnoreCase);
        if (!smoke)
        {
            _instance = new SingleInstanceService();
            if (!_instance.IsPrimary)
            {
                var activated = startInTray || await _instance.ActivateExistingAsync();
                if (!activated) System.Windows.MessageBox.Show("面板正在启动或退出，请稍后从托盘打开。", "音频设备切换面板");
                Shutdown(activated ? 0 : 1);
                return;
            }
        }

        var window = new MainWindow();
        MainWindow = window;
        window.Closing += MainWindow_Closing;

        _controller = new AppController(window);
        _controller.SceneApplyCompleted += Controller_SceneApplyCompleted;
        window.ViewModel.PropertyChanged += (_, _) => UpdateTrayText();
        CreateTrayIcon();
        _instance?.StartListening(() =>
        {
            if (!Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(ShowMainWindow);
        });
        if (!startInTray || renderSmoke) window.Show();
        if (smoke)
        {
            _ = RunSmokeAsync(window, renderSmoke, startInTray);
        }
        else
        {
            _ = _controller.InitializeAsync();
        }
    }

    private async Task RunSmokeAsync(Window window, bool render, bool startInTray)
    {
        var initialized = _controller is not null && await _controller.InitializeAsync();
        await Task.Delay(750);
        if (initialized && startInTray && !render) initialized = !window.IsVisible;
        if (initialized && render)
        {
            initialized = RenderWindow(window);
        }
        if (_controller is not null) await _controller.PrepareForExitAsync();
        _exitRequested = true;
        window.Close();
        Shutdown(initialized ? 0 : 1);
    }

    private static bool RenderWindow(Window window)
    {
        try
        {
            window.UpdateLayout();
            if (window.Content is not FrameworkElement content) return false;
            var dpi = VisualTreeHelper.GetDpi(window);
            var clientWidth = content.ActualWidth + content.Margin.Left + content.Margin.Right;
            var clientHeight = content.ActualHeight + content.Margin.Top + content.Margin.Bottom;
            var width = Math.Max(1, (int)Math.Ceiling(clientWidth * dpi.DpiScaleX));
            var height = Math.Max(1, (int)Math.Ceiling(clientHeight * dpi.DpiScaleY));
            var bitmap = new RenderTargetBitmap(
                width, height, 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
            var visual = new DrawingVisual();
            using (var drawing = visual.RenderOpen())
            {
                drawing.DrawRectangle(window.Background, null, new Rect(0, 0, clientWidth, clientHeight));
                drawing.DrawRectangle(new VisualBrush(content), null,
                    new Rect(content.Margin.Left, content.Margin.Top, content.ActualWidth, content.ActualHeight));
            }
            bitmap.Render(visual);

            var path = Environment.GetEnvironmentVariable("AUDIO_PANEL_SCREENSHOT");
            if (string.IsNullOrWhiteSpace(path))
            {
                path = Path.Combine(AppDataPaths.DirectoryPath, "ui-smoke.png");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = File.Create(path);
            encoder.Save(stream);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void CreateTrayIcon()
    {
        var menu = new Forms.ContextMenuStrip();
        var openItem = menu.Items.Add("打开面板", null, (_, _) => ShowMainWindow());
        _menuOpenFont = new Font(openItem.Font, DrawingFontStyle.Bold);
        openItem.Font = _menuOpenFont;
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(new Forms.ToolStripLabel("快速切换场景")
        {
            ForeColor = DrawingColor.FromArgb(100, 100, 100)
        });
        menu.ShowItemToolTips = true;
        var startupItem = new Forms.ToolStripMenuItem("开机启动");
        startupItem.Click += (_, _) => ToggleStartup(startupItem);
        RefreshStartupItem(startupItem);
        menu.Opening += (_, _) =>
        {
            RefreshTrayScenes(menu);
            RefreshStartupItem(startupItem);
        };
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("刷新设备", null, (_, _) =>
        {
            if (MainWindow is MainWindow window)
            {
                window.RequestRefresh();
            }
        });
        menu.Items.Add(startupItem);
        menu.Items.Add("退出", null, async (_, _) => await ExitApplicationAsync());

        _trayIcon = AppIconFactory.CreateTrayIcon();
        _notifyIcon = new Forms.NotifyIcon
        {
            Icon = _trayIcon,
            Text = "音频切换面板",
            ContextMenuStrip = menu,
            Visible = true
        };
        _notifyIcon.MouseClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) ShowMainWindow(); };
        UpdateTrayText();
    }

    private void RefreshStartupItem(Forms.ToolStripMenuItem item)
    {
        try
        {
            item.Checked = _startup.IsEnabled;
            item.Enabled = true;
            item.ToolTipText = "当前账户登录后在托盘运行，不自动切换音频";
        }
        catch
        {
            item.Checked = false;
            item.Enabled = false;
            item.ToolTipText = "无法读取启动设置";
        }
    }

    private void ToggleStartup(Forms.ToolStripMenuItem item)
    {
        try
        {
            var enabled = !_startup.IsEnabled;
            _startup.SetEnabled(enabled);
            RefreshStartupItem(item);
            _notifyIcon?.ShowBalloonTip(2000, enabled ? "已开启开机启动" : "已关闭开机启动",
                enabled ? "下次登录后，面板会自动在托盘运行。" : "面板将不再随登录自动启动。", Forms.ToolTipIcon.Info);
        }
        catch (Exception exception)
        {
            RefreshStartupItem(item);
            System.Windows.MessageBox.Show($"设置开机启动失败：{exception.Message}", "音频设备切换面板",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void RefreshTrayScenes(Forms.ContextMenuStrip menu)
    {
        foreach (var item in _sceneMenuItems) { menu.Items.Remove(item); item.Dispose(); }
        _sceneMenuItems.Clear();
        if (MainWindow is not MainWindow window) return;
        var insertAt = 3;
        foreach (var scene in window.ViewModel.Scenes)
        {
            var item = new Forms.ToolStripMenuItem(scene.Name)
            {
                Checked = scene.IsApplied,
                Enabled = window.ViewModel.CanEdit && scene.IsReady,
                ToolTipText = scene.StateText
            };
            var sceneId = scene.Id;
            item.Click += (_, _) => { if (!window.RequestSceneApply(sceneId)) ShowMainWindow(); };
            menu.Items.Insert(insertAt++, item);
            _sceneMenuItems.Add(item);
        }
    }

    private void UpdateTrayText()
    {
        if (_notifyIcon is null || MainWindow is not MainWindow window) return;
        var text = "音频设备切换面板\n" + window.ViewModel.AppliedSceneText;
        _notifyIcon.Text = text.Length > 63 ? text[..63] : text;
    }

    private void Controller_SceneApplyCompleted(object? sender, SceneApplyCompletedEventArgs e)
    {
        if (!e.FromTray || _notifyIcon is null || _exiting) return;
        var success = e.Result.Status == SceneApplyStatus.Succeeded;
        var saved = MainWindow is MainWindow window && window.ViewModel.StatusKind != Models.PanelStatusKind.Error;
        _notifyIcon.ShowBalloonTip(2500, success && saved ? "场景已切换" : "场景需要注意",
            success && saved ? $"已切换为“{e.Scene.Name}”" : MainWindow is MainWindow panel ? panel.ViewModel.StatusText : e.Result.Message,
            success && saved ? Forms.ToolTipIcon.Info : Forms.ToolTipIcon.Warning);
        if (!success || !saved) ShowMainWindow();
    }

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_exitRequested)
        {
            return;
        }

        e.Cancel = true;
        if (sender is Window window)
        {
            window.Hide();
        }
    }

    private void ShowMainWindow()
    {
        if (_exiting || MainWindow is not Window window)
        {
            return;
        }

        if (window.WindowState == WindowState.Minimized)
        {
            window.WindowState = WindowState.Normal;
        }

        if (window is AudioPanel.MainWindow panel) panel.AnchorToTray();
        window.Show();
        window.Activate();
    }

    private async Task ExitApplicationAsync()
    {
        if (_exiting) return;
        _exiting = true;
        if (_controller is not null) await _controller.PrepareForExitAsync();
        _exitRequested = true;
        DisposeTrayIcon();
        _controller?.Dispose();
        _controller = null;
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        DisposeTrayIcon();
        _controller?.Dispose();
        _controller = null;
        _instance?.Dispose();
        _instance = null;
        base.OnExit(e);
    }

    private void DisposeTrayIcon()
    {
        if (_notifyIcon is not null)
        {
            var menu = _notifyIcon.ContextMenuStrip;
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
            _notifyIcon = null;
            menu?.Dispose();
            _sceneMenuItems.Clear();
        }

        _menuOpenFont?.Dispose();
        _menuOpenFont = null;

        _trayIcon?.Dispose();
        _trayIcon = null;
    }
}

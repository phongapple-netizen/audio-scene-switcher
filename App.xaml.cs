using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AudioPanel.Services;
using Forms = System.Windows.Forms;

namespace AudioPanel;

/// <summary>
/// WPF application shell. The audio service is intentionally not created here yet;
/// the tray shell only owns window visibility and application lifetime.
/// </summary>
public partial class App : System.Windows.Application
{
    private Forms.NotifyIcon? _notifyIcon;
    private bool _exitRequested;
    private AppController? _controller;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var window = new MainWindow();
        MainWindow = window;
        window.Closing += MainWindow_Closing;

        CreateTrayIcon();
        window.Show();
        _controller = new AppController(window);
        var renderSmoke = e.Args.Contains("--render-smoke", StringComparer.OrdinalIgnoreCase);
        if (renderSmoke || e.Args.Contains("--smoke", StringComparer.OrdinalIgnoreCase))
        {
            _ = RunSmokeAsync(window, renderSmoke);
        }
        else
        {
            _ = _controller.InitializeAsync();
        }
    }

    private async Task RunSmokeAsync(Window window, bool render)
    {
        var initialized = _controller is not null && await _controller.InitializeAsync();
        await Task.Delay(750);
        if (initialized && render)
        {
            initialized = RenderWindow(window);
        }
        _exitRequested = true;
        window.Close();
        Shutdown(initialized ? 0 : 1);
    }

    private static bool RenderWindow(Window window)
    {
        try
        {
            window.UpdateLayout();
            var dpi = VisualTreeHelper.GetDpi(window);
            var width = Math.Max(1, (int)Math.Ceiling(window.ActualWidth * dpi.DpiScaleX));
            var height = Math.Max(1, (int)Math.Ceiling(window.ActualHeight * dpi.DpiScaleY));
            var bitmap = new RenderTargetBitmap(
                width, height, 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
            bitmap.Render(window);

            var path = Environment.GetEnvironmentVariable("AUDIO_PANEL_SCREENSHOT");
            if (string.IsNullOrWhiteSpace(path))
            {
                path = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "AudioPanel",
                    "ui-smoke.png");
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
        menu.Items.Add("打开面板", null, (_, _) => ShowMainWindow());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => ExitApplication());

        _notifyIcon = new Forms.NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "音频切换面板",
            ContextMenuStrip = menu,
            Visible = true
        };
        _notifyIcon.DoubleClick += (_, _) => ShowMainWindow();
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
        if (MainWindow is not Window window)
        {
            return;
        }

        if (window.WindowState == WindowState.Minimized)
        {
            window.WindowState = WindowState.Normal;
        }

        window.Show();
        window.Activate();
    }

    private void ExitApplication()
    {
        _exitRequested = true;
        _notifyIcon?.Dispose();
        _notifyIcon = null;
        _controller?.Dispose();
        _controller = null;
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _notifyIcon?.Dispose();
        _notifyIcon = null;
        _controller?.Dispose();
        _controller = null;
        base.OnExit(e);
    }
}

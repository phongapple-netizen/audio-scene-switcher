using System.IO;
using Microsoft.Win32;

namespace AudioPanel.Services;

/// <summary>Starts this installation in the tray when the current user signs in.</summary>
public sealed class StartupService
{
    public const string ValueName = "AudioPanel";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private readonly string _executablePath;

    public StartupService()
    {
        var processPath = Environment.ProcessPath;
        _executablePath = processPath is not null &&
            !string.Equals(Path.GetFileName(processPath), "dotnet.exe", StringComparison.OrdinalIgnoreCase)
            ? processPath
            : Path.Combine(AppContext.BaseDirectory, "AudioPanel.exe");
    }

    public string Command => $"\"{_executablePath}\" --startup";

    public bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return string.Equals(key?.GetValue(ValueName) as string, Command, StringComparison.OrdinalIgnoreCase);
        }
    }

    public void SetEnabled(bool enabled)
    {
        if (enabled)
        {
            if (!File.Exists(_executablePath)) throw new FileNotFoundException("找不到当前程序，请重新打开面板后设置。", _executablePath);
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            key.SetValue(ValueName, Command, RegistryValueKind.String);
        }
        else
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            key?.DeleteValue(ValueName, throwOnMissingValue: false);
        }

        if (IsEnabled != enabled) throw new IOException("启动设置未保存，请稍后重试。");
    }
}

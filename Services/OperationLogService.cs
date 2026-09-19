using System.IO;

namespace AudioPanel.Services;

public sealed class OperationLogService
{
    private readonly string _logPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AudioPanel",
        "audio-panel.log");

    public async Task WriteAsync(string category, string message)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_logPath)!);
            var line = $"{DateTimeOffset.Now:O}\t{category}\t{message}{Environment.NewLine}";
            await File.AppendAllTextAsync(_logPath, line);
        }
        catch (IOException)
        {
            // Logging must never break device control.
        }
        catch (UnauthorizedAccessException)
        {
            // Keep the panel usable if the log directory is unavailable.
        }
    }
}

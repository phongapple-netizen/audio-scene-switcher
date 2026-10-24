using System.IO;

namespace AudioPanel.Services;

public sealed class OperationLogService
{
    private readonly string _logPath;
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    public OperationLogService(string? logPath = null) =>
        _logPath = logPath ?? Path.Combine(AppDataPaths.DirectoryPath, "audio-panel.log");

    public async Task WriteAsync(string category, string message)
    {
        await _writeGate.WaitAsync();
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
        finally { _writeGate.Release(); }
    }
}

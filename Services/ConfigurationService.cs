using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using AudioPanel.Models;

namespace AudioPanel.Services;

/// <summary>
/// Persists user preferences without talking to the audio stack.
/// </summary>
public sealed class ConfigurationService
{
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public ConfigurationService(string? configPath = null)
    {
        ConfigPath = configPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AudioPanel",
            "config.json");
    }

    public string ConfigPath { get; }

    public async Task<AudioPanelConfiguration> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(ConfigPath)) return AudioPanelConfiguration.CreateDefault();

        try
        {
            await using var stream = File.OpenRead(ConfigPath);
            var result = await JsonSerializer.DeserializeAsync<AudioPanelConfiguration>(
                stream, _jsonOptions, cancellationToken);
            if (result is null) throw new JsonException("配置为空");
            result.Scenes ??= [];
            if (result.Scenes.Count == 0) result.Scenes = AudioPanelConfiguration.CreateDefault().Scenes;
            if (result.Version <= 0) result.Version = AudioPanelConfiguration.CurrentVersion;
            return result;
        }
        catch (JsonException)
        {
            BackupCorruptFile();
            return AudioPanelConfiguration.CreateDefault();
        }
    }

    public async Task SaveAsync(AudioPanelConfiguration configuration, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        await _saveGate.WaitAsync(cancellationToken);
        try
        {
        var directory = Path.GetDirectoryName(ConfigPath)
            ?? throw new InvalidOperationException("配置路径没有目录");
        Directory.CreateDirectory(directory);

        configuration.Version = AudioPanelConfiguration.CurrentVersion;
        configuration.UpdatedAt = DateTimeOffset.UtcNow;
        var tempPath = Path.Combine(directory, $".{Path.GetFileName(ConfigPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(
                tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, useAsync: true))
            {
                await JsonSerializer.SerializeAsync(stream, configuration, _jsonOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            if (File.Exists(ConfigPath))
                File.Replace(tempPath, ConfigPath, destinationBackupFileName: null, ignoreMetadataErrors: true);
            else
                File.Move(tempPath, ConfigPath);
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }
        }
        finally
        {
            _saveGate.Release();
        }
    }

    private void BackupCorruptFile()
    {
        if (!File.Exists(ConfigPath)) return;
        var badPath = $"{ConfigPath}.bad-{DateTime.Now:yyyyMMdd-HHmmssfff}.json";
        try { File.Move(ConfigPath, badPath); }
        catch (IOException) { /* Leave the original for manual recovery if locked. */ }
    }
}

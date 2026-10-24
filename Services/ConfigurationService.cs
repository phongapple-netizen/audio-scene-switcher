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
        ConfigPath = configPath ?? Path.Combine(AppDataPaths.DirectoryPath, "config.json");
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
            if (result.Scenes.Any(scene => scene is null || scene.Output is null || scene.Input is null ||
                    scene.Camera is null || scene.Fallback is null || string.IsNullOrWhiteSpace(scene.Id)) ||
                result.Scenes.Select(scene => scene.Id).Distinct(StringComparer.Ordinal).Count() != result.Scenes.Count)
                throw new JsonException("场景结构不完整或场景 ID 重复");
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
        // Freeze on the caller's thread before the first await, so later UI edits
        // cannot alter a save already queued behind the write gate.
        configuration.Version = AudioPanelConfiguration.CurrentVersion;
        configuration.UpdatedAt = DateTimeOffset.UtcNow;
        var payload = JsonSerializer.SerializeToUtf8Bytes(configuration, _jsonOptions);
        await _saveGate.WaitAsync(cancellationToken);
        try
        {
        var directory = Path.GetDirectoryName(ConfigPath)
            ?? throw new InvalidOperationException("配置路径没有目录");
        Directory.CreateDirectory(directory);

        var tempPath = Path.Combine(directory, $".{Path.GetFileName(ConfigPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(
                tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, useAsync: true))
            {
                await stream.WriteAsync(payload, cancellationToken);
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

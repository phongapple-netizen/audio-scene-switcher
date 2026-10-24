using System.IO;

namespace AudioPanel.Services;

public static class AppDataPaths
{
    public static string DirectoryPath =>
        Environment.GetEnvironmentVariable("AUDIO_PANEL_DATA_DIRECTORY") is { Length: > 0 } overridePath
            ? Path.GetFullPath(overridePath)
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AudioPanel");
}

using AudioPanel.Models;

namespace AudioPanel.Services;

/// <summary>
/// First-run guesses for the built-in scenes from device names. A scene whose devices are not
/// recognised stays unset ("待配置") instead of being filled with an unrelated device.
/// </summary>
public static class DefaultSceneDetector
{
    // Endpoints served by the graphics driver. "Realtek High Definition Audio" is onboard audio, so
    // only the GPU vendors' "High Definition Audio" drivers count.
    private static readonly string[] DisplayAudio =
        ["HDMI", "DisplayPort", "Display Audio", "NVIDIA High Definition Audio", "AMD High Definition Audio", "Intel(R) Display"];
    private static readonly string[] DedicatedCards =
        ["Sound Blaster", "Creative", "Audigy", "Xonar", "Essence", "Focusrite", "Scarlett", "Behringer", "MOTU",
         "Audient", "PreSonus", "Steinberg", "Universal Audio", "FiiO", "Topping"];
    private static readonly string[] WebcamWords = ["Webcam", "Cam", "Camera", "摄像头"];
    private static readonly string[] MicrophoneWords = ["麦克风", "Microphone", "Mic"];

    /// <summary>Fills the scenes only when none has any device yet; returns whether anything changed.</summary>
    public static bool Apply(AudioPanelConfiguration configuration, AudioSnapshot snapshot, IReadOnlyList<DeviceInfo> cameras)
    {
        if (configuration.Scenes.Any(scene => scene.Output.NormalEndpointId is not null ||
            scene.Input.NormalEndpointId is not null || scene.Camera.DeviceId is not null)) return false;

        var outputs = snapshot.Outputs.Where(device => device.IsAvailable && !device.IsVirtual).ToArray();
        var inputs = snapshot.Inputs.Where(device => device.IsAvailable && !device.IsVirtual)
            .OrderBy(device => ContainsAny(device.DisplayName, MicrophoneWords) ? 0 : 1).ToArray();
        var defaultOutput = snapshot.GetNormalDefault(DeviceDirection.Output);
        var defaultInput = snapshot.GetNormalDefault(DeviceDirection.Input);

        var displayOutput = outputs.FirstOrDefault(device => ContainsAny(device.DisplayName, DisplayAudio));
        var cardOutput = outputs.FirstOrDefault(device => device != displayOutput && ContainsAny(device.DisplayName, DedicatedCards));
        var cardInput = cardOutput is null ? null
            : inputs.FirstOrDefault(device => SameHardware(device, cardOutput)) ??
              inputs.FirstOrDefault(device => ContainsAny(device.DisplayName, DedicatedCards));
        var camera = cameras.FirstOrDefault(device => ContainsAny(device.DisplayName, WebcamWords)) ?? cameras.FirstOrDefault();
        var cameraMic = inputs.FirstOrDefault(device => camera is not null && SameHardware(device, camera)) ??
            inputs.FirstOrDefault(device => ContainsAny(DevicePart(device.DisplayName), WebcamWords));

        foreach (var scene in configuration.Scenes)
        {
            switch (scene.Id)
            {
                case "independent": Assign(scene, cardOutput, cardInput); break;
                case "monitor": Assign(scene, displayOutput, displayOutput is null ? null : cardInput ?? defaultInput); break;
                case "meeting":
                    Assign(scene, cameraMic is null ? null : cardOutput ?? defaultOutput, cameraMic);
                    scene.Camera.DeviceId = camera?.Id;
                    scene.Camera.DisplayName = camera?.DisplayName;
                    break;
                case "fallback": Assign(scene, defaultOutput, defaultInput); break;
            }
        }
        configuration.LastSceneId ??= configuration.Scenes.FirstOrDefault()?.Id;
        return true;
    }

    /// <summary>"扬声器 (Sound Blaster Audigy 5/Rx)" → "Sound Blaster Audigy 5/Rx"; names without a suffix are returned whole.</summary>
    public static string DevicePart(string name)
    {
        name = name.Trim();
        if (!name.EndsWith(')')) return name;
        var depth = 0;
        for (var i = name.Length - 1; i >= 0; i--)
        {
            if (name[i] == ')') depth++;
            else if (name[i] == '(' && --depth == 0) return i == 0 ? name : name[(i + 1)..^1].Trim();
        }
        return name;
    }

    private static bool SameHardware(DeviceInfo endpoint, DeviceInfo other)
    {
        var part = DevicePart(endpoint.DisplayName);
        var otherPart = other.Direction == DeviceDirection.Camera ? other.DisplayName.Trim() : DevicePart(other.DisplayName);
        if (part.Length < 4 || otherPart.Length < 4) return false;
        return part.Equals(otherPart, StringComparison.OrdinalIgnoreCase) ||
            (other.Direction == DeviceDirection.Camera &&
             (part.Contains(otherPart, StringComparison.OrdinalIgnoreCase) || otherPart.Contains(part, StringComparison.OrdinalIgnoreCase)));
    }

    private static void Assign(AudioScene scene, DeviceInfo? output, DeviceInfo? input)
    {
        scene.Output.NormalEndpointId = scene.Output.CommunicationsEndpointId = output?.Id;
        scene.Input.NormalEndpointId = scene.Input.CommunicationsEndpointId = input?.Id;
        scene.Output.FollowNormalForCommunications = scene.Input.FollowNormalForCommunications = true;
    }

    private static bool ContainsAny(string text, string[] parts) =>
        parts.Any(part => text.Contains(part, StringComparison.OrdinalIgnoreCase));
}

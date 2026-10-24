using System.Runtime.InteropServices;
using AudioPanel.Interop;
using AudioPanel.Models;

namespace AudioPanel.Services;

public sealed class AudioSnapshot
{
    private readonly IReadOnlyDictionary<(DeviceDirection Direction, AudioEndpointRole Role), string?> _defaults;

    public AudioSnapshot(
        IReadOnlyList<DeviceInfo> outputs,
        IReadOnlyList<DeviceInfo> inputs,
        IReadOnlyDictionary<(DeviceDirection, AudioEndpointRole), string?> defaults)
    {
        Outputs = outputs;
        Inputs = inputs;
        _defaults = defaults;
    }

    public IReadOnlyList<DeviceInfo> Outputs { get; }
    public IReadOnlyList<DeviceInfo> Inputs { get; }
    public IReadOnlyDictionary<(DeviceDirection, AudioEndpointRole), string?> Defaults => _defaults;

    public string? GetDefaultId(DeviceDirection direction, AudioEndpointRole role) =>
        _defaults.TryGetValue((direction, role), out var id) ? id : null;

    public DeviceInfo? GetNormalDefault(DeviceDirection direction)
    {
        var id = GetDefaultId(direction, AudioEndpointRole.Console)
            ?? GetDefaultId(direction, AudioEndpointRole.Multimedia);
        var devices = direction == DeviceDirection.Output ? Outputs : Inputs;
        return devices.FirstOrDefault(device => device.Id == id);
    }
}

public sealed class AudioDeviceService : IAudioDeviceService, IMMNotificationClient
{
    private readonly IMMDeviceEnumerator _enumerator;
    private volatile bool _disposed;
    private readonly Guid _volumeEventContext = Guid.NewGuid();
    private IAudioEndpointVolume? _volume;
    private EndpointVolumeCallback? _volumeCallback;
    private string? _volumeEndpointId;
    private IAudioMeterInformation? _meter;
    private string? _meterEndpointId;
    private IAudioClient? _captureAudioClient;
    private IAudioCaptureClient? _captureClient;

    public AudioDeviceService()
    {
        _enumerator = (IMMDeviceEnumerator)Activator.CreateInstance(
            Type.GetTypeFromCLSID(CoreAudioIds.MMDeviceEnumerator, throwOnError: true)!)!;
        ComResult.ThrowIfFailed(_enumerator.RegisterEndpointNotificationCallback(this));
    }

    public event EventHandler? DevicesChanged;
    public event EventHandler<EndpointVolumeChangedEventArgs>? VolumeChanged;

    public AudioSnapshot GetSnapshot()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var defaults = new Dictionary<(DeviceDirection, AudioEndpointRole), string?>();
        foreach (var direction in new[] { DeviceDirection.Output, DeviceDirection.Input })
        {
            foreach (var role in Enum.GetValues<AudioEndpointRole>())
            {
                defaults[(direction, role)] = TryGetDefaultEndpointId(direction, role);
            }
        }

        var outputs = Enumerate(DeviceDirection.Output, defaults);
        var inputs = Enumerate(DeviceDirection.Input, defaults);
        return new AudioSnapshot(outputs, inputs, defaults);
    }

    public string? TryGetDefaultEndpointId(DeviceDirection direction, AudioEndpointRole role)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        IMMDevice? device = null;
        try
        {
            var result = _enumerator.GetDefaultAudioEndpoint(ToFlow(direction), ToRole(role), out device!);
            if (result < 0 || device is null)
            {
                return null;
            }

            ComResult.ThrowIfFailed(device.GetId(out var id));
            return id;
        }
        finally
        {
            ComResult.Release(device);
        }
    }

    public bool IsEndpointActive(string endpointId, DeviceDirection direction)
    {
        return Enumerate(direction, new Dictionary<(DeviceDirection, AudioEndpointRole), string?>())
            .Any(device => device.Id == endpointId && device.IsAvailable);
    }

    public void SetDefaultEndpoint(string endpointId, AudioEndpointRole role)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var policy = (IPolicyConfig)Activator.CreateInstance(
            Type.GetTypeFromCLSID(CoreAudioIds.PolicyConfigClient, throwOnError: true)!)!;
        try
        {
            ComResult.ThrowIfFailed(policy.SetDefaultEndpoint(endpointId, ToRole(role)));
        }
        finally
        {
            ComResult.Release(policy);
        }
    }

    public double GetVolume(string endpointId)
    {
        var volume = GetVolumeInterface(endpointId);
        try
        {
            ComResult.ThrowIfFailed(volume.GetMasterVolumeLevelScalar(out var level));
            return Math.Clamp(level, 0, 1);
        }
        catch (COMException) { ReleaseVolume(); throw; }
    }

    public void SetVolume(string endpointId, double value)
    {
        var volume = GetVolumeInterface(endpointId);
        var context = _volumeEventContext;
        try { ComResult.ThrowIfFailed(volume.SetMasterVolumeLevelScalar((float)Math.Clamp(value, 0, 1), ref context)); }
        catch (COMException) { ReleaseVolume(); throw; }
    }

    public bool GetMute(string endpointId)
    {
        try
        {
            ComResult.ThrowIfFailed(GetVolumeInterface(endpointId).GetMute(out var muted));
            return muted;
        }
        catch (COMException) { ReleaseVolume(); throw; }
    }
    public void SetMute(string endpointId, bool muted)
    {
        var context = _volumeEventContext;
        try { ComResult.ThrowIfFailed(GetVolumeInterface(endpointId).SetMute(muted, ref context)); }
        catch (COMException) { ReleaseVolume(); throw; }
    }
    public void ObserveVolume(string? endpointId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (endpointId is null) { ReleaseVolume(); return; }
        _ = GetVolumeInterface(endpointId);
    }
    private IAudioEndpointVolume GetVolumeInterface(string endpointId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_volume is not null && _volumeEndpointId == endpointId) return _volume;
        ReleaseVolume();
        var volume = Activate<IAudioEndpointVolume>(endpointId, CoreAudioIds.AudioEndpointVolume);
        var callback = new EndpointVolumeCallback(data =>
        {
            if (!_disposed && data.EventContext != _volumeEventContext)
                VolumeChanged?.Invoke(this, new(endpointId, Math.Clamp(data.MasterVolume, 0, 1), data.Muted));
        });
        try { ComResult.ThrowIfFailed(volume.RegisterControlChangeNotify(callback)); }
        catch { ComResult.Release(volume); throw; }
        _volume = volume; _volumeCallback = callback; _volumeEndpointId = endpointId;
        return volume;
    }
    private void ReleaseVolume()
    {
        if (_volume is not null)
        {
            try { if (_volumeCallback is not null) _volume.UnregisterControlChangeNotify(_volumeCallback); }
            catch (COMException) { }
            ComResult.Release(_volume);
        }
        _volume = null; _volumeCallback = null; _volumeEndpointId = null;
    }

    public double GetPeakValue(string endpointId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_meter is null || _meterEndpointId != endpointId)
        {
            ReleaseMeter();
            _meter = Activate<IAudioMeterInformation>(endpointId, CoreAudioIds.AudioMeterInformation);
            _meterEndpointId = endpointId;
            StartCaptureStream(endpointId);
        }
        try
        {
            DrainCaptureStream();
            ComResult.ThrowIfFailed(_meter.GetPeakValue(out var peak));
            return Math.Clamp(peak, 0, 1);
        }
        catch
        {
            ReleaseMeter();
            throw;
        }
    }

    public void ReleaseMeter()
    {
        ComResult.Release(_meter);
        _meter = null; _meterEndpointId = null;
        ReleaseCaptureStream();
    }

    /// <summary>
    /// A capture endpoint's meter reads 0 unless some client is recording from it,
    /// so keep a shared-mode stream open while the meter is in use. Failure (e.g. microphone
    /// privacy blocked) is not fatal: the meter then only moves when another app records.
    /// </summary>
    private void StartCaptureStream(string endpointId)
    {
        IAudioClient? client = null;
        var format = IntPtr.Zero;
        try
        {
            client = Activate<IAudioClient>(endpointId, CoreAudioIds.AudioClient);
            ComResult.ThrowIfFailed(client.GetMixFormat(out format));
            ComResult.ThrowIfFailed(client.Initialize(0, 0, 10_000_000, 0, format, IntPtr.Zero)); // shared mode, 1 s buffer
            var serviceId = CoreAudioIds.AudioCaptureClient;
            ComResult.ThrowIfFailed(client.GetService(ref serviceId, out var service));
            _captureClient = (IAudioCaptureClient)service;
            ComResult.ThrowIfFailed(client.Start());
            _captureAudioClient = client;
            client = null;
        }
        catch (Exception exception) when (exception is COMException or InvalidCastException or UnauthorizedAccessException)
        {
            ComResult.Release(_captureClient);
            _captureClient = null;
        }
        finally
        {
            if (format != IntPtr.Zero) Marshal.FreeCoTaskMem(format);
            ComResult.Release(client);
        }
    }

    private void DrainCaptureStream()
    {
        if (_captureClient is null) return;
        while (_captureClient.GetNextPacketSize(out var frames) >= 0 && frames > 0)
        {
            if (_captureClient.GetBuffer(out _, out var read, out _, out _, out _) < 0) return;
            _captureClient.ReleaseBuffer(read);
        }
    }

    private void ReleaseCaptureStream()
    {
        if (_captureAudioClient is not null)
        {
            try { _captureAudioClient.Stop(); }
            catch (COMException) { }
        }
        ComResult.Release(_captureClient);
        ComResult.Release(_captureAudioClient);
        _captureClient = null; _captureAudioClient = null;
    }

    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.None)]
    private sealed class EndpointVolumeCallback(Action<AudioVolumeNotification> notify) : IAudioEndpointVolumeCallback
    {
        public void OnNotify(IntPtr notificationData)
        {
            if (notificationData == IntPtr.Zero) return;
            notify(Marshal.PtrToStructure<AudioVolumeNotification>(notificationData));
        }
    }

    private IReadOnlyList<DeviceInfo> Enumerate(
        DeviceDirection direction,
        IReadOnlyDictionary<(DeviceDirection, AudioEndpointRole), string?> defaults)
    {
        IMMDeviceCollection? collection = null;
        var result = new List<DeviceInfo>();
        try
        {
            ComResult.ThrowIfFailed(_enumerator.EnumAudioEndpoints(
                ToFlow(direction), CoreAudioDeviceState.Active, out collection!));
            ComResult.ThrowIfFailed(collection.GetCount(out var count));

            for (uint index = 0; index < count; index++)
            {
                IMMDevice? device = null;
                try
                {
                    ComResult.ThrowIfFailed(collection.Item(index, out device!));
                    ComResult.ThrowIfFailed(device.GetId(out var id));
                    ComResult.ThrowIfFailed(device.GetState(out var state));
                    var name = GetFriendlyName(device) ?? id;
                    result.Add(new DeviceInfo(id, name, direction, ToModelState(state))
                    {
                        IsDefault =
                            defaults.GetValueOrDefault((direction, AudioEndpointRole.Console)) == id ||
                            defaults.GetValueOrDefault((direction, AudioEndpointRole.Multimedia)) == id,
                        IsDefaultCommunications =
                            defaults.GetValueOrDefault((direction, AudioEndpointRole.Communications)) == id,
                        IsVirtual =
                            name.Contains("ASIO", StringComparison.OrdinalIgnoreCase) ||
                            name.StartsWith("Mix ", StringComparison.OrdinalIgnoreCase)
                    });
                }
                finally
                {
                    ComResult.Release(device);
                }
            }
        }
        finally
        {
            ComResult.Release(collection);
        }

        return result.OrderByDescending(device => device.IsDefault)
            .ThenByDescending(device => device.IsDefaultCommunications)
            .ThenBy(device => device.IsVirtual)
            .ThenBy(device => device.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    private T Activate<T>(string endpointId, Guid interfaceId) where T : class
    {
        IMMDevice? device = null;
        try
        {
            ComResult.ThrowIfFailed(_enumerator.GetDevice(endpointId, out device!));
            ComResult.ThrowIfFailed(device.Activate(
                ref interfaceId, CoreAudioIds.ClassContextAll, IntPtr.Zero, out var instance));
            return (T)instance;
        }
        finally
        {
            ComResult.Release(device);
        }
    }

    private static string? GetFriendlyName(IMMDevice device)
    {
        IPropertyStore? store = null;
        var value = default(PropVariant);
        try
        {
            ComResult.ThrowIfFailed(device.OpenPropertyStore(CoreAudioIds.StorageRead, out store!));
            var key = CoreAudioIds.FriendlyName;
            ComResult.ThrowIfFailed(store.GetValue(ref key, out value));
            return value.GetString();
        }
        finally
        {
            value.Clear();
            ComResult.Release(store);
        }
    }

    private static EDataFlow ToFlow(DeviceDirection direction) => direction switch
    {
        DeviceDirection.Output => EDataFlow.Render,
        DeviceDirection.Input => EDataFlow.Capture,
        _ => throw new ArgumentOutOfRangeException(nameof(direction), "摄像头不是 Core Audio 端点")
    };

    private static ERole ToRole(AudioEndpointRole role) => role switch
    {
        AudioEndpointRole.Console => ERole.Console,
        AudioEndpointRole.Multimedia => ERole.Multimedia,
        AudioEndpointRole.Communications => ERole.Communications,
        _ => throw new ArgumentOutOfRangeException(nameof(role))
    };

    private static DeviceState ToModelState(CoreAudioDeviceState state) => state switch
    {
        CoreAudioDeviceState.Active => DeviceState.Active,
        CoreAudioDeviceState.Disabled => DeviceState.Disabled,
        CoreAudioDeviceState.NotPresent => DeviceState.NotPresent,
        CoreAudioDeviceState.Unplugged => DeviceState.Unplugged,
        _ => DeviceState.Unknown
    };

    void IMMNotificationClient.OnDeviceStateChanged(string deviceId, CoreAudioDeviceState newState) =>
        RaiseDevicesChanged();

    void IMMNotificationClient.OnDeviceAdded(string deviceId) => RaiseDevicesChanged();

    void IMMNotificationClient.OnDeviceRemoved(string deviceId) => RaiseDevicesChanged();

    void IMMNotificationClient.OnDefaultDeviceChanged(EDataFlow flow, ERole role, string? defaultDeviceId) =>
        RaiseDevicesChanged();

    void IMMNotificationClient.OnPropertyValueChanged(string deviceId, PropertyKey key) =>
        RaiseDevicesChanged();

    private void RaiseDevicesChanged()
    {
        if (!_disposed)
        {
            DevicesChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        ReleaseMeter();
        ReleaseVolume();
        try
        {
            _enumerator.UnregisterEndpointNotificationCallback(this);
        }
        catch (COMException)
        {
            // Windows may already be shutting the audio service down.
        }

        ComResult.Release(_enumerator);
    }
}

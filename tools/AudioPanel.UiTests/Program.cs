using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using AudioPanel;
using AudioPanel.Models;
using AudioPanel.Services;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        var app = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("/AudioPanel;component/Resources/Styles.xaml", UriKind.Relative)
        });
        var dispatcher = Dispatcher.CurrentDispatcher;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
        var run = RunAsync();
        _ = run.ContinueWith(_ => dispatcher.BeginInvoke(new Action(() =>
        {
            app.Shutdown();
            dispatcher.InvokeShutdown();
        })), TaskScheduler.Default);
        Dispatcher.Run();
        return run.GetAwaiter().GetResult();
    }

    private static async Task<int> RunAsync()
    {
        var tests = new (string Name, Func<Task> Test)[]
        {
            ("scene selection preserves independent communication devices and saved data", SceneSelectionPreservesData),
            ("initialization blocks overlapping refresh and edits", InitializationBlocksOverlap),
            ("refresh preserves offline selections without writing configuration", RefreshPreservesOfflineData),
            ("user edits persist only the selected scene without switching audio", UserEditsPersist),
            ("incomplete and offline scenes are explicitly unavailable", UnavailableScenes),
            ("failure feedback survives manual and device-change refresh", FailureFeedbackSurvivesRefresh),
            ("external volume and mute updates do not write back", VolumeUpdatesDoNotWriteBack),
            ("user volume changes write once", UserVolumeChange),
            ("input metering pauses while hidden or minimized", MeterVisibility),
            ("scene matching checks communications as well as normal roles", SceneMatchingChecksAllRoles),
            ("scenes with identical devices light only the last applied one", IdenticalScenesLightOnce),
            ("scene rename trims, rejects blanks and persists", SceneRenamePersists),
            ("first-run detection does not mistake onboard audio for a monitor", DetectorOnOnboardAudioMachine),
            ("first-run detection pairs a dedicated card and webcam microphone", DetectorOnDedicatedCardMachine),
            ("semantic configuration damage is backed up", DamagedConfiguration),
            ("single instance activation uses the isolated current-user pipe", InstanceActivation)
        };
        var failures = 0;
        foreach (var (name, test) in tests)
        {
            try { await test(); Console.WriteLine($"PASS {name}"); }
            catch (Exception exception) { failures++; Console.WriteLine($"FAIL {name}: {exception}"); }
        }
        return failures == 0 ? 0 : 1;
    }

    private static async Task SceneSelectionPreservesData()
    {
        await using var ctx = await Context.CreateAsync();
        var before = await File.ReadAllTextAsync(ctx.ConfigPath);
        var split = ctx.Scene("split");
        ctx.Window.ViewModel.SelectScene(split);
        await Idle();
        Require(split.Output.CommunicationsEndpointId == "B-out" && split.Input.CommunicationsEndpointId == "B-in", "通信设备被场景载入覆盖");
        Require(!split.Output.FollowNormalForCommunications && !split.Input.FollowNormalForCommunications, "跟随设置被载入改写");
        ctx.Window.ViewModel.SelectScene(ctx.Scene("mixed"));
        await Idle();
        Require(ctx.Scene("mixed").Input.FollowNormalForCommunications, "输入的独立跟随设置被统一复选框改写");
        Require(await File.ReadAllTextAsync(ctx.ConfigPath) == before, "单纯选择场景触发了配置保存");
        Require(ctx.Audio.SetCount == 0 && ctx.Audio.VolumeWrites == 0, "载入场景触发了系统写入");
    }

    private static async Task RefreshPreservesOfflineData()
    {
        await using var ctx = await Context.CreateAsync();
        ctx.Window.ViewModel.SelectScene(ctx.Scene("offline"));
        var before = await File.ReadAllTextAsync(ctx.ConfigPath);
        ctx.Window.RequestRefresh();
        await Idle();
        Require(ctx.Scene("offline").Output.NormalEndpointId == "OFFLINE-out", "离线端点记录被清空");
        Require(ctx.Window.ViewModel.SelectedScene?.Id == "offline", "刷新改变了编辑场景");
        Require(await File.ReadAllTextAsync(ctx.ConfigPath) == before, "刷新改写配置");
    }

    private static async Task InitializationBlocksOverlap()
    {
        var directory = NewDirectory();
        var configService = new ConfigurationService(Path.Combine(directory, "config.json"));
        await configService.SaveAsync(new AudioPanelConfiguration { Scenes = [Scene("follow", "A")] });
        var window = new MainWindow();
        var audio = new FakeAudio();
        var ready = new TaskCompletionSource();
        using var controller = new AppController(window, configService, audio, new DelayedCameras(ready.Task),
            new OperationLogService(Path.Combine(directory, "log.txt")));
        var initialization = controller.InitializeAsync();
        await Idle();
        Require(window.ViewModel.IsInitializing && !window.ViewModel.CanEdit && !window.ViewModel.CanRefresh, "初始化期间仍可编辑或刷新");
        window.RequestRefresh();
        ready.TrySetResult();
        Require(await initialization, "初始化没有完成");
        await Idle();
        Require(audio.SnapshotReads == 1, "初始化期间的刷新重复枚举并载入场景");
        await controller.PrepareForExitAsync();
        window.Close();
    }

    private static async Task UserEditsPersist()
    {
        await using var ctx = await Context.CreateAsync();
        ctx.Window.ViewModel.SelectScene(ctx.Scene("missing"));
        var inputCombo = (ComboBox)ctx.Window.FindName("InputCombo");
        inputCombo.SelectedItem = ctx.Window.ViewModel.InputDevices.Single(device => device.Id == "B-in");
        await ctx.Controller.PrepareForExitAsync();
        var saved = await new ConfigurationService(ctx.ConfigPath).LoadAsync();
        Require(saved.Scenes.Single(scene => scene.Id == "missing").Input.NormalEndpointId == "B-in", "用户编辑没有保存");
        Require(saved.Scenes.Single(scene => scene.Id == "split").Input.CommunicationsEndpointId == "B-in", "编辑改变了其他场景");
        Require(ctx.Audio.SetCount == 0 && ctx.Audio.VolumeWrites == 0, "编辑场景提前切换了系统设备");
        Require(!File.ReadAllText(ctx.ConfigPath).Contains("IsSelected", StringComparison.Ordinal), "界面状态写入了配置");
    }

    private static async Task UnavailableScenes()
    {
        await using var ctx = await Context.CreateAsync();
        ctx.Window.ViewModel.SelectScene(ctx.Scene("missing"));
        Require(ctx.Window.ViewModel.SelectedSceneStateText.Contains("待配置输入"), "未提示缺少输入");
        Require(!ctx.Window.ViewModel.CanApplySelectedScene && !ctx.Window.RequestSceneApply("missing"), "未配置场景可以应用");
        ctx.Window.ViewModel.SelectScene(ctx.Scene("offline"));
        Require(ctx.Window.ViewModel.SelectedSceneStateText.Contains("离线") && !ctx.Window.ViewModel.CanApplySelectedScene, "离线设备没有阻止应用");
        Require(ctx.Audio.SetCount == 0, "不可用场景触发了写入");
    }

    private static async Task FailureFeedbackSurvivesRefresh()
    {
        await using var ctx = await Context.CreateAsync();
        ctx.Audio.FailOnSet = 3;
        var complete = new TaskCompletionSource<SceneApplyResult>();
        ctx.Controller.SceneApplyCompleted += (_, e) => complete.TrySetResult(e.Result);
        Require(ctx.Window.RequestSceneApply("split"), "有效场景请求被拒绝");
        var result = await complete.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Require(result.Status == SceneApplyStatus.Failed, "模拟失败未生效");
        var failureText = ctx.Window.ViewModel.StatusText;
        ctx.Window.RequestRefresh();
        ctx.Audio.NotifyDevicesChanged();
        await Task.Delay(450);
        Require(ctx.Window.ViewModel.StatusText == failureText && ctx.Window.ViewModel.StatusKind == PanelStatusKind.Error, "刷新覆盖了失败反馈");
        Require(ctx.Audio.Defaults.All(pair => pair.Value == Id("A", pair.Key.Item1)), "失败后默认角色未恢复");
    }

    private static async Task VolumeUpdatesDoNotWriteBack()
    {
        await using var ctx = await Context.CreateAsync();
        await Task.Run(() => ctx.Audio.NotifyVolume("A-out", 0.23, true));
        await Idle();
        Require(Math.Abs(ctx.Window.ViewModel.Volume - 0.23) < 0.001 && ctx.Window.ViewModel.IsMuted, "外部音量/静音没有同步");
        ctx.Audio.NotifyVolume("B-out", 0.91, false);
        await Idle();
        Require(Math.Abs(ctx.Window.ViewModel.Volume - 0.23) < 0.001, "旧设备通知覆盖了当前音量");
        Require(ctx.Audio.VolumeWrites == 0 && ctx.Audio.MuteWrites == 0, "音量通知引发回写循环");
    }

    private static async Task MeterVisibility()
    {
        await using var ctx = await Context.CreateAsync();
        ctx.Window.ShowActivated = false;
        ctx.Window.Left = -10000; ctx.Window.Top = -10000;
        ctx.Window.Show();
        await Task.Delay(350);
        Require(ctx.Audio.PeakReads > 0, "面板显示时没有电平采样");
        ctx.Window.Hide();
        var hiddenReads = ctx.Audio.PeakReads;
        await Task.Delay(350);
        Require(ctx.Audio.PeakReads == hiddenReads, "隐藏面板后仍然采样");
        ctx.Window.Show();
        await Task.Delay(250);
        Require(ctx.Audio.PeakReads > hiddenReads, "恢复显示后采样未恢复");
        ctx.Window.WindowState = WindowState.Minimized;
        var minimizedReads = ctx.Audio.PeakReads;
        await Task.Delay(350);
        Require(ctx.Audio.PeakReads == minimizedReads, "最小化后仍然采样");
    }

    private static async Task UserVolumeChange()
    {
        await using var ctx = await Context.CreateAsync();
        ((Slider)ctx.Window.FindName("VolumeSlider")).Value = 0.7;
        await Idle();
        Require(ctx.Audio.VolumeWrites == 1, "用户音量变化被忽略或重复写入");
    }

    private static async Task SceneMatchingChecksAllRoles()
    {
        await using var ctx = await Context.CreateAsync();
        ctx.Window.ViewModel.SelectScene(ctx.Scene("split"));
        Require(ctx.Scene("follow").IsApplied && !ctx.Scene("split").IsApplied && ctx.Scene("split").IsSelected, "当前场景和编辑场景混淆");
        ctx.Audio.Defaults[(DeviceDirection.Output, AudioEndpointRole.Communications)] = "B-out";
        ctx.Audio.NotifyDevicesChanged();
        await Task.Delay(450);
        Require(!ctx.Scene("follow").IsApplied, "通信角色变化未影响场景匹配标记");
    }

    private static async Task IdenticalScenesLightOnce()
    {
        await using var ctx = await Context.CreateAsync();
        var vm = ctx.Window.ViewModel;
        vm.ReplaceScenes([Program.Scene("follow", "A"), Program.Scene("twin", "A")], "follow");
        Require(vm.Scenes.Count(scene => scene.IsApplied) == 1 && ctx.Scene("follow").IsApplied, "设备相同的场景同时点亮");
        vm.LastAppliedSceneId = "twin"; vm.RefreshSceneStates();
        Require(ctx.Scene("twin").IsApplied && !ctx.Scene("follow").IsApplied, "未优先点亮最后切换的场景");
        Require(vm.AppliedSceneText == "正在使用：twin", "标题仍列出多个场景");
    }

    private static async Task SceneRenamePersists()
    {
        await using var ctx = await Context.CreateAsync();
        ctx.Window.ViewModel.SelectScene(ctx.Scene("split"));
        var box = (TextBox)ctx.Window.FindName("SceneNameBox");
        box.Text = "  耳机  "; ctx.Window.CommitSceneName();
        Require(ctx.Scene("split").Name == "耳机" && box.Text == "耳机", "改名未生效或未去掉空格");
        box.Text = "   "; ctx.Window.CommitSceneName();
        Require(ctx.Scene("split").Name == "耳机" && box.Text == "耳机", "空名称被保存");
        await ctx.Controller.PrepareForExitAsync();
        var saved = await new ConfigurationService(ctx.ConfigPath).LoadAsync();
        Require(saved.Scenes.Single(scene => scene.Id == "split").Name == "耳机", "改名没有保存");
        Require(ctx.Audio.SetCount == 0, "改名切换了系统设备");
    }

    private static Task DetectorOnOnboardAudioMachine()
    {
        // Typical laptop/desktop: Realtek onboard audio, NVIDIA HDMI, a Logitech webcam, a Bluetooth headset.
        var outputs = new[]
        {
            Device("realtek-out", "扬声器 (Realtek(R) Audio)", DeviceDirection.Output),
            Device("rhd-out", "Speakers (Realtek High Definition Audio)", DeviceDirection.Output),
            Device("nv-out", "DELL U2720Q (NVIDIA High Definition Audio)", DeviceDirection.Output),
            Device("bt-out", "耳机 (WH-1000XM4)", DeviceDirection.Output)
        };
        var inputs = new[]
        {
            Device("realtek-in", "麦克风阵列 (Realtek(R) Audio)", DeviceDirection.Input),
            Device("cam-in", "麦克风 (C920 HD Pro Webcam)", DeviceDirection.Input)
        };
        var config = AudioPanelConfiguration.CreateDefault();
        Require(DefaultSceneDetector.Apply(config, Snapshot(outputs, inputs, "realtek-out", "realtek-in"),
            [Device("cam", "C920 HD Pro Webcam", DeviceDirection.Camera)]), "首次识别没有执行");
        var scenes = config.Scenes.ToDictionary(scene => scene.Id);
        Require(scenes["monitor"].Output.NormalEndpointId == "nv-out", "没有识别出显示器音频");
        Require(scenes["monitor"].Input.NormalEndpointId == "realtek-in", "显示器场景没有使用默认输入");
        Require(scenes["independent"].Output.NormalEndpointId is null && scenes["independent"].Input.NormalEndpointId is null, "板载声卡被当成独立声卡");
        Require(scenes["meeting"].Input.NormalEndpointId == "cam-in" && scenes["meeting"].Output.NormalEndpointId == "realtek-out", "会议场景识别错误");
        Require(scenes["meeting"].Camera.DeviceId == "cam", "会议场景没有记录摄像头");
        Require(scenes["fallback"].Output.NormalEndpointId == "realtek-out" && scenes["fallback"].Input.NormalEndpointId == "realtek-in", "备用场景没有用默认设备");
        Require(!DefaultSceneDetector.Apply(config, Snapshot(outputs, inputs, "realtek-out", "realtek-in"), []), "已配置的场景被重新识别");
        return Task.CompletedTask;
    }

    private static Task DetectorOnDedicatedCardMachine()
    {
        var outputs = new[]
        {
            Device("hdmi-out", "AMD HDMI Output (AMD High Definition Audio Device)", DeviceDirection.Output),
            Device("sb-out", "扬声器 (Sound Blaster Audigy 5/Rx)", DeviceDirection.Output),
            Device("asio-out", "Speakers 01 (ASIOVADPRO Driver)", DeviceDirection.Output, isVirtual: true)
        };
        var inputs = new[]
        {
            Device("sb-line", "线性输入 (Sound Blaster Audigy 5/Rx)", DeviceDirection.Input),
            Device("sb-mic", "麦克风 (Sound Blaster Audigy 5/Rx)", DeviceDirection.Input),
            Device("emeet-in", "麦克风 (EMEET SmartCam C60E 4K)", DeviceDirection.Input)
        };
        var config = AudioPanelConfiguration.CreateDefault();
        DefaultSceneDetector.Apply(config, Snapshot(outputs, inputs, "asio-out", "sb-mic"),
            [Device("emeet", "EMEET SmartCam C60E 4K", DeviceDirection.Camera)]);
        var scenes = config.Scenes.ToDictionary(scene => scene.Id);
        Require(scenes["independent"].Output.NormalEndpointId == "sb-out" && scenes["independent"].Input.NormalEndpointId == "sb-mic", "独立声卡识别错误");
        Require(scenes["monitor"].Output.NormalEndpointId == "hdmi-out" && scenes["monitor"].Input.NormalEndpointId == "sb-mic", "显示器场景识别错误");
        Require(scenes["meeting"].Output.NormalEndpointId == "sb-out" && scenes["meeting"].Input.NormalEndpointId == "emeet-in", "会议场景识别错误");
        Require(DefaultSceneDetector.DevicePart("扬声器 (Realtek(R) Audio)") == "Realtek(R) Audio", "设备名解析错误");
        return Task.CompletedTask;
    }

    private static DeviceInfo Device(string id, string name, DeviceDirection direction, bool isVirtual = false) =>
        new(id, name, direction, DeviceState.Active) { IsVirtual = isVirtual };
    private static AudioSnapshot Snapshot(DeviceInfo[] outputs, DeviceInfo[] inputs, string defaultOutput, string defaultInput) =>
        new(outputs, inputs, (from direction in new[] { DeviceDirection.Output, DeviceDirection.Input }
            from role in Enum.GetValues<AudioEndpointRole>() select (direction, role))
            .ToDictionary(key => key, key => (string?)(key.direction == DeviceDirection.Output ? defaultOutput : defaultInput)));

    private static async Task DamagedConfiguration()
    {
        var directory = NewDirectory();
        var path = Path.Combine(directory, "config.json");
        await File.WriteAllTextAsync(path, "{\"Scenes\":[{\"Id\":\"broken\",\"Output\":null}]}");
        var config = await new ConfigurationService(path).LoadAsync();
        Require(config.Scenes.All(scene => scene.Output is not null), "损坏结构没有恢复默认值");
        Require(Directory.GetFiles(directory, "config.json.bad-*.json").Length == 1, "损坏配置没有备份");
    }

    private static async Task InstanceActivation()
    {
        using var instance = new SingleInstanceService(Guid.NewGuid().ToString("N"));
        Require(instance.IsPrimary, "隔离测试锁不是主实例");
        var received = new TaskCompletionSource();
        instance.StartListening(() => received.TrySetResult());
        Require(await instance.ActivateExistingAsync(), "激活请求未送达");
        await received.Task.WaitAsync(TimeSpan.FromSeconds(3));
    }

    private static async Task Idle() => await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static string Id(string prefix, DeviceDirection direction) => $"{prefix}-{(direction == DeviceDirection.Output ? "out" : "in")}";
    private static string NewDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "AudioPanel.UiTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path); return path;
    }
    private static AudioScene Scene(string id, string normal, bool follow = true) => new()
    {
        Id = id, Name = id,
        Output = new() { NormalEndpointId = Id(normal, DeviceDirection.Output), CommunicationsEndpointId = "B-out", FollowNormalForCommunications = follow },
        Input = new() { NormalEndpointId = Id(normal, DeviceDirection.Input), CommunicationsEndpointId = "B-in", FollowNormalForCommunications = follow }
    };

    private sealed class Context : IAsyncDisposable
    {
        public required MainWindow Window { get; init; }
        public required AppController Controller { get; init; }
        public required FakeAudio Audio { get; init; }
        public required string ConfigPath { get; init; }
        public AudioScene Scene(string id) => Window.ViewModel.Scenes.Single(scene => scene.Id == id);
        public static async Task<Context> CreateAsync()
        {
            var directory = NewDirectory();
            var configPath = Path.Combine(directory, "config.json");
            var configService = new ConfigurationService(configPath);
            var mixed = Program.Scene("mixed", "C", false); mixed.Input.FollowNormalForCommunications = true;
            var missing = Program.Scene("missing", "A"); missing.Input.NormalEndpointId = null;
            var config = new AudioPanelConfiguration { LastSceneId = "follow", Scenes =
                [Program.Scene("follow", "A"), Program.Scene("split", "C", false), mixed, missing, Program.Scene("offline", "OFFLINE")] };
            await configService.SaveAsync(config);
            var window = new MainWindow();
            var audio = new FakeAudio();
            var controller = new AppController(window, configService, audio, new FakeCameras(), new OperationLogService(Path.Combine(directory, "log.txt")));
            Require(await controller.InitializeAsync(), "界面初始化失败");
            window.Measure(new Size(460, 740)); window.Arrange(new Rect(0, 0, 460, 740)); window.UpdateLayout();
            await Idle();
            return new() { Window = window, Controller = controller, Audio = audio, ConfigPath = configPath };
        }
        public async ValueTask DisposeAsync()
        {
            await Controller.PrepareForExitAsync(); Controller.Dispose(); Window.Close();
        }
    }
    private sealed class FakeCameras : ICameraDeviceSource
    {
        public Task<IReadOnlyList<DeviceInfo>> GetCamerasAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<DeviceInfo>>([]);
    }
    private sealed class DelayedCameras(Task ready) : ICameraDeviceSource
    {
        public async Task<IReadOnlyList<DeviceInfo>> GetCamerasAsync(CancellationToken cancellationToken = default)
        {
            await ready.WaitAsync(cancellationToken); return [];
        }
    }
    private sealed class FakeAudio : IAudioDeviceService
    {
        public Dictionary<(DeviceDirection, AudioEndpointRole), string?> Defaults { get; } = (
            from direction in new[] { DeviceDirection.Output, DeviceDirection.Input }
            from role in Enum.GetValues<AudioEndpointRole>() select (direction, role)).ToDictionary(key => key, key => (string?)Id("A", key.direction));
        public int SetCount { get; private set; }
        public int FailOnSet { get; set; }
        public int PeakReads { get; private set; }
        public int VolumeWrites { get; private set; }
        public int MuteWrites { get; private set; }
        public int SnapshotReads { get; private set; }
        public event EventHandler? DevicesChanged;
        public event EventHandler<EndpointVolumeChangedEventArgs>? VolumeChanged;
        public AudioSnapshot GetSnapshot()
        {
            SnapshotReads++;
            return new(Devices(DeviceDirection.Output), Devices(DeviceDirection.Input), Defaults.ToDictionary(pair => pair.Key, pair => pair.Value));
        }
        private static DeviceInfo[] Devices(DeviceDirection direction) => new[] { "A", "B", "C" }.Select(prefix => new DeviceInfo(Id(prefix, direction), Id(prefix, direction), direction, DeviceState.Active)).ToArray();
        public string? TryGetDefaultEndpointId(DeviceDirection direction, AudioEndpointRole role) => Defaults[(direction, role)];
        public bool IsEndpointActive(string endpointId, DeviceDirection direction) => Devices(direction).Any(device => device.Id == endpointId);
        public void SetDefaultEndpoint(string endpointId, AudioEndpointRole role)
        {
            if (++SetCount == FailOnSet) throw new InvalidOperationException("模拟第三步写入失败");
            Defaults[(endpointId.EndsWith("-out") ? DeviceDirection.Output : DeviceDirection.Input, role)] = endpointId;
        }
        public void ObserveVolume(string? endpointId) { }
        public double GetVolume(string endpointId) => 0.5;
        public bool GetMute(string endpointId) => false;
        public void SetVolume(string endpointId, double value) => VolumeWrites++;
        public void SetMute(string endpointId, bool muted) => MuteWrites++;
        public double GetPeakValue(string endpointId) { PeakReads++; return 0.3; }
        public void ReleaseMeter() { }
        public void NotifyDevicesChanged() => DevicesChanged?.Invoke(this, EventArgs.Empty);
        public void NotifyVolume(string id, double volume, bool muted) => VolumeChanged?.Invoke(this, new(id, volume, muted));
        public void Dispose() { }
    }
}

using AudioPanel.Models;
using AudioPanel.Services;

var tests = new (string Name, Action Test)[]
{
    ("all roles succeed", AllRolesSucceed),
    ("communication roles can differ", CommunicationRolesCanDiffer),
    ("mid-flight failure rolls back", FailureRollsBack),
    ("external change is not overwritten", ConflictDoesNotOverwrite),
    ("verification failure rolls back the attempted role", VerificationFailureRollsBack),
    ("write then throw is safely rolled back", WriteThenThrowRollsBack),
    ("final verification detects an earlier external change", FinalVerificationDetectsChange),
    ("missing devices cause no writes", MissingDevicesDoNotWrite),
    ("offline devices cause no writes", OfflineDevicesDoNotWrite),
    ("rollback preserves an external change to an attempted role", RollbackPreservesExternalChange)
};

var failures = new List<string>();
foreach (var (name, test) in tests)
{
    try
    {
        test();
        Console.WriteLine($"PASS {name}");
    }
    catch (Exception exception)
    {
        failures.Add($"{name}: {exception.Message}");
        Console.WriteLine($"FAIL {name}: {exception.Message}");
    }
}

return failures.Count == 0 ? 0 : 1;

void VerificationFailureRollsBack()
{
    var fake = new FakeAudioController("A") { StaleReadKey = (DeviceDirection.Output, AudioEndpointRole.Console) };
    var result = new SceneApplyService(fake).ApplyAsync(Scene("B")).GetAwaiter().GetResult();
    Require(result.Status == SceneApplyStatus.Failed, result.Message);
    Require(fake.Defaults.All(pair => pair.Value == Expected("A", pair.Key.Item1)), "核验失败的写入没有被恢复");
    Require(result.Roles.Any(role => !role.Verified), "结果中缺少失败的角色");
}

void WriteThenThrowRollsBack()
{
    var fake = new FakeAudioController("A") { AfterSet = (count, _) => { if (count == 1) throw new InvalidOperationException("写入后发生错误"); } };
    var result = new SceneApplyService(fake).ApplyAsync(Scene("B")).GetAwaiter().GetResult();
    Require(result.Status == SceneApplyStatus.Failed, result.Message);
    Require(fake.Defaults.All(pair => pair.Value == Expected("A", pair.Key.Item1)), "写入后抛错的角色没有被恢复");
}

void FinalVerificationDetectsChange()
{
    var fake = new FakeAudioController("A") { AfterSet = (count, audio) =>
    {
        if (count == 6) audio.Defaults[(DeviceDirection.Output, AudioEndpointRole.Console)] = "EXTERNAL";
    } };
    var result = new SceneApplyService(fake).ApplyAsync(Scene("B")).GetAwaiter().GetResult();
    Require(result.Status == SceneApplyStatus.Conflict, "最终核验漏掉较早角色的外部变化");
    Require(fake.Defaults[(DeviceDirection.Output, AudioEndpointRole.Console)] == "EXTERNAL", "最终核验后的回滚覆盖了外部修改");
}

void MissingDevicesDoNotWrite()
{
    var fake = new FakeAudioController("A");
    var scene = Scene("B"); scene.Input.NormalEndpointId = null;
    var result = new SceneApplyService(fake).ApplyAsync(scene).GetAwaiter().GetResult();
    Require(result.Status == SceneApplyStatus.Failed && fake.SetCount == 0, "配置不完整时发生了写入");
}

void OfflineDevicesDoNotWrite()
{
    var fake = new FakeAudioController("A") { UnavailableId = "B-in" };
    var result = new SceneApplyService(fake).ApplyAsync(Scene("B")).GetAwaiter().GetResult();
    Require(result.Status == SceneApplyStatus.Failed && fake.SetCount == 0, "设备离线时发生了写入");
}

void RollbackPreservesExternalChange()
{
    var fake = new FakeAudioController("A") { AfterSet = (count, audio) =>
    {
        if (count != 2) return;
        audio.Defaults[(DeviceDirection.Output, AudioEndpointRole.Console)] = "EXTERNAL";
        throw new InvalidOperationException("模拟中途失败");
    } };
    var result = new SceneApplyService(fake).ApplyAsync(Scene("B")).GetAwaiter().GetResult();
    Require(result.Status == SceneApplyStatus.Partial, "无法完整恢复时未返回部分失败");
    Require(fake.Defaults[(DeviceDirection.Output, AudioEndpointRole.Console)] == "EXTERNAL", "回滚覆盖了外部修改");
    Require(fake.Defaults[(DeviceDirection.Output, AudioEndpointRole.Multimedia)] == "A-out", "其余已写角色未恢复");
}

void AllRolesSucceed()
{
    var fake = new FakeAudioController("A");
    var result = new SceneApplyService(fake).ApplyAsync(Scene("B")).GetAwaiter().GetResult();
    Require(result.Status == SceneApplyStatus.Succeeded, result.Message);
    Require(
        fake.Defaults.All(pair => pair.Value == Expected("B", pair.Key.Item1)),
        "并非六个角色都切到对应的 B 端点");
}

void FailureRollsBack()
{
    var fake = new FakeAudioController("A") { FailOnSetNumber = 3 };
    var result = new SceneApplyService(fake).ApplyAsync(Scene("B")).GetAwaiter().GetResult();
    Require(result.Status == SceneApplyStatus.Failed, $"意外状态：{result.Status}");
    Require(
        fake.Defaults.All(pair => pair.Value == Expected("A", pair.Key.Item1)),
        "失败后没有完整恢复 A");
}

void CommunicationRolesCanDiffer()
{
    var fake = new FakeAudioController("A");
    var scene = Scene("B");
    scene.Output.FollowNormalForCommunications = false;
    scene.Output.CommunicationsEndpointId = "C-out";
    scene.Input.FollowNormalForCommunications = false;
    scene.Input.CommunicationsEndpointId = "C-in";

    var result = new SceneApplyService(fake).ApplyAsync(scene).GetAwaiter().GetResult();
    Require(result.Status == SceneApplyStatus.Succeeded, result.Message);
    Require(
        fake.Defaults[(DeviceDirection.Output, AudioEndpointRole.Communications)] == "C-out",
        "通信输出没有使用独立端点");
    Require(
        fake.Defaults[(DeviceDirection.Input, AudioEndpointRole.Communications)] == "C-in",
        "通信输入没有使用独立端点");
}

void ConflictDoesNotOverwrite()
{
    var fake = new FakeAudioController("A")
    {
        ConflictKey = (DeviceDirection.Output, AudioEndpointRole.Multimedia)
    };
    var result = new SceneApplyService(fake).ApplyAsync(Scene("B")).GetAwaiter().GetResult();
    Require(result.Status == SceneApplyStatus.Conflict, $"意外状态：{result.Status}");
    Require(
        fake.Defaults[(DeviceDirection.Output, AudioEndpointRole.Multimedia)] == "EXTERNAL",
        "场景回滚覆盖了外部修改");
    Require(
        fake.Defaults[(DeviceDirection.Output, AudioEndpointRole.Console)] == "A-out",
        "冲突前已修改的角色没有恢复");
}

AudioScene Scene(string endpointId) => new()
{
    Id = "test",
    Name = "测试",
    Output = new AudioEndpointSelection
    {
        NormalEndpointId = $"{endpointId}-out",
        CommunicationsEndpointId = $"{endpointId}-out",
        FollowNormalForCommunications = true
    },
    Input = new AudioEndpointSelection
    {
        NormalEndpointId = $"{endpointId}-in",
        CommunicationsEndpointId = $"{endpointId}-in",
        FollowNormalForCommunications = true
    }
};

string Expected(string prefix, DeviceDirection direction) =>
    $"{prefix}-{(direction == DeviceDirection.Output ? "out" : "in")}";

void Require(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

sealed class FakeAudioController : IAudioEndpointController
{
    private readonly Dictionary<(DeviceDirection, AudioEndpointRole), int> _reads = [];
    private int _setCount;

    public FakeAudioController(string initial)
    {
        Defaults = (
            from direction in new[] { DeviceDirection.Output, DeviceDirection.Input }
            from role in Enum.GetValues<AudioEndpointRole>()
            select (direction, role))
            .ToDictionary(
                key => key,
                key => $"{initial}-{(key.direction == DeviceDirection.Output ? "out" : "in")}");
    }

    public Dictionary<(DeviceDirection, AudioEndpointRole), string> Defaults { get; }
    public int? FailOnSetNumber { get; init; }
    public (DeviceDirection, AudioEndpointRole)? ConflictKey { get; init; }
    public (DeviceDirection, AudioEndpointRole)? StaleReadKey { get; init; }
    public Action<int, FakeAudioController>? AfterSet { get; init; }
    public string? UnavailableId { get; init; }
    public int SetCount => _setCount;

    public string? TryGetDefaultEndpointId(
        DeviceDirection direction,
        AudioEndpointRole role)
    {
        var key = (direction, role);
        _reads[key] = _reads.GetValueOrDefault(key) + 1;
        if (StaleReadKey == key && _reads[key] == 3) return ExpectedInitial(direction);
        if (ConflictKey == key && _reads[key] == 2)
        {
            Defaults[key] = "EXTERNAL";
        }

        return Defaults[key];
    }

    private static string ExpectedInitial(DeviceDirection direction) => direction == DeviceDirection.Output ? "A-out" : "A-in";
    public bool IsEndpointActive(string endpointId, DeviceDirection direction) => endpointId != UnavailableId;

    public void SetDefaultEndpoint(string endpointId, AudioEndpointRole role)
    {
        _setCount++;
        if (_setCount == FailOnSetNumber)
        {
            throw new InvalidOperationException("模拟第三步写入失败");
        }

        var direction = endpointId.EndsWith("-out", StringComparison.Ordinal)
            ? DeviceDirection.Output
            : DeviceDirection.Input;
        Defaults[(direction, role)] = endpointId;
        AfterSet?.Invoke(_setCount, this);
    }
}

using AudioPanel.Models;
using AudioPanel.Services;

var tests = new (string Name, Action Test)[]
{
    ("all roles succeed", AllRolesSucceed),
    ("communication roles can differ", CommunicationRolesCanDiffer),
    ("mid-flight failure rolls back", FailureRollsBack),
    ("external change is not overwritten", ConflictDoesNotOverwrite)
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

    public string? TryGetDefaultEndpointId(
        DeviceDirection direction,
        AudioEndpointRole role)
    {
        var key = (direction, role);
        _reads[key] = _reads.GetValueOrDefault(key) + 1;
        if (ConflictKey == key && _reads[key] == 2)
        {
            Defaults[key] = "EXTERNAL";
        }

        return Defaults[key];
    }

    public bool IsEndpointActive(string endpointId, DeviceDirection direction) => true;

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
    }
}

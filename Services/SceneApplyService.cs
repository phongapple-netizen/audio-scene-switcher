using AudioPanel.Models;

namespace AudioPanel.Services;

public enum SceneApplyStatus
{
    Succeeded,
    Failed,
    Partial,
    Conflict
}

public sealed record RoleApplyResult(
    DeviceDirection Direction,
    AudioEndpointRole Role,
    string? PreviousEndpointId,
    string TargetEndpointId,
    bool Changed,
    bool Verified,
    string Message);

public sealed class SceneApplyResult
{
    public required SceneApplyStatus Status { get; init; }
    public required string Message { get; init; }
    public required IReadOnlyList<RoleApplyResult> Roles { get; init; }
}

public sealed class SceneApplyService
{
    private readonly IAudioEndpointController _audio;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public SceneApplyService(IAudioEndpointController audio)
    {
        _audio = audio;
    }

    public async Task<SceneApplyResult> ApplyAsync(
        AudioScene scene,
        CancellationToken cancellationToken = default)
    {
        if (!await _gate.WaitAsync(0, cancellationToken))
        {
            return new SceneApplyResult
            {
                Status = SceneApplyStatus.Conflict,
                Message = "另一个场景切换仍在进行",
                Roles = []
            };
        }

        try
        {
            return ApplyCore(scene.Clone());
        }
        finally
        {
            _gate.Release();
        }
    }

    private SceneApplyResult ApplyCore(AudioScene scene)
    {
        var targets = BuildTargets(scene);
        var missing = targets.Where(target => string.IsNullOrWhiteSpace(target.EndpointId)).ToArray();
        if (missing.Length > 0)
        {
            return new SceneApplyResult
            {
                Status = SceneApplyStatus.Failed,
                Message = $"场景“{scene.Name}”尚未配置完整的输出和输入设备",
                Roles = []
            };
        }

        foreach (var target in targets.DistinctBy(target => (target.Direction, target.EndpointId)))
        {
            if (!_audio.IsEndpointActive(target.EndpointId!, target.Direction))
            {
                return new SceneApplyResult
                {
                    Status = SceneApplyStatus.Failed,
                    Message = $"目标端点当前不可用：{target.Direction} / {target.EndpointId}",
                    Roles = []
                };
            }
        }

        var before = targets.ToDictionary(
            target => (target.Direction, target.Role),
            target => _audio.TryGetDefaultEndpointId(target.Direction, target.Role));
        var results = new List<RoleApplyResult>();
        var changed = new List<(Target Target, string? Previous)>();

        try
        {
            foreach (var target in targets)
            {
                var key = (target.Direction, target.Role);
                var previous = before[key];
                var current = _audio.TryGetDefaultEndpointId(target.Direction, target.Role);

                if (current == target.EndpointId)
                {
                    results.Add(new RoleApplyResult(
                        target.Direction, target.Role, previous, target.EndpointId!,
                        Changed: false, Verified: true, "已是目标端点"));
                    continue;
                }

                if (current != previous)
                {
                    throw new SceneConflictException(
                        $"{target.Direction}/{target.Role} 在切换期间被其他程序修改");
                }

                // A write can take effect even when it throws or verification
                // fails. Track the attempt first; rollback checks its actual state.
                changed.Add((target, previous));
                results.Add(new RoleApplyResult(
                    target.Direction, target.Role, previous, target.EndpointId!,
                    Changed: true, Verified: false, "写入或核验未完成"));
                _audio.SetDefaultEndpoint(target.EndpointId!, target.Role);
                var verified = _audio.TryGetDefaultEndpointId(target.Direction, target.Role);
                if (verified != target.EndpointId)
                {
                    throw new InvalidOperationException(
                        $"{target.Direction}/{target.Role} 写入后回读不一致");
                }

                results[^1] = new RoleApplyResult(
                    target.Direction, target.Role, previous, target.EndpointId!,
                    Changed: true, Verified: true, "切换并回读成功");
            }

            // Earlier roles may change externally while later roles are written.
            foreach (var target in targets)
                if (_audio.TryGetDefaultEndpointId(target.Direction, target.Role) != target.EndpointId)
                    throw new SceneConflictException($"{target.Direction}/{target.Role} 在最终核验时已变化");

            return new SceneApplyResult
            {
                Status = SceneApplyStatus.Succeeded,
                Message = $"场景“{scene.Name}”已应用，6 个默认角色均已核验",
                Roles = results
            };
        }
        catch (Exception exception)
        {
            var rollbackFailures = RollBack(changed);
            var status = exception is SceneConflictException
                ? SceneApplyStatus.Conflict
                : rollbackFailures.Count == 0
                    ? SceneApplyStatus.Failed
                    : SceneApplyStatus.Partial;
            var rollbackMessage = rollbackFailures.Count == 0
                ? changed.Count == 0 ? "未写入任何角色" : "本次已写入的角色已恢复"
                : $"有 {rollbackFailures.Count} 个角色无法恢复";

            return new SceneApplyResult
            {
                Status = status,
                Message = $"{exception.Message}；{rollbackMessage}",
                Roles = results.Concat(rollbackFailures).ToArray()
            };
        }
    }

    private List<RoleApplyResult> RollBack(
        IEnumerable<(Target Target, string? Previous)> changedRoles)
    {
        var failures = new List<RoleApplyResult>();
        foreach (var (target, previous) in changedRoles.Reverse())
        {
            try
            {
                var current = _audio.TryGetDefaultEndpointId(target.Direction, target.Role);
                if (current == previous) continue; // Failed before writing, or already restored.
                if (current != target.EndpointId)
                {
                    failures.Add(new RoleApplyResult(
                        target.Direction, target.Role, previous, target.EndpointId!,
                        Changed: true, Verified: false, "检测到外部修改，未覆盖用户选择"));
                    continue;
                }

                if (string.IsNullOrWhiteSpace(previous))
                {
                    failures.Add(new RoleApplyResult(target.Direction, target.Role, previous, target.EndpointId!,
                        Changed: true, Verified: false, "原端点为空，无法恢复"));
                    continue;
                }

                _audio.SetDefaultEndpoint(previous, target.Role);
                var verified = _audio.TryGetDefaultEndpointId(target.Direction, target.Role);
                if (verified != previous)
                {
                    failures.Add(new RoleApplyResult(
                        target.Direction, target.Role, previous, target.EndpointId!,
                        Changed: true, Verified: false, "恢复后回读不一致"));
                }
            }
            catch (Exception exception)
            {
                failures.Add(new RoleApplyResult(
                    target.Direction, target.Role, previous, target.EndpointId!,
                    Changed: true, Verified: false, $"恢复失败：{exception.Message}"));
            }
        }

        return failures;
    }

    private static Target[] BuildTargets(AudioScene scene)
    {
        var outputNormal = scene.Output.NormalEndpointId;
        var outputCommunications = scene.Output.FollowNormalForCommunications
            ? outputNormal
            : scene.Output.CommunicationsEndpointId;
        var inputNormal = scene.Input.NormalEndpointId;
        var inputCommunications = scene.Input.FollowNormalForCommunications
            ? inputNormal
            : scene.Input.CommunicationsEndpointId;

        return
        [
            new(DeviceDirection.Output, AudioEndpointRole.Console, outputNormal),
            new(DeviceDirection.Output, AudioEndpointRole.Multimedia, outputNormal),
            new(DeviceDirection.Output, AudioEndpointRole.Communications, outputCommunications),
            new(DeviceDirection.Input, AudioEndpointRole.Console, inputNormal),
            new(DeviceDirection.Input, AudioEndpointRole.Multimedia, inputNormal),
            new(DeviceDirection.Input, AudioEndpointRole.Communications, inputCommunications)
        ];
    }

    private sealed record Target(
        DeviceDirection Direction,
        AudioEndpointRole Role,
        string? EndpointId);

    private sealed class SceneConflictException(string message) : Exception(message);
}

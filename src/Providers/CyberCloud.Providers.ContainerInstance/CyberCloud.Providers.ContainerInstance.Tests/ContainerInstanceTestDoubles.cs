using CyberCloud.Core.Time;
using CyberCloud.ResourceManager.Reconcile;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.ContainerInstance.Tests;

/// <summary>What every test in this project drives the reconciler or the handler with.</summary>
static class Groups {
    public static readonly Guid ClusterId = Guid.Parse("44444444-4444-4444-8444-444444444444");
    public static readonly Guid TenantA = Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa");
    public static readonly Guid TenantB = Guid.Parse("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb");
    public static readonly Guid Subscription = Guid.Parse("11111111-1111-4111-8111-111111111111");

    public static ResourceId Address(string name, Guid? tenant = null) =>
        new(tenant ?? TenantA, Subscription, "prod", ContainerGroups.Type, name, Guid.NewGuid());

    /// <summary>A vault path under a tenant's own prefix — the only kind a handle may name.</summary>
    public static string VaultPath(string leaf, Guid? tenant = null) => ContainerGroups.TenantVaultPrefix(tenant ?? TenantA) + leaf;

    public static ReconcileContext Context(
        GroupConnection connection,
        ResourceId address,
        JsonElement desired,
        ISecretResolver? secrets = null
    ) =>
        new(
            address,
            ContainerGroups.V2026,
            desired,
            null,
            ReconcileDriver.NamespaceFor(address),
            connection,
            secrets ?? new CyberCloud.ResourceManager.UnavailableSecretResolver(),
            new NullLog()
        );

    public static ActionContext Action(
        GroupConnection connection,
        ResourceId address,
        JsonElement desired,
        string action,
        string request = "{}"
    ) =>
        new(
            address,
            ContainerGroups.V2026,
            action,
            JsonDocument.Parse(request).RootElement.Clone(),
            desired,
            ReconcileDriver.NamespaceFor(address),
            connection,
            new CyberCloud.ResourceManager.UnavailableSecretResolver()
        );

    public static JsonDocument Body(string body) => JsonDocument.Parse(body);

    public static Task<ReconcileOutcome> Reconcile(
        GroupConnection connection,
        ResourceId address,
        JsonDocument body,
        ISecretResolver? secrets = null
    ) =>
        new ContainerGroupReconciler(new FixedClock()).ReconcileAsync(
            Context(connection, address, body.RootElement, secrets),
            TestContext.Current.CancellationToken
        );

    /// <summary>Puts a kubelet-shaped status onto the stored pod, as the kubelet would.</summary>
    public static void Report(GroupConnection connection, ObjectRef pod, JsonObject status) {
        var stored = JsonNode.Parse(connection.Objects[GroupConnection.Key(pod)])!.AsObject();
        stored["status"] = status;
        connection.Objects[GroupConnection.Key(pod)] = stored.ToJsonString();
    }
}

/// <summary>
///     A connection that keeps what it was given, carries <c>status</c> and a <c>uid</c> through an
///     apply as an API server does, and holds each container's log.
/// </summary>
/// <remarks>
///     ⚠ <see cref="Terminating" /> models the one API-server behaviour the pod's replacement path turns on:
///     a pod with running containers is not gone when its delete returns, it carries a
///     <c>deletionTimestamp</c> until its grace period is over.
/// </remarks>
sealed class GroupConnection : IKubeClusterConnection {
    int uids;

    public ConcurrentDictionary<string, string> Objects { get; } = new(StringComparer.Ordinal);

    public List<KubeCommand> Applied { get; } = [];

    public List<ObjectRef> Deleted { get; } = [];

    public Dictionary<string, string> Logs { get; } = new(StringComparer.Ordinal);

    /// <summary>A deleted pod stays, marked, rather than going at once.</summary>
    public bool Terminating { get; set; }

    public Guid ClusterId => Groups.ClusterId;

    public Task<Result<ApplyOutcome>> ApplyAsync(KubeCommand command, CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(command);
        Applied.Add(command);

        var key = Key(command.Target);
        var applied = JsonNode.Parse(command.Body)!.AsObject();
        var existed = Objects.TryGetValue(key, out var existing);

        if (existed && JsonNode.Parse(existing!) is JsonObject previous) {
            applied["status"] = previous["status"]?.DeepClone();
            applied["metadata"]!["uid"] = previous["metadata"]?["uid"]?.DeepClone();
        } else {
            applied["metadata"]!["uid"] = "uid-" + Interlocked.Increment(ref uids);
        }

        Objects[key] = applied.ToJsonString();

        return Task.FromResult(
            Result<ApplyOutcome>.Success(
                new() { Result = existed ? ApplyResult.Updated : ApplyResult.Created, Target = command.Target }
            )
        );
    }

    public Task<Result<KubeObject>> GetAsync(ObjectRef target, CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(target);

        return Task.FromResult(
            Objects.TryGetValue(Key(target), out var json)
                ? Result<KubeObject>.Success(new() { Ref = target, Json = json })
                : Result<KubeObject>.Failure(ErrorCode.ResourceNotFound, $"'{target}' is not here.")
        );
    }

    public Task<Result> DeleteAsync(
        KubeCommand command,
        CascadePolicy policy = CascadePolicy.Background,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(command);

        var key = Key(command.Target);

        if (!Objects.TryGetValue(key, out var json)) {
            return Task.FromResult(Result.Failure(ErrorCode.ResourceNotFound, $"'{command.Target}' is not here."));
        }

        Deleted.Add(command.Target);

        if (Terminating && command.Target.Kind.Kind == "Pod") {
            var marked = JsonNode.Parse(json)!.AsObject();
            marked["metadata"]!["deletionTimestamp"] = "2026-09-23T12:00:00Z";
            Objects[key] = marked.ToJsonString();
        } else {
            Objects.TryRemove(key, out _);
        }

        return Task.FromResult(Result.Success);
    }

    public Task<Result<string>> ReadLogsAsync(
        ObjectRef pod,
        string container,
        int tailLines,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(pod);

        if (!Objects.ContainsKey(Key(pod))) {
            return Task.FromResult(Result<string>.Failure(ErrorCode.ResourceNotFound, $"'{pod}' is not here."));
        }

        if (!Logs.TryGetValue(container, out var text)) {
            return Task.FromResult(
                Result<string>.Failure(ErrorCode.OperationInProgress, $"container \"{container}\" is waiting to start")
            );
        }

        return Task.FromResult(
            Result<string>.Success(string.Join('\n', text.Split('\n', StringSplitOptions.RemoveEmptyEntries).TakeLast(tailLines)) + "\n")
        );
    }

    public void Finish(ObjectRef pod) => Objects.TryRemove(Key(pod), out _);

    internal static string Key(ObjectRef target) => target.Kind.Kind + "/" + target.Namespace + "/" + target.Name;
}

/// <summary>A clock that does not move.</summary>
sealed class FixedClock : IClock {
    public DateTimeOffset UtcNow => new(2026, 9, 23, 12, 0, 0, TimeSpan.Zero);
}

/// <summary>A log that drops everything.</summary>
sealed class NullLog : IReconcileLog {
    public void Report(string phase, string detail) { }

    public void Report(string phase, string detail, int percent) { }
}

/// <summary>A vault holding exactly the handles a test seeds, and counting what it was asked.</summary>
sealed class SeededSecrets(params (string Path, string Field, string Value)[] entries) : ISecretResolver {
    public int Resolves { get; private set; }

    public Task<Result<string>> ResolveAsync(
        CyberCloud.Core.Contracts.SecretRef reference,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(reference);
        Resolves++;

        foreach (var (path, field, value) in entries) {
            if (path == reference.Path && field == reference.Field) {
                return Task.FromResult(Result<string>.Success(value));
            }
        }

        return Task.FromResult(Result<string>.Failure(ErrorCode.ResourceNotFound, $"the test vault holds nothing at '{reference}'."));
    }
}

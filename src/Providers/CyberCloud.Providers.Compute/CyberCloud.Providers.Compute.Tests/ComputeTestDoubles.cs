using CyberCloud.Core.Time;
using CyberCloud.ResourceManager.Reconcile;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.Compute.Tests;

/// <summary>What every test in this project drives a reconciler or a handler with.</summary>
static class Compute {
    public static readonly Guid ClusterId = Guid.Parse("33333333-3333-4333-8333-333333333333");
    public static readonly Guid TenantA = Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa");
    public static readonly Guid TenantB = Guid.Parse("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb");
    public static readonly Guid SubscriptionA = Guid.Parse("11111111-1111-4111-8111-111111111111");
    public static readonly Guid SubscriptionB = Guid.Parse("22222222-2222-4222-8222-222222222222");

    public static ResourceId Machine(string name, Guid? tenant = null, Guid? subscription = null) =>
        new(tenant ?? TenantA, subscription ?? SubscriptionA, "prod", VirtualMachines.Type, name, Guid.NewGuid());

    public static ResourceId Disk(string name) => new(TenantA, SubscriptionA, "prod", Disks.Type, name, Guid.NewGuid());

    public static ResourceId Image(string name) =>
        new(TenantA, SubscriptionA, "prod", Images.Type, name, Guid.NewGuid());

    /// <summary>A vault path under a tenant's own prefix — the only kind a cloud-init handle may name.</summary>
    public static string VaultPath(string leaf, Guid? tenant = null) =>
        VirtualMachines.TenantVaultPrefix(tenant ?? TenantA) + leaf;

    public static ReconcileContext Context(
        RecordingConnection connection,
        ResourceId address,
        JsonElement desired,
        ISecretResolver? secrets = null
    ) =>
        new(
            address,
            VirtualMachines.V2026,
            desired,
            null,
            ReconcileDriver.NamespaceFor(address),
            connection,
            secrets ?? new CyberCloud.ResourceManager.UnavailableSecretResolver(),
            new NullLog()
        );

    public static ObserveContext Observe(RecordingConnection connection, ResourceId address, JsonElement desired) =>
        new(address, VirtualMachines.V2026, desired, ReconcileDriver.NamespaceFor(address), connection);

    public static ActionContext Action(
        RecordingConnection connection,
        ResourceId address,
        JsonElement desired,
        string action
    ) =>
        new(
            address,
            VirtualMachines.V2026,
            action,
            JsonDocument.Parse("{}").RootElement,
            desired,
            ReconcileDriver.NamespaceFor(address),
            connection,
            new CyberCloud.ResourceManager.UnavailableSecretResolver()
        );

    /// <summary>The image's DataVolume, as CDI leaves it once the import is done.</summary>
    public static string ImportedImage(string name, string phase = Cdi.Succeeded) =>
        new JsonObject {
            ["apiVersion"] = Cdi.DataVolumeKind.ApiVersion,
            ["kind"] = Cdi.DataVolumeKind.Kind,
            ["metadata"] = new JsonObject { ["name"] = name },
            ["spec"] = new JsonObject {
                ["source"] = new JsonObject { ["registry"] = new JsonObject { ["url"] = "docker://x" } }
            },
            ["status"] = new JsonObject { ["phase"] = phase }
        }.ToJsonString();

    /// <summary>Puts a KubeVirt-shaped status onto a stored object, as the controller would.</summary>
    public static void Report(RecordingConnection connection, ObjectRef target, JsonObject status) {
        var stored = JsonNode.Parse(connection.Objects[RecordingConnection.Key(target)])!.AsObject();
        stored["status"] = status;
        connection.Store(RecordingConnection.Key(target), stored.ToJsonString());
    }

    public static JsonObject Spec(string objectJson) => JsonNode.Parse(objectJson)!["spec"]!.AsObject();
}

/// <summary>A connection that records what it was asked to do and can be made to misbehave.</summary>
/// <remarks>
///     The same double <c>ManagedClusterReconcilerTests</c> carries, with the one behaviour this
///     family needs from it: <c>status</c> survives an apply, as it does on a real API server, because
///     this reconciler's <c>Converged</c> reads one and its power handler reads <c>spec.runStrategy</c>
///     off an object the reconciler applied.
/// </remarks>
sealed class RecordingConnection : IKubeClusterConnection {
    public ConcurrentDictionary<string, string> Objects { get; } = new(StringComparer.Ordinal);

    public List<KubeCommand> Applied { get; } = [];

    public List<ObjectRef> Deleted { get; } = [];

    public List<ObjectRef> Read { get; } = [];

    public bool Suspend { get; init; }

    public string ConflictField { get; init; } = string.Empty;

    public bool SwallowApplies { get; init; }

    public ErrorCode? RefuseWith { get; init; }

    public Guid ClusterId => Compute.ClusterId;

    public Task<Result<ApplyOutcome>> ApplyAsync(KubeCommand command, CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(command);
        Applied.Add(command);

        if (RefuseWith is { } refusal) {
            return Task.FromResult(
                Result<ApplyOutcome>.Failure(
                    refusal,
                    $"the API server refused to apply {command.Target}: admission webhook denied the request."
                )
            );
        }

        if (Suspend) {
            return Task.FromResult(
                Result<ApplyOutcome>.Success(
                    new() {
                        Result = ApplyResult.Suspended,
                        Target = command.Target,
                        Message = "We cannot reach your cluster; this will resume automatically."
                    }
                )
            );
        }

        if (ConflictField.Length > 0) {
            return Task.FromResult(
                Result<ApplyOutcome>.Success(
                    new() {
                        Result = ApplyResult.Conflict,
                        Target = command.Target,
                        Drift = new() {
                            Target = command.Target,
                            FieldManager = command.FieldManager,
                            Conflicts = [new() { Field = ConflictField, OwnedBy = "virt-api" }]
                        }
                    }
                )
            );
        }

        // ⚠ The one write between a read and an apply that a test wants to land — an action racing a
        // reconcile pass — runs here, before the precondition is checked, exactly once.
        if (BeforeNextApply is { } racing) {
            BeforeNextApply = null;
            racing(command);
        }

        var key = Key(command.Target);
        var body = JsonNode.Parse(command.Body)!.AsObject();

        // IKubeCommandBuilder.IfResourceVersion, as the API server answers it: a version that moved is
        // Stale with nothing written; an absent object is created regardless.
        if ((body["metadata"] as JsonObject)?["resourceVersion"]?.GetValue<string>() is { Length: > 0 } carried) {
            if (Objects.ContainsKey(key) && carried != VersionOf(key)) {
                Stale.Add(command.Target);

                return Task.FromResult(
                    Result<ApplyOutcome>.Success(
                        new() { Result = ApplyResult.Stale, Target = command.Target, Message = "moved" }
                    )
                );
            }

            ((JsonObject)body["metadata"]!).Remove("resourceVersion");
        }

        if (!SwallowApplies) {
            Store(key, WithExistingStatus(key, body.ToJsonString()));
        }

        return Task.FromResult(
            Result<ApplyOutcome>.Success(new() { Result = ApplyResult.Created, Target = command.Target })
        );
    }

    /// <summary>A write that lands between the next apply's read and the apply itself, once.</summary>
    public Action<KubeCommand>? BeforeNextApply { get; set; }

    /// <summary>Every apply refused because its precondition had moved.</summary>
    public List<ObjectRef> Stale { get; } = [];

    /// <summary>Stores a body and moves the object's version, as any write on a real API server does.</summary>
    public void Store(string key, string json) {
        if (!Objects.TryGetValue(key, out var previous) || previous != json) {
            versions[key] = versions.GetValueOrDefault(key) + 1;
        }

        Objects[key] = json;
    }

    readonly ConcurrentDictionary<string, long> versions = new(StringComparer.Ordinal);

    string VersionOf(string key) =>
        versions.GetValueOrDefault(key, 1).ToString(System.Globalization.CultureInfo.InvariantCulture);

    public Task<Result<KubeObject>> GetAsync(ObjectRef target, CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(target);
        Read.Add(target);

        return Task.FromResult(
            Objects.TryGetValue(Key(target), out var json)
                ? Result<KubeObject>.Success(new() { Ref = target, Json = json, ResourceVersion = VersionOf(Key(target)) })
                : Result<KubeObject>.Failure(ErrorCode.ResourceNotFound, $"'{target}' is not here.")
        );
    }

    public Task<Result> DeleteAsync(
        KubeCommand command,
        CascadePolicy policy = CascadePolicy.Background,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(command);

        var removed = Objects.TryRemove(Key(command.Target), out _);

        if (removed) {
            Deleted.Add(command.Target);
        }

        return Task.FromResult(
            removed
                ? Result.Success
                : Result.Failure(ErrorCode.ResourceNotFound, $"'{command.Target}' is not here.")
        );
    }

    /// <summary>The objects of one kind in one namespace whose labels carry every pair of the selector.</summary>
    /// <remarks>
    ///     What a scale set's <c>listInstances</c> asks; the machines a pool controller would create are
    ///     put here by the test, labelled as the pool's template labels them.
    /// </remarks>
    public Task<Result<IReadOnlyList<KubeObjectSummary>>> ListAsync(
        GroupVersionKind kind,
        string ns,
        string labelSelector,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(kind);
        ArgumentException.ThrowIfNullOrEmpty(labelSelector);

        var wanted = labelSelector.Split(',').Select(static x => x.Split('=', 2)).ToList();
        var found = new List<KubeObjectSummary>();

        foreach (var (key, json) in Objects) {
            var parts = key.Split('/');

            if (parts[0] != kind.Kind || parts[1] != ns) {
                continue;
            }

            var labels = (JsonNode.Parse(json)?["metadata"]?["labels"] as JsonObject)
                ?.ToDictionary(static x => x.Key, static x => x.Value?.GetValue<string>() ?? string.Empty)
                ?? [];

            if (wanted.All(pair => labels.TryGetValue(pair[0], out var value) && value == pair[1])) {
                found.Add(new() { Kind = kind, Namespace = ns, Name = parts[2], Labels = labels });
            }
        }

        return Task.FromResult(Result<IReadOnlyList<KubeObjectSummary>>.Success(found));
    }

    /// <summary>Carries an existing object's <c>status</c> through an apply, as a real API server does.</summary>
    string WithExistingStatus(string key, string body) {
        if (!Objects.TryGetValue(key, out var existing) || JsonNode.Parse(existing) is not JsonObject previous) {
            return body;
        }

        var applied = JsonNode.Parse(body)!.AsObject();

        if (previous["status"] is { } status) {
            applied["status"] = status.DeepClone();
        }

        return applied.ToJsonString();
    }

    internal static string Key(ObjectRef target) => target.Kind.Kind + "/" + target.Namespace + "/" + target.Name;
}

/// <summary>A clock that does not move.</summary>
sealed class FixedClock : IClock {
    public DateTimeOffset UtcNow => new(2026, 9, 17, 12, 0, 0, TimeSpan.Zero);
}

/// <summary>A log that drops everything.</summary>
sealed class NullLog : IReconcileLog {
    public void Report(string phase, string detail) { }

    public void Report(string phase, string detail, int percent) { }
}

/// <summary>A vault holding exactly the handles a test seeds.</summary>
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

        return Task.FromResult(
            Result<string>.Failure(ErrorCode.ResourceNotFound, $"the test vault holds nothing at '{reference}'.")
        );
    }
}

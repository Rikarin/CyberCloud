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

    public static ResourceId Image(string name) => new(TenantA, SubscriptionA, "prod", Images.Type, name, Guid.NewGuid());

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
            ["spec"] = new JsonObject { ["source"] = new JsonObject { ["registry"] = new JsonObject { ["url"] = "docker://x" } } },
            ["status"] = new JsonObject { ["phase"] = phase }
        }.ToJsonString();

    /// <summary>Puts a KubeVirt-shaped status onto a stored object, as the controller would.</summary>
    public static void Report(RecordingConnection connection, ObjectRef target, JsonObject status) {
        var stored = JsonNode.Parse(connection.Objects[RecordingConnection.Key(target)])!.AsObject();
        stored["status"] = status;
        connection.Objects[RecordingConnection.Key(target)] = stored.ToJsonString();
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

        if (!SwallowApplies) {
            Objects[Key(command.Target)] = WithExistingStatus(Key(command.Target), command.Body);
        }

        return Task.FromResult(
            Result<ApplyOutcome>.Success(new() { Result = ApplyResult.Created, Target = command.Target })
        );
    }

    public Task<Result<KubeObject>> GetAsync(ObjectRef target, CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(target);
        Read.Add(target);

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

    public Task<Result<string>> ResolveAsync(CyberCloud.Core.Contracts.SecretRef reference, CancellationToken cancellationToken = default) {
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

using CyberCloud.ResourceManager.Reconcile;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.RecoveryServices.Tests;

/// <summary>
///     The cross-resource view, scripted: which addresses answer, with what snapshot and which
///     rendered objects — and a record of every question asked of it.
/// </summary>
/// <remarks>
///     ⚠ Every address not scripted answers <c>ResourceNotFound</c> with the view's own message, which
///     is what the real view answers for another tenant's path, an absent one and an ungranted one
///     alike. A test that wants "not granted" scripts nothing, exactly as the seam would have it.
/// </remarks>
sealed class ScriptedView : IResourceView {
    readonly Dictionary<string, (ResourceSnapshot Snapshot, ImmutableArray<ObjectRef> Objects)> visible =
        new(StringComparer.Ordinal);

    /// <summary>Every path asked of <see cref="ReadAsync" /> or <see cref="RenderedObjectsAsync" />, in order.</summary>
    public List<string> Asked { get; } = [];

    /// <summary>Makes an address readable, with the given body and rendered objects.</summary>
    public ScriptedView Showing(
        ResourceId target,
        string body,
        Guid clusterId,
        ProvisioningState state,
        params ObjectRef[] rendered
    ) {
        visible[target.CanonicalPath] = (
            new ResourceSnapshot {
                Id = target.Id == Guid.Empty ? Guid.NewGuid() : target.Id,
                Path = target.Path,
                Type = target.Type.ToString(),
                Name = target.Name,
                ApiVersion = "2026-08-01",
                ProvisioningState = state,
                Body = body,
                ClusterId = clusterId
            },
            [.. rendered]
        );

        return this;
    }

    public Task<Result<ResourceSnapshot>> ReadAsync(ResourceId target, CancellationToken cancellationToken = default) {
        Asked.Add(target.Path);

        return Task.FromResult(
            visible.TryGetValue(target.CanonicalPath, out var seen)
                ? Result<ResourceSnapshot>.Success(seen.Snapshot)
                : Result<ResourceSnapshot>.Failure(ErrorCode.ResourceNotFound, $"'{target.Path}' does not exist.")
        );
    }

    public Task<Result<ImmutableArray<ObjectRef>>> RenderedObjectsAsync(
        ResourceId target,
        CancellationToken cancellationToken = default
    ) {
        Asked.Add(target.Path);

        return Task.FromResult(
            visible.TryGetValue(target.CanonicalPath, out var seen)
                ? Result<ImmutableArray<ObjectRef>>.Success(seen.Objects)
                : Result<ImmutableArray<ObjectRef>>.Failure(
                    ErrorCode.ResourceNotFound,
                    $"'{target.Path}' does not exist."
                )
        );
    }
}

/// <summary>The watch, recording.</summary>
sealed class RecordingWatch : IResourceWatch {
    public List<ResourceTypeName> Subscribed { get; } = [];

    public Task<Result> SubscribeAsync(ResourceTypeName type, CancellationToken cancellationToken = default) {
        Subscribed.Add(type);
        return Task.FromResult(Result.Success);
    }

    public Task<Result> UnsubscribeAsync(ResourceTypeName type, CancellationToken cancellationToken = default) =>
        Task.FromResult(Result.Success);
}

/// <summary>
///     A cluster connection that remembers what it was asked to do and holds what was applied, with
///     the two listings the vault reads.
/// </summary>
sealed class RecordingConnection : IKubeClusterConnection {
    public ConcurrentDictionary<string, string> Objects { get; } = new(StringComparer.Ordinal);

    public List<KubeCommand> Applied { get; } = [];

    public List<ObjectRef> Deleted { get; } = [];

    public bool Suspend { get; init; }

    public bool RefuseListing { get; init; }

    public Guid ClusterId { get; init; } = Guid.Parse("eeeeeeee-0000-4000-8000-000000000005");

    public Task<Result<ApplyOutcome>> ApplyAsync(KubeCommand command, CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(command);
        Applied.Add(command);

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

        // ⚠ Stored WITH the labels the builder injected, because the vault's listings select on them —
        // the body alone carries none, exactly as a rendered object carries none until applied.
        var root = JsonNode.Parse(command.Body)!.AsObject();
        var metadata = root["metadata"] as JsonObject ?? [];
        metadata["labels"] = new JsonObject(
            command.Labels.Select(static x => KeyValuePair.Create(x.Key, (JsonNode?)x.Value))
        );
        metadata["namespace"] = command.Target.Namespace;
        // A uid, as the API server issues one — stable per object, so a re-apply keeps it and an owner
        // reference taken from it stays valid.
        metadata["uid"] = "uid-" + Key(command.Target);
        root["metadata"] = metadata;
        root["kind"] = command.Target.Kind.Kind;
        root["apiVersion"] = command.Target.Kind.ApiVersion;

        var existed = Objects.ContainsKey(Key(command.Target));
        Objects[Key(command.Target)] = root.ToJsonString();

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

        var removed = Objects.TryRemove(Key(command.Target), out _);
        if (removed) {
            Deleted.Add(command.Target);
        }

        return Task.FromResult(
            removed ? Result.Success : Result.Failure(ErrorCode.ResourceNotFound, $"'{command.Target}' is not here.")
        );
    }

    public Task<Result<IReadOnlyList<KubeObjectSummary>>> ListAsync(
        GroupVersionKind kind,
        string ns,
        string labelSelector,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(kind);

        if (RefuseListing) {
            return Task.FromResult(
                Result<IReadOnlyList<KubeObjectSummary>>.Failure(
                    ErrorCode.InternalError,
                    "this connection cannot list."
                )
            );
        }

        var wanted = labelSelector.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(static pair => pair.Split('=', 2))
            .ToDictionary(static x => x[0], static x => x.Length > 1 ? x[1] : string.Empty, StringComparer.Ordinal);

        var found = new List<KubeObjectSummary>();

        foreach (var (key, json) in Objects) {
            if (!key.StartsWith(kind.Kind + "/" + ns + "/", StringComparison.Ordinal)) {
                continue;
            }

            var held = LabelsOf(json);
            if (wanted.All(pair => held.TryGetValue(pair.Key, out var value) && value == pair.Value)) {
                found.Add(
                    new() { Kind = kind, Namespace = ns, Name = key[(key.LastIndexOf('/') + 1)..], Labels = held }
                );
            }
        }

        return Task.FromResult(Result<IReadOnlyList<KubeObjectSummary>>.Success(found));
    }

    /// <summary>Places an object the way an operator would — behind the reconciler's back, no command.</summary>
    public void Plant(ObjectRef target, string json) => Objects[Key(target)] = json;

    public bool Holds(ObjectRef target) => Objects.ContainsKey(Key(target));

    public string? Read(ObjectRef target) => Objects.TryGetValue(Key(target), out var json) ? json : null;

    internal static string Key(ObjectRef target) => target.Kind.Kind + "/" + target.Namespace + "/" + target.Name;

    static Dictionary<string, string> LabelsOf(string json) {
        var labels = ((JsonNode.Parse(json) as JsonObject)?["metadata"] as JsonObject)?["labels"] as JsonObject;
        return labels?.ToDictionary(
            static x => x.Key,
            static x => x.Value?.GetValue<string>() ?? string.Empty,
            StringComparer.Ordinal
        )
            ?? new Dictionary<string, string>(StringComparer.Ordinal);
    }
}

sealed class FixedClock : IClock {
    public DateTimeOffset UtcNow { get; init; } = new(2026, 9, 18, 12, 0, 0, TimeSpan.Zero);
}

/// <summary>
///     Records what a handler asked the manager to create, and answers the way the write path would.
/// </summary>
/// <remarks>
///     ⚠ A double of the SEAM, never of the write path: what the manager does with the request is
///     <c>ResourceManagerService.CreateForActionAsync</c>'s, tested through the real manager in
///     <c>RecoverThroughTheWritePathTests</c>. This one lets the handler's own checks be asserted in
///     isolation — what it asks for, and that a refusal comes back unchanged.
/// </remarks>
sealed class RecordingCreator : IResourceCreator {
    public List<(ResourceTypeName Type, string Name, string ApiVersion, string Body)> Created { get; } = [];

    public Error? Refuse { get; init; }

    public Task<Result<ResourceCreated>> CreateAsync(
        ResourceTypeName type,
        string name,
        string apiVersion,
        string body,
        CancellationToken cancellationToken = default
    ) {
        if (Refuse is { } refusal) {
            return Task.FromResult(Result<ResourceCreated>.Failure(refusal));
        }

        Created.Add((type, name, apiVersion, body));
        var id = new ResourceId(Ids.TenantA, Ids.SubscriptionA, "prod", type, name, Guid.NewGuid());
        return Task.FromResult(Result<ResourceCreated>.Success(new(id, Guid.NewGuid())));
    }
}

sealed class RecordingLog : IReconcileLog {
    public List<(string Phase, string Detail)> Entries { get; } = [];

    public void Report(string phase, string detail) => Entries.Add((phase, detail));

    public void Report(string phase, string detail, int percent) => Entries.Add((phase, detail));
}

/// <summary>The fixed ids every test in this project addresses with.</summary>
static class Ids {
    public static Guid TenantA { get; } = Guid.Parse("aaaaaaaa-0000-4000-8000-000000000001");

    public static Guid TenantB { get; } = Guid.Parse("bbbbbbbb-0000-4000-8000-000000000002");

    public static Guid SubscriptionA { get; } = Guid.Parse("cccccccc-0000-4000-8000-000000000003");

    public static Guid SubscriptionB { get; } = Guid.Parse("dddddddd-0000-4000-8000-000000000004");

    public static Guid Cluster { get; } = Guid.Parse("eeeeeeee-0000-4000-8000-000000000005");

    public static Guid OtherCluster { get; } = Guid.Parse("ffffffff-0000-4000-8000-000000000006");

    static readonly ConcurrentDictionary<string, Guid> VaultIds = new(StringComparer.Ordinal);

    /// <summary>A vault's address, with one GUID per name so two passes over one vault agree on it.</summary>
    public static ResourceId Vault(
        string name,
        Guid? tenant = null,
        Guid? subscription = null,
        string group = "prod"
    ) =>
        new(
            tenant ?? TenantA,
            subscription ?? SubscriptionA,
            group,
            RecoveryVaults.Type,
            name,
            VaultIds.GetOrAdd(name, static _ => Guid.NewGuid())
        );

    public static ResourceId Server(
        string name,
        Guid? tenant = null,
        Guid? subscription = null,
        string group = "prod"
    ) =>
        new(
            tenant ?? TenantA,
            subscription ?? SubscriptionA,
            group,
            RecoveryVaults.PostgresServerType,
            name,
            Guid.Empty
        );

    public static string Namespace(ResourceId id) => ReconcileDriver.NamespaceFor(id);

    /// <summary>A PostgreSQL server body as its own type stores it: no backup block, so the published defaults apply.</summary>
    public static string ServerBody(bool? backupEnabled = null, int? retentionDays = null) {
        var properties = new JsonObject { ["clusterId"] = Cluster.ToString("D"), ["version"] = "17", ["replicas"] = 2 };

        if (backupEnabled is not null || retentionDays is not null) {
            var backup = new JsonObject();
            if (backupEnabled is { } enabled) {
                backup["enabled"] = enabled;
            }

            if (retentionDays is { } days) {
                backup["retentionDays"] = days;
            }

            properties["backup"] = backup;
        }

        return new JsonObject { ["location"] = "eu-central", ["properties"] = properties }.ToJsonString();
    }

    public static ReconcileContext Context(
        RecordingConnection? connection,
        ResourceId vault,
        JsonElement desired,
        IResourceView? view = null,
        IResourceWatch? watch = null,
        IReconcileLog? log = null
    ) =>
        new(
            vault,
            RecoveryVaults.V2026,
            desired,
            null,
            Namespace(vault),
            connection,
            new UnavailableSecretResolver(),
            log ?? new RecordingLog()
        ) { View = view ?? new ScriptedView(), Watch = watch ?? new RecordingWatch() };
}

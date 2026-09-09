using CyberCloud.Core.Time;
using CyberCloud.ResourceManager.Reconcile;
using CyberCloud.ResourceManager.Conformance;
using System.Collections.Concurrent;
using System.Text.Json;

namespace CyberCloud.Providers.Mail.Tests;

/// <summary>
///     The doubles this provider's hand-written tests drive the reconciler with.
/// </summary>
/// <remarks>
///     ⚠ <b>They are this provider's own rather than shared, which is the convention every family
///     follows.</b> A shared recording connection would have to satisfy every provider's assertions
///     at once, and each family needs a different set of misbehaviours out of it.
///     <see cref="InMemorySecretVault" /> is the exception and IS shared, because a vault's
///     behaviour — <c>cas=0</c> writes once, resolves read back — is the platform's contract rather
///     than any provider's.
/// </remarks>
static class MailHarness {
    /// <summary>The tenant most tests run in.</summary>
    public static Guid TenantA { get; } = Guid.Parse("11111111-1111-4111-8111-111111111111");

    /// <summary>A second tenant, for the cross-tenant test.</summary>
    public static Guid TenantB { get; } = Guid.Parse("11111111-1111-4111-8111-222222222222");

    /// <summary>The subscription most tests run in.</summary>
    public static Guid SubscriptionA { get; } = Guid.Parse("22222222-2222-4222-8222-222222222222");

    /// <summary>The cluster a body is placed in.</summary>
    public static Guid ClusterId { get; } = Guid.Parse("eeeeeeee-0000-4000-8000-000000000001");

    /// <summary>An address in a named tenant and its own subscription.</summary>
    /// <param name="name">The resource's name, which on this type is the mail domain.</param>
    /// <param name="tenant">The tenant.</param>
    /// <param name="subscription">The subscription.</param>
    /// <param name="id">The resource GUID. ⚠ Distinct per tenant, or the vault paths collide.</param>
    public static ResourceId Address(string name, Guid tenant, Guid subscription, Guid? id = null) =>
        new(
            tenant,
            subscription,
            "prod",
            MailDomains.Type,
            name,
            id ?? Guid.Parse("33333333-3333-4333-8333-333333333333")
        );

    /// <summary>A context over a connection and a vault.</summary>
    /// <param name="connection">The cluster, or <see langword="null" /> for none.</param>
    /// <param name="desired">The desired body.</param>
    /// <param name="vault">The vault, or <see langword="null" /> for a fresh empty one.</param>
    /// <param name="address">The resource, or <see langword="null" /> for the default.</param>
    public static ReconcileContext Context(
        IKubeClusterConnection? connection,
        JsonElement desired,
        InMemorySecretVault? vault = null,
        ResourceId? address = null
    ) {
        var id = address ?? Address("example-com", TenantA, SubscriptionA);
        var store = vault ?? new InMemorySecretVault();

        return new(
            id,
            MailDomains.V2026,
            desired,
            null,
            ReconcileDriver.NamespaceFor(id),
            connection,
            store,
            new NullLog()
        ) {
            SecretWriter = store
        };
    }
}

/// <summary>A connection that records what it was asked to do and can be made to misbehave.</summary>
sealed class RecordingConnection : IKubeClusterConnection {
    /// <summary>What is in the "cluster", keyed by kind, namespace and name.</summary>
    public ConcurrentDictionary<string, string> Objects { get; } = new(StringComparer.Ordinal);

    /// <summary>Every command applied, in order.</summary>
    public List<KubeCommand> Applied { get; } = [];

    /// <summary>Every object deleted, in order.</summary>
    public List<ObjectRef> Deleted { get; } = [];

    /// <summary>Every object <i>read</i>, in order — clause 4's evidence.</summary>
    public List<ObjectRef> Read { get; } = [];

    /// <summary>Whether every apply answers <c>Suspended</c>.</summary>
    public bool Suspend { get; init; }

    /// <summary>Whether every apply fails outright.</summary>
    public bool FailApplies { get; set; }

    public Guid ClusterId => MailHarness.ClusterId;

    public Task<Result<ApplyOutcome>> ApplyAsync(
        KubeCommand command,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(command);

        if (FailApplies) {
            // ⚠ Not recorded in Applied: a refused apply changed nothing, and the ordering assertion
            // reads that list to prove the cluster was never touched before the vault was.
            return Task.FromResult(
                Result<ApplyOutcome>.Failure(ErrorCode.ProvisioningFailed, "the API server refused.")
            );
        }

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

        Objects[Key(command.Target)] = command.Body;

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

    /// <summary>The body applied for one object, or <see langword="null" />.</summary>
    /// <param name="target">The object.</param>
    public string? Peek(ObjectRef target) =>
        Objects.TryGetValue(Key(target), out var json) ? json : null;

    /// <summary>
    ///     ⚠ Keyed by kind, namespace AND name. The namespace is in it because the cross-tenant test
    ///     puts the same domain name in two tenants.
    /// </summary>
    internal static string Key(ObjectRef target) =>
        target.Kind.Kind + "/" + target.Namespace + "/" + target.Name;
}

/// <summary>A clock that does not move. Nothing here depends on time passing.</summary>
sealed class FixedClock : IClock {
    public DateTimeOffset UtcNow => new(2026, 9, 9, 12, 0, 0, TimeSpan.Zero);
}

/// <summary>A log that drops everything. These tests assert outcomes, not progress.</summary>
sealed class NullLog : IReconcileLog {
    public void Report(string phase, string detail) { }

    public void Report(string phase, string detail, int percent) { }
}

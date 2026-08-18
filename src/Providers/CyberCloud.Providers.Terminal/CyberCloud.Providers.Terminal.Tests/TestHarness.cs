using CyberCloud.Core.Time;
using System.Collections.Concurrent;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.Terminal.Tests;

/// <summary>A connection that records what it was asked to do and can be made to misbehave.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>This harness is a fresh copy and it has to be.</b>
///         <c>src/Providers/README.md § Hard rule</c> forbids a <c>Providers.*</c> assembly
///         referencing another, so <c>RecordingConnection</c>, <c>FixedClock</c> and <c>NullLog</c>
///         cannot be shared with any other provider's tests however identical they look. The
///         duplication is the rule working.
///     </para>
///     <para>
///         ⚠ <b>ONE THING HERE IS NOT A COPY: <see cref="AssignsUids" />.</b> A real API server stamps
///         <c>metadata.uid</c> on every object it accepts, and this is the first provider for which
///         that matters — <c>CloudConsoleSessionHandler</c> returns the shell pod's UID as the session
///         id, so a fake that echoed the applied body back unchanged would make every connect fail. It
///         is a knob rather than always-on because the refusal path needs a world where the uid is
///         missing, and that path is reachable against a badly-behaved proxy rather than never.
///     </para>
/// </remarks>
sealed class RecordingConnection : IKubeClusterConnection {
    /// <summary>What is in the "cluster", keyed by kind, namespace and name.</summary>
    public ConcurrentDictionary<string, string> Objects { get; } = new(StringComparer.Ordinal);

    /// <summary>Every command applied, in order.</summary>
    public List<KubeCommand> Applied { get; } = [];

    /// <summary>Every object deleted, in order.</summary>
    public List<ObjectRef> Deleted { get; } = [];

    /// <summary>The cascade policy of every delete, in order.</summary>
    public List<CascadePolicy> Cascades { get; } = [];

    /// <summary>Every object <i>read</i>, in order — clause 4's evidence.</summary>
    public List<ObjectRef> Read { get; } = [];

    /// <summary>Whether every apply answers <c>Suspended</c>.</summary>
    public bool Suspend { get; init; }

    /// <summary>The field another manager owns, or empty.</summary>
    public string ConflictField { get; init; } = string.Empty;

    /// <summary>Whether an apply reports success and stores nothing — the clause-4 trap.</summary>
    public bool SwallowApplies { get; init; }

    /// <summary>Whether an accepted object is stamped with a <c>metadata.uid</c>, as a real one is.</summary>
    public bool AssignsUids { get; init; } = true;

    /// <summary>The <c>status.phase</c> stamped onto an accepted <c>Pod</c>, or empty for none.</summary>
    /// <remarks>
    ///     ⚠ Empty by default, because a real API server accepts a pod and answers with <b>no phase at
    ///     all</b> until the kubelet reports one. A fake that defaulted to <c>Running</c> would make
    ///     every connect look ready and would hide the one branch a person actually sees first.
    /// </remarks>
    public string PodPhase { get; init; } = string.Empty;

    public Guid ClusterId => Guid.Parse("eeeeeeee-0000-4000-8000-000000000005");

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

        if (ConflictField.Length > 0) {
            return Task.FromResult(
                Result<ApplyOutcome>.Success(
                    new() {
                        Result = ApplyResult.Conflict,
                        Target = command.Target,
                        Drift = new() {
                            Target = command.Target,
                            FieldManager = command.FieldManager,
                            Conflicts = [new() { Field = ConflictField, OwnedBy = "kubectl-edit" }]
                        }
                    }
                )
            );
        }

        if (!SwallowApplies) {
            Objects[Key(command.Target)] = Accept(command);
        }

        return Task.FromResult(
            Result<ApplyOutcome>.Success(new() { Result = ApplyResult.Created, Target = command.Target })
        );
    }

    /// <summary>What the "API server" stores — the body, plus what a real one adds.</summary>
    string Accept(KubeCommand command) {
        if (JsonNode.Parse(command.Body) is not JsonObject document) {
            return command.Body;
        }

        if (AssignsUids) {
            var metadata = document["metadata"] as JsonObject;

            if (metadata is null) {
                metadata = [];
                document["metadata"] = metadata;
            }

            // Deterministic, so a re-apply of the same object keeps the same uid — which is what makes
            // "reconnect returns the same session id" a real assertion rather than a coincidence.
            metadata["uid"] = "uid-" + Key(command.Target);
        }

        if (PodPhase.Length > 0 && command.Target.Kind.Kind == "Pod") {
            document["status"] = new JsonObject { ["phase"] = PodPhase };
        }

        return document.ToJsonString();
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
            Cascades.Add(policy);
        }

        return Task.FromResult(
            removed
                ? Result.Success
                : Result.Failure(ErrorCode.ResourceNotFound, $"'{command.Target}' is not here.")
        );
    }

    /// <summary>
    ///     ⚠ Keyed by kind, namespace AND name. The namespace is in it because the cross-tenant test
    ///     puts the same resource name in two tenants, which is the only shape in which one singleton
    ///     reconciler serving both can be caught mixing them.
    /// </summary>
    internal static string Key(ObjectRef target) =>
        target.Kind.Kind + "/" + target.Namespace + "/" + target.Name;
}

/// <summary>A clock that does not move. Nothing here depends on time passing.</summary>
sealed class FixedClock : IClock {
    public DateTimeOffset UtcNow => new(2026, 8, 18, 12, 0, 0, TimeSpan.Zero);
}

/// <summary>A log that drops everything. These tests assert outcomes, not progress.</summary>
sealed class NullLog : IReconcileLog {
    public void Report(string phase, string detail) { }

    public void Report(string phase, string detail, int percent) { }
}

/// <summary>
///     A reconciler that <c>CheckNoHiddenState</c> reports and that is not stateless.
/// </summary>
/// <remarks>
///     The field is <see langword="readonly" />, which stops it being reassigned and stops nothing
///     about the dictionary it holds. That is the shape a per-tenant cache takes when somebody adds
///     one for performance. <c>CheckNoHiddenState</c> used to skip it for being
///     <see langword="readonly" /> and now reports it; the cross-tenant test in the sibling file is
///     what still catches the mixing a field's declared type cannot show.
/// </remarks>
sealed class ReconcilerWithAReadonlyCache : IResourceReconciler {
    readonly Dictionary<string, string> lastRendered = new(StringComparer.Ordinal);

    public ResourceTypeName Type => CloudConsoles.Type;

    public Task<ReconcileOutcome> ReconcileAsync(
        ReconcileContext context,
        CancellationToken cancellationToken = default
    ) {
        lastRendered[context.Id.Name] = context.Desired.GetRawText();
        return Task.FromResult(ReconcileOutcome.Converged);
    }

    public Task<ReconcileOutcome> DeleteAsync(
        ReconcileContext context,
        CancellationToken cancellationToken = default
    ) => Task.FromResult(ReconcileOutcome.Converged);

    public Task<ObservedState> ObserveAsync(
        ObserveContext context,
        CancellationToken cancellationToken = default
    ) => Task.FromResult(ObservedState.Absent);
}

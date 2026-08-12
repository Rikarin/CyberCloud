using System.Text.Json;

namespace CyberCloud.ResourceManager.Contracts;

/// <summary>
///     Everything one reconcile pass is allowed to know. docs/plan/08 § The reconcile loop.
/// </summary>
/// <remarks>
///     <para>
///         <b>This record <i>is</i> clause 2 of the reconciler contract.</b> docs/plan/08 § The
///         reconcile loop: <i>"No hidden state. Everything it needs comes from
///         <c>ReconcileContext</c>. A reconciler with a field is a reconciler that breaks when the
///         grain moves silo."</i> The conformance suite rejects a reconciler type with instance
///         fields for exactly that reason, and this record is what makes the rule livable — a
///         reconciler that was tempted to cache something has somewhere to read it from instead.
///     </para>
///     <para>
///         ⚠
///         <b>
///             <see cref="Cluster" /> is nullable and docs/plan/08's sketch has it non-nullable.
///         </b>
///         The document's own § What the resource manager deliberately does not do requires the
///         manager to <i>"work for a provider with no cluster at all (a DNS zone, a mail domain, a
///         role assignment)"</i>, and a non-nullable connection makes that unrepresentable. So the
///         sketch and the table disagree, and the table wins: a clusterless provider gets
///         <see langword="null" /> here and never touches it.
///     </para>
///     <para>
///         ⚠ <b><see cref="Namespace" /> is not in the document's record and has to be.</b>
///         docs/plan/09 § The command builder's worked example reads
///         <c>.InNamespace(ctx.Namespace)</c> against this very type, which does not compile against
///         the record as § The reconcile loop declares it. Rather than make every reconciler derive
///         the namespace itself — twenty derivations, twenty chances to disagree — the manager
///         computes it once and passes it.
///     </para>
/// </remarks>
/// <param name="Id">
///     The resource, with <see cref="ResourceId.Id" /> resolved. ⚠ Never the path-parsed form whose
///     <see cref="ResourceId.Id" /> is <see cref="Guid.Empty" /> — a reconciler labelling objects
///     with an empty resource id produces objects the drift scan reports as orphans forever.
/// </param>
/// <param name="ApiVersion">
///     The api-version the desired body was <i>written</i> at, which is not necessarily the newest
///     the registry knows. docs/plan/08 § The provider registry: each version has its own
///     body-to-state mapping.
/// </param>
/// <param name="Desired">
///     The desired body, already validated against <paramref name="ApiVersion" />'s schema. A
///     reconciler may assume every required property is present and well-typed; it may not assume
///     anything the schema does not state.
/// </param>
/// <param name="Observed">
///     What the last observation saw, or <see langword="null" /> when nothing has been observed yet.
///     ⚠ Not a substitute for observing — clause 4 is that <c>Converged</c> follows a read-back, and
///     a cached observation from ten minutes ago is not one.
/// </param>
/// <param name="Namespace">
///     The Kubernetes namespace this resource's objects belong in, for a provider that has objects.
///     Empty for a clusterless provider.
/// </param>
/// <param name="Cluster">
///     The cluster to reach, or <see langword="null" /> for a provider that has none. Reached only
///     through <c>KubeCommand.For(ctx.Cluster)</c> — docs/plan/09 § The command builder.
/// </param>
/// <param name="Secrets">
///     Resolves <see cref="SecretRef" /> handles at the data plane. ⚠ The value never comes back into
///     grain state; see <see cref="ISecretResolver" />.
/// </param>
/// <param name="Log">
///     Where a reconciler says anything <see cref="ReconcileOutcome" /> has no case for. Entries
///     stream to <c>operation-progress</c>.
/// </param>
public readonly record struct ReconcileContext(
    ResourceId Id,
    string ApiVersion,
    JsonElement Desired,
    ObservedState? Observed,
    string Namespace,
    IKubeClusterConnection? Cluster,
    ISecretResolver Secrets,
    IReconcileLog Log
) {
    /// <summary>
    ///     Mints a credential the resource needs and does not yet have. ⚠ Mint-once — see
    ///     <see cref="ISecretWriter" />.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>An <c>init</c> property rather than a positional parameter, and the reason is that
    ///         a refusing default is the only safe one.</b> Every construction of this record that
    ///         predates the mint path names eight arguments and means them; adding a ninth would make
    ///         each of those call sites choose a writer, and the ones that did not care would reach for
    ///         whatever compiled. Initialised here, they all get
    ///         <see cref="RefusingSecretWriter" /> — which refuses by name, exactly as an unwired host
    ///         does — and only the driver, which knows what the host wired, replaces it.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Here rather than on a reconciler's constructor, even though
    ///         <c>ReconcilerConformance.CheckNoHiddenState</c> would have allowed the constructor.</b>
    ///         Clause 2 is "everything it needs comes from <see cref="ReconcileContext" />", and
    ///         <see cref="Secrets" /> — the same seam in the other direction — is already here. Two
    ///         halves of one story arriving by two routes is the shape somebody later has to explain.
    ///     </para>
    /// </remarks>
    public ISecretWriter SecretWriter { get; init; } = new RefusingSecretWriter();
}

/// <summary>
///     The <see cref="ISecretWriter" /> a <see cref="ReconcileContext" /> carries when nobody supplied
///     one.
/// </summary>
/// <remarks>
///     ⚠ <b>A refusal and not a no-op, for the reason every seam in this tree gives.</b> A mint that
///     silently did nothing would tell a reconciler the credential exists, and the reconciler would
///     then render a data plane against a secret nobody wrote. <c>CyberCloud.ResourceManager</c>'s
///     <c>UnavailableSecretWriter</c> is the registered default and says more about the wiring; this
///     one exists because <see cref="ReconcileContext" /> lives in the contracts assembly and cannot
///     name it.
/// </remarks>
public sealed class RefusingSecretWriter : ISecretWriter {
    /// <inheritdoc />
    public Task<Result<SecretMint>> MintAsync(
        string path,
        IReadOnlyDictionary<string, string> fields,
        CancellationToken cancellationToken = default
    ) =>
        Task.FromResult(
            Result<SecretMint>.Failure(
                ErrorCode.InternalError,
                $"This reconcile context carries no secret writer, so nothing can be minted at "
                + $"'{path}'. A pass driven by ReconcileDriver always carries the host's writer; a "
                + "context built by hand has to supply one through ReconcileContext.SecretWriter."
            )
        );
}

/// <summary>
///     What an observation pass is given. Strictly less than <see cref="ReconcileContext" />, because
///     observing must not be able to change anything.
/// </summary>
/// <remarks>
///     ⚠ <b>The absence of <see cref="ReconcileContext.Log" /> is the point.</b> Observation runs on
///     the drift path as well as the reconcile path, so it can be called on a resource with no live
///     operation to report progress to. A reconciler that wrote progress from
///     <c>ObserveAsync</c> would produce entries against whatever operation happened to be open.
/// </remarks>
/// <param name="Id">The resource, with its GUID resolved.</param>
/// <param name="ApiVersion">The api-version the desired body was written at.</param>
/// <param name="Desired">The desired body, so the observer knows what shape to look for.</param>
/// <param name="Namespace">The resource's namespace, or empty for a clusterless provider.</param>
/// <param name="Cluster">The cluster to read, or <see langword="null" />.</param>
public readonly record struct ObserveContext(
    ResourceId Id,
    string ApiVersion,
    JsonElement Desired,
    string Namespace,
    IKubeClusterConnection? Cluster
);

/// <summary>
///     Where a reconciler says the things <see cref="ReconcileOutcome" /> has no case for.
/// </summary>
/// <remarks>
///     <para>
///         docs/plan/08 § The reconcile loop:
///         <i>
///             "A reconciler that wants to say something else wants to log, and <c>IReconcileLog</c>
///             is how — those entries stream to <c>operation-progress</c> and appear in the portal and
///             in <c>cyc --wait</c>, which is what turns a four-minute cluster creation from a spinner
///             into a story."
///         </i>
///     </para>
///     <para>
///         ⚠ <b>Synchronous and returning nothing, on purpose.</b> A reconciler is bounded at 30
///         seconds (clause 3) and runs inside a single-threaded grain turn. An awaitable log would be
///         one more thing that can block that turn, and a reconciler that awaited a progress write
///         per replica would spend its budget on reporting. The grain buffers what is reported and
///         flushes once, after the pass.
///     </para>
/// </remarks>
public interface IReconcileLog {
    /// <summary>Records one progress entry.</summary>
    /// <param name="phase">
    ///     A short, stable name for the phase — <c>rendering</c>, <c>applying</c>,
    ///     <c>waiting-for-ready</c>. Stable because the portal groups on it.
    /// </param>
    /// <param name="detail">
    ///     What is happening, naming the actual numbers. "waiting for 2 of 3 replicas", not
    ///     "waiting".
    /// </param>
    void Report(string phase, string detail);

    /// <summary>Records one progress entry and a completion percentage.</summary>
    /// <param name="phase">The phase name.</param>
    /// <param name="detail">What is happening.</param>
    /// <param name="percentComplete">
    ///     How far along, 0 to 100. Clamped rather than refused — a provider miscounting replicas
    ///     should not fail an otherwise healthy provision.
    /// </param>
    void Report(string phase, string detail, int percentComplete);
}

/// <summary>
///     Resolves a secret handle to its value, at the data plane and never into grain state.
/// </summary>
/// <remarks>
///     ⚠ <b>The return value must not be written anywhere durable.</b> docs/plan/00 § Non-negotiables,
///     the "Secrets never reach grain state" row, and <c>CC1005</c> which enforces it on
///     <c>[Id]</c>-annotated members. This interface exists so a reconciler can put a password into a
///     Kubernetes <c>Secret</c> it is applying without the password ever appearing in the resource's
///     desired state. docs/plan/08 § What the resource manager deliberately does not do routes the
///     implementation to OpenBao.
/// </remarks>
public interface ISecretResolver {
    /// <summary>Reads the value behind a handle.</summary>
    /// <param name="reference">The handle, as it appears in desired state.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The value, or a failure. ⚠ Do not persist the value.</returns>
    Task<Result<string>> ResolveAsync(SecretRef reference, CancellationToken cancellationToken = default);
}

/// <summary>
///     What a mint did, or found already done.
/// </summary>
/// <param name="Minted">
///     <see langword="true" /> when this call wrote the secret, <see langword="false" /> when one was
///     already there and was left alone. ⚠ The second is the ordinary outcome — a reconcile pass runs
///     over and over, and every pass after the first finds the credential it minted.
/// </param>
public readonly record struct SecretMint(bool Minted);

/// <summary>
///     Writes a secret into the vault, once. The half of the credential story
///     <see cref="ISecretResolver" /> cannot tell.
/// </summary>
/// <remarks>
///     <para>
///         <b>docs/plan/12 § The pattern, once, piece 5 — "credential provisioning into the tenant's
///         Vault" — is this interface.</b> That document's table used to assign piece 5 to
///         <c>ISecretResolver</c>, which reads and cannot provision anything, and the table is
///         corrected. A managed service whose password nobody can produce is not a managed service:
///         CloudNativePG happens to generate its own, so <c>CyberCloud.DBforPostgreSQL/servers</c>
///         merely could not hand the password out, but SeaweedFS has no such fallback and comes up
///         <i>anonymously administrable</i> without one.
///     </para>
///     <para>
///         ⚠ <b>MINT-ONCE, AND THAT IS THE WHOLE SEMANTIC RATHER THAN AN OPTIMISATION.</b> A
///         reconciler is idempotent by clause 1 and its reminder fires on a resource that already
///         converged, so a write that replaced what was there would hand the tenant a new credential
///         on every pass and break the one they are using. An implementation must therefore write only
///         when the path holds nothing, and report which happened. OpenBao's <c>kv-v2</c> spells that
///         <c>cas=0</c>; the check belongs at the vault rather than in a read-then-write here, because
///         two silos reconciling the same resource would both read "absent" and both write.
///     </para>
///     <para>
///         ⚠ <b>A path and a bag of fields, not a <see cref="SecretRef" />.</b> A ref addresses one
///         field so that a reconciler can resolve one value; a mint writes the whole document at a
///         path, because a credential is usually a pair and writing the two halves separately would
///         make a half-written credential representable. Resolve each field back with a
///         <see cref="SecretRef" /> naming the same path.
///     </para>
///     <para>
///         ⚠ <b>The value must not be written anywhere durable on the way in either.</b> The same rule
///         <see cref="ISecretResolver" /> carries, in the other direction: what a caller passes here
///         belongs in a local for the length of one pass, and in nothing that outlives it.
///     </para>
/// </remarks>
public interface ISecretWriter {
    /// <summary>Writes a secret at a path, unless one is already there.</summary>
    /// <param name="path">
    ///     The vault path, for example <c>tenants/{tenantId}/storage/main</c>. The same string a
    ///     <see cref="SecretRef.Path" /> reading it back carries.
    /// </param>
    /// <param name="fields">
    ///     The field names and values to write. ⚠ Refused when empty, because an empty document is a
    ///     path that exists and resolves to nothing — which every reader would report as a missing
    ///     field rather than as a mint that wrote nothing.
    /// </param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>
    ///     Whether this call wrote, or a failure. ⚠ Finding a secret already at the path is a
    ///     <b>success</b> carrying <c>Minted: false</c> — the caller's goal is that a credential
    ///     exists, and the second pass achieving it without writing is the correct outcome rather than
    ///     a conflict.
    /// </returns>
    Task<Result<SecretMint>> MintAsync(
        string path,
        IReadOnlyDictionary<string, string> fields,
        CancellationToken cancellationToken = default
    );
}

/// <summary>
///     One resource type's convergence logic. docs/plan/08 § The reconcile loop, implemented once per
///     resource type.
/// </summary>
/// <remarks>
///     <para>
///         <b>The four-clause contract, checked by <c>ReconcilerConformance</c>:</b>
///     </para>
///     <list type="number">
///         <item>
///             <b>Idempotent.</b> Called twice with identical inputs, the second call is
///             <see cref="ReconcileOutcome.Converged" /> and changes nothing. Not aspirational — the
///             reminder <i>will</i> fire on a grain that already converged, and the silo <i>will</i>
///             die between the apply and the state write.
///         </item>
///         <item>
///             <b>No hidden state.</b> Everything it needs comes from <see cref="ReconcileContext" />.
///             A reconciler with a field is a reconciler that breaks when the grain moves silo.
///         </item>
///         <item>
///             <b>Bounded.</b> Returns within 30 seconds or returns
///             <see cref="ReconcileOutcome.InProgress" />. A reconciler that blocks on a four-minute
///             cluster creation blocks that grain's turn, and Orleans grains are single-threaded.
///         </item>
///         <item>
///             <b>Observes, never assumes.</b> <see cref="ReconcileOutcome.Converged" /> means it
///             <i>read back</i> the desired shape, not that the apply returned 200.
///         </item>
///     </list>
///     <para>
///         ⚠ <b>A reconciler never calls the authorization engine, the quota grain or the index.</b>
///         docs/plan/07 § The enforcement seam: <i>"Providers never call the engine. A provider that
///         does is failing a review."</i> The same holds for steps 6 and 7 of docs/plan/08 § The write
///         path, end to end. This assembly's reference set is the mechanical half of that rule — a
///         reconciler cannot name <c>ICheckGrain</c> or <c>IQuotaGrain</c> because
///         <c>CyberCloud.ResourceManager.Contracts</c> does not reference the assemblies that declare
///         them.
///     </para>
///     <para>
///         ⚠ <b>Register the implementation as a singleton.</b> Clause 2 makes one instance per
///         process correct, and a transient registration would hide a field long enough for it to
///         reach production before the silo moved.
///     </para>
/// </remarks>
public interface IResourceReconciler {
    /// <summary>The one resource type this reconciler converges.</summary>
    /// <remarks>
    ///     ⚠ Must match a type the provider's <c>Describe</c> declared, and the registry checks it on
    ///     build. A reconciler whose <see cref="Type" /> names nothing is a reconciler that is never
    ///     called, which is a silent no-op rather than a startup failure unless something looks.
    /// </remarks>
    ResourceTypeName Type { get; }

    /// <summary>Moves the world one step towards <see cref="ReconcileContext.Desired" />.</summary>
    /// <param name="context">Everything this pass may know. See clause 2.</param>
    /// <param name="cancellationToken">
    ///     Cancelled when the operation is cancelled or the grain's turn budget runs out. ⚠
    ///     Cancellation here means "stop this pass", not "abandon the resource" — the delete path for
    ///     anything already applied runs afterwards, from <see cref="DeleteAsync" />.
    /// </param>
    /// <returns>
    ///     <see cref="ReconcileOutcome.Converged" /> only after reading the desired shape back.
    /// </returns>
    Task<ReconcileOutcome> ReconcileAsync(ReconcileContext context, CancellationToken cancellationToken = default);

    /// <summary>Tears the resource's data plane down.</summary>
    /// <param name="context">The same context the create path saw.</param>
    /// <param name="cancellationToken">Cancels this pass.</param>
    /// <returns>
    ///     <see cref="ReconcileOutcome.Converged" /> once the objects are <b>gone</b>, read back —
    ///     not once a delete was issued. ⚠ A failure here leaves the resource in
    ///     <c>Deleting</c> and <i>visible in listings</i>, per docs/plan/06 § Two-phase create, rather
    ///     than silently gone while its pods run and its meter ticks.
    /// </returns>
    Task<ReconcileOutcome> DeleteAsync(ReconcileContext context, CancellationToken cancellationToken = default);

    /// <summary>Reads the world as it currently is.</summary>
    /// <param name="context">The read-only context.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>
    ///     What is actually there. ⚠ Must not apply anything — this runs on the drift path too, where
    ///     a write would turn a diff into a change.
    /// </returns>
    Task<ObservedState> ObserveAsync(ObserveContext context, CancellationToken cancellationToken = default);
}

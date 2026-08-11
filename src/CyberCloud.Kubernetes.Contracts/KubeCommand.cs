using System.Text.Json;

namespace CyberCloud.Kubernetes.Contracts;

/// <summary>
///     One fully-labelled write to one cluster: what object, where, under which field manager, and
///     with the seven mandatory labels already injected.
/// </summary>
/// <remarks>
///     <para>
///         <b>This type is only reachable through the builder</b> — <see cref="For" /> returns
///         <see cref="IKubeCommandNeedsTenant" /> and <c>Build()</c> exists only on
///         <see cref="IKubeCommandBuilder" />, which is three method calls away. That is ADR-013's
///         "the builder makes omission impossible": there is no public constructor and no object
///         initializer, so a <see cref="KubeCommand" /> that reached a cluster without the labels is
///         not a thing that can be typed.
///     </para>
///     <para>
///         ⚠ <b>docs/plan/09 § The command builder declares <c>public static class KubeCommand</c>
///         and, four lines later, <c>KubeCommand Build();</c>.</b> Those cannot both be true — a
///         static class cannot be a return type. The repair, which preserves both spellings the
///         document uses, is for <c>KubeCommand</c> to be an ordinary sealed record carrying a
///         <see langword="static" /> <see cref="For" />: <c>KubeCommand.For(connection)</c> reads
///         exactly as written, and <c>Build()</c> has something to return.
///     </para>
/// </remarks>
[GenerateSerializer]
[Alias("CyberCloud.Kubernetes.KubeCommand")]
public sealed record KubeCommand
{
    // Internal so that only KubeCommandBuilder can mint one. A record's positional/init surface
    // would otherwise let a caller construct an unlabelled command directly, which is the exact
    // hole the type-state chain exists to close.
    internal KubeCommand()
    {
    }

    /// <summary>The tenant that owns the resource being written.</summary>
    [Id(0)]
    public Guid TenantId { get; init; }

    /// <summary>The subscription the resource bills to.</summary>
    [Id(1)]
    public Guid SubscriptionId { get; init; }

    /// <summary>The resource this object belongs to.</summary>
    [Id(2)]
    public Guid ResourceId { get; init; }

    /// <summary>The object being written.</summary>
    [Id(3)]
    public ObjectRef Target { get; init; } = new();

    /// <summary>
    ///     The complete object JSON, <b>with</b> the seven labels and two annotations already in
    ///     <c>metadata</c>. This is the body sent as an apply patch.
    /// </summary>
    [Id(4)]
    public string Body { get; init; } = "{}";

    /// <summary>
    ///     The field manager — <c>cybercloud/{provider}</c>. ADR-013: "a stable field manager per
    ///     provider … that gives us conflict detection for free".
    /// </summary>
    [Id(5)]
    public string FieldManager { get; init; } = string.Empty;

    /// <summary>The seven mandatory labels plus any extras, as emitted.</summary>
    [Id(6)]
    public IReadOnlyDictionary<string, string> Labels { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>The two mandatory annotations plus any extras, as emitted.</summary>
    [Id(7)]
    public IReadOnlyDictionary<string, string> Annotations { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>The <c>sha256:…</c> of the body before injection — the no-op detector.</summary>
    [Id(8)]
    public string ReconcileHash { get; init; } = string.Empty;

    /// <summary>
    ///     Whether the apply may take ownership of fields another manager holds.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Always <see langword="false" /> and there is no builder method to set it.</b> ADR-013
    ///     wants a tenant's hand-edit to produce "a conflict rather than silently reverting"; the
    ///     API server's <c>force=true</c> is precisely the switch that turns that conflict back into
    ///     a silent revert. It exists as a field because the wire shape has one and because a
    ///     deliberate, audited override may one day be a supported repair action — but it is not
    ///     reachable from the builder, so no reconciler can quietly opt into stomping a tenant.
    /// </remarks>
    [Id(9)]
    public bool Force { get; init; }

    /// <summary>The resource's address, carried for the drift event and the audit line.</summary>
    [Id(10)]
    public string ResourcePath { get; init; } = string.Empty;

    /// <summary>
    ///     Starts the type-state chain — ADR-013 and docs/plan/09 § The command builder.
    /// </summary>
    /// <param name="connection">The cluster to write to.</param>
    /// <param name="charts">
    ///     The chart renderer, when one is registered. <see langword="null" /> — the default — makes
    ///     <see cref="IKubeCommandBuilder.Chart" /> fail with a message naming
    ///     <c>CyberCloud.Kubernetes.Charts</c>. See the remarks on <see cref="IChartRenderer" />.
    /// </param>
    /// <returns>
    ///     A builder on which the <b>only</b> available method is
    ///     <see cref="IKubeCommandNeedsTenant.WithTenantId" />. There is no <c>Build()</c>, no
    ///     <c>ApplyAsync()</c> and no <c>DeleteAsync()</c> here, and that is the point: the unlabelled
    ///     case does not compile.
    /// </returns>
    public static IKubeCommandNeedsTenant For(
        IKubeClusterConnection connection,
        IChartRenderer? charts = null)
    {
        ArgumentNullException.ThrowIfNull(connection);
        return new KubeCommandBuilder(connection, charts);
    }
}

/// <summary>
///     Stage 1 of the type-state chain. The only thing you can do is name the tenant.
/// </summary>
/// <remarks>
///     ⚠ This interface deliberately has <b>one</b> member. Anything else on it — a convenience
///     <c>Build()</c>, an <c>InNamespace</c>, a <c>WithLabels</c> — would make some unlabelled
///     command expressible, and ADR-013's whole claim is that none is.
/// </remarks>
public interface IKubeCommandNeedsTenant
{
    /// <summary>Names the owning tenant and advances to stage 2.</summary>
    /// <param name="tenantId">The tenant. Becomes <c>cybercloud.io/tenant-id</c>.</param>
    IKubeCommandNeedsResource WithTenantId(Guid tenantId);
}

/// <summary>
///     Stage 2 of the type-state chain. The only thing you can do is name the resource.
/// </summary>
public interface IKubeCommandNeedsResource
{
    /// <summary>Names the resource and advances to the fully-qualified stage.</summary>
    /// <param name="resourceId">
    ///     The resource. Supplies <c>resource-id</c>, and by inference <c>subscription-id</c>,
    ///     <c>resource-group</c> and <c>resource-type</c>.
    /// </param>
    IKubeCommandBuilder WithResourceId(ResourceId resourceId);
}

/// <summary>
///     The fully-qualified stage — and the <b>only</b> place <c>Build</c>, <c>ApplyAsync</c> and
///     <c>DeleteAsync</c> exist.
/// </summary>
/// <remarks>
///     ADR-013: <i>"<c>Build()</c> and <c>Apply()</c> exist only on the fully-qualified interface, so
///     an unlabelled object does not compile."</i> That is a compile-time claim and it is proved as
///     one — <c>CompileFailureTests</c> compiles the illegal call with Roslyn and asserts the
///     diagnostic, rather than asserting an exception at run time.
/// </remarks>
public interface IKubeCommandBuilder
{
    /// <summary>Overrides the inferred subscription — for platform objects. docs/plan/09.</summary>
    /// <param name="id">The subscription to bill to.</param>
    IKubeCommandBuilder WithSubscriptionId(Guid id);

    /// <summary>Sets the namespace. Defaults to the resource's namespace.</summary>
    /// <param name="ns">The Kubernetes namespace.</param>
    IKubeCommandBuilder InNamespace(string ns);

    /// <summary>
    ///     Names the kind, including the <b>plural</b> the REST path needs.
    /// </summary>
    /// <param name="kind">The group, version, kind and plural.</param>
    /// <remarks>
    ///     ⚠ <b>Not in docs/plan/09's list, and required.</b> See the remarks on
    ///     <see cref="GroupVersionKind" />: every Kubernetes REST path is keyed by the plural
    ///     resource name, which is not derivable from the kind. Without this the builder would have
    ///     to guess, and a guess that works for ninety kinds and 404s on the ninety-first is worse
    ///     than a required argument.
    /// </remarks>
    IKubeCommandBuilder WithKind(GroupVersionKind kind);

    /// <summary>Adds labels. May not replace one of the seven — see the remarks.</summary>
    /// <param name="extra">The additional labels.</param>
    /// <exception cref="ArgumentException">
    ///     A key is one of <see cref="KubeLabels.Mandatory" />, or a key or value is not legal
    ///     Kubernetes label syntax. ADR-013 makes the seven "injected and non-overridable"; silently
    ///     ignoring an attempted override would leave the caller believing it took effect, so it is
    ///     loud. A mandatory-label override in code is a bug, and docs/plan/00 § Coding standards
    ///     puts bugs on the exception path.
    /// </exception>
    IKubeCommandBuilder WithLabels(params (string Key, string Value)[] extra);

    /// <summary>Adds annotations. May not replace the two mandatory ones.</summary>
    /// <param name="extra">The additional annotations.</param>
    /// <exception cref="ArgumentException">A key is mandatory, or the key is not legal syntax.</exception>
    IKubeCommandBuilder WithAnnotations(params (string Key, string Value)[] extra);

    /// <summary>Adds an owner reference, so deletion cascades. docs/plan/09 § The command builder.</summary>
    /// <param name="parent">The owning resource.</param>
    /// <param name="ownerKind">The owner's kind and plural.</param>
    /// <param name="ownerName">The owner object's name in the cluster.</param>
    /// <param name="ownerUid">The owner object's <c>metadata.uid</c>, which Kubernetes requires.</param>
    IKubeCommandBuilder WithOwner(
        ResourceId parent,
        GroupVersionKind ownerKind,
        string ownerName,
        string ownerUid);

    /// <summary>Overrides the field manager. Defaults to <c>cybercloud/{provider}</c>.</summary>
    /// <param name="manager">The field manager name.</param>
    IKubeCommandBuilder WithFieldManager(string manager);

    /// <summary>Sets the api-version label — the version the desired state was written at.</summary>
    /// <param name="apiVersion">For example <c>2026-08-01</c>.</param>
    IKubeCommandBuilder WithApiVersion(string apiVersion);

    /// <summary>
    ///     Renders a chart and applies the result. <b>The seam only</b> — see the remarks.
    /// </summary>
    /// <param name="chart">The chart name, for example <c>managed/postgres</c>.</param>
    /// <param name="values">The values document.</param>
    /// <remarks>
    ///     ⚠ <b>Not implemented here, on purpose.</b> docs/plan/03 § src puts Helm rendering in its
    ///     own assembly, <c>CyberCloud.Kubernetes.Charts</c>, which does not exist yet. This records
    ///     the request; if no <see cref="IChartRenderer" /> was supplied to <see cref="KubeCommand.For" />
    ///     the command fails to build with a message naming that assembly, rather than silently
    ///     applying nothing.
    /// </remarks>
    IKubeCommandBuilder Chart(string chart, JsonElement values);

    /// <summary>
    ///     Sets the object to apply, by serializing <paramref name="obj" /> to JSON.
    /// </summary>
    /// <typeparam name="T">
    ///     Anything serializable. ⚠ <b>Not</b> constrained to
    ///     <c>IKubernetesObject&lt;V1ObjectMeta&gt;</c> as docs/plan/09 § The command builder writes
    ///     it — that constraint would put <c>k8s.Models</c> in every caller's compile-time closure,
    ///     which docs/plan/03 § Assembly graph rules rule 3 forbids. See the remarks on
    ///     <see cref="KubeObject" />.
    /// </typeparam>
    /// <param name="obj">The object.</param>
    // ⚠ CA1720/CA1716 suppressed rather than obeyed: both want this renamed because `Object` is a
    // type name and a Visual Basic keyword. docs/plan/09 § The command builder spells the method
    // `Object<T>(T obj)`, and the value of a builder whose call sites read exactly as the design
    // document writes them is worth more here than cross-language override ergonomics on an
    // interface no other language will implement. The neighbouring ObjectJson(string) gives anyone
    // who dislikes the name an alternative.
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Naming", "CA1720:Identifier contains type name",
        Justification = "The name is docs/plan/09 § The command builder's, verbatim.")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Naming", "CA1716:Identifiers should not match keywords",
        Justification = "The name is docs/plan/09 § The command builder's, verbatim.")]
    IKubeCommandBuilder Object<T>(T obj)
        where T : notnull;

    /// <summary>Sets the object to apply, from JSON that is already built.</summary>
    /// <param name="json">A complete Kubernetes object document.</param>
    IKubeCommandBuilder ObjectJson(string json);

    /// <summary>
    ///     Builds the command, injecting the seven labels and two annotations.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    ///     The command is not buildable — see <see cref="TryBuild" /> for the same check without the
    ///     throw. This throws because reaching it means a reconciler was written wrong, and
    ///     docs/plan/00 § Coding standards keeps bugs on the exception path.
    /// </exception>
    KubeCommand Build();

    /// <summary>
    ///     <see cref="Build" /> as a <see cref="Result" />, for the paths that must not throw.
    /// </summary>
    Result<KubeCommand> TryBuild();

    /// <summary>Builds and applies, server-side, under the field manager.</summary>
    /// <param name="cancellationToken">The reconcile's token.</param>
    Task<Result<ApplyOutcome>> ApplyAsync(CancellationToken cancellationToken = default);

    /// <summary>Builds and deletes.</summary>
    /// <param name="policy">How to cascade.</param>
    /// <param name="cancellationToken">The reconcile's token.</param>
    Task<Result> DeleteAsync(
        CascadePolicy policy = CascadePolicy.Background,
        CancellationToken cancellationToken = default);
}

/// <summary>
///     The seam <c>CyberCloud.Kubernetes.Charts</c> fills — docs/plan/03 § src.
/// </summary>
/// <remarks>
///     <para>
///         docs/plan/09 § The command builder: charts are rendered <b>in-process</b> with a Helm
///         library and the resulting objects applied with server-side apply — no <c>HelmRelease</c>,
///         no Flux, because desired state must not live in the target cluster's etcd (ADR-001) and
///         the reconcile loop must be ours.
///     </para>
///     <para>
///         The interface is declared here, in the assembly the builder lives in, so that the
///         builder's shape is settled before the renderer exists. Nothing implements it yet.
///     </para>
/// </remarks>
public interface IChartRenderer
{
    /// <summary>Renders a chart to Kubernetes object documents.</summary>
    /// <param name="chart">The chart name.</param>
    /// <param name="values">The values document.</param>
    /// <param name="releaseNamespace">The namespace the release targets.</param>
    /// <param name="releaseName">The release name.</param>
    Result<IReadOnlyList<string>> Render(
        string chart,
        JsonElement values,
        string releaseNamespace,
        string releaseName);
}

namespace CyberCloud.ResourceManager.Contracts.Registry;

/// <summary>
///     One provider — a namespace and the resource types in it. docs/plan/08 § The provider registry.
/// </summary>
/// <remarks>
///     <para>
///         docs/plan/00 § 4. Everything a resource, everything a provider: every capability is a
///         resource type in a provider namespace, including platform administration, which
///         docs/plan/06 § Platform administration makes the <c>CyberCloud.Platform</c> provider rather
///         than a second API.
///     </para>
///     <para>
///         ⚠ <b><see cref="Describe" /> is called once, at silo start, and must be pure.</b> The
///         registry it builds is immutable afterwards and is read on every request. A
///         <c>Describe</c> that consulted configuration, a clock or a database would make the
///         platform's API surface depend on when the silo happened to start — and the generated CLI,
///         which runs <c>Describe</c> in a build step with none of those available, would describe a
///         different platform than the one running.
///     </para>
/// </remarks>
public interface IResourceProvider {
    /// <summary>
    ///     The provider namespace, for example <c>CyberCloud.DBforPostgreSQL</c>. Two or more
    ///     <c>.</c>-separated segments — see <see cref="ResourceTypeName" />.
    /// </summary>
    string ProviderNamespace { get; }

    /// <summary>Declares this provider's resource types.</summary>
    /// <param name="builder">The builder to declare into.</param>
    void Describe(IProviderBuilder builder);
}

/// <summary>
///     Where a provider declares its resource types. The entry point of the chain
///     docs/plan/08 § The provider registry shows.
/// </summary>
public interface IProviderBuilder {
    /// <summary>Begins a resource type.</summary>
    /// <param name="type">
    ///     The type path, for example <c>servers</c> or the nested <c>servers/databases</c>. Not the
    ///     namespace — that comes from <see cref="IResourceProvider.ProviderNamespace" />.
    /// </param>
    /// <returns>The type's builder, which is also where the next <c>ResourceType</c> is chained.</returns>
    IResourceTypeBuilder ResourceType(string type);
}

/// <summary>
///     One resource type under construction, and the point the next type chains from.
/// </summary>
/// <remarks>
///     ⚠ <b>This interface carries <see cref="IProviderBuilder.ResourceType" /> as well as the type-scoped methods,
///     and that is what makes docs/plan/08's example compile.</b> The document's <c>Describe</c> ends
///     one type's declaration by starting the next one on the same chain:
///     <code>
///     .ResourceType("servers")
///         .ApiVersion(…)
///         .Reconciler&lt;PostgresServerReconciler&gt;()
///     .ResourceType("servers/databases")
///         .ApiVersion(…);
///     </code>
///     A separate "finish this type" step would read better in isolation and would make that example
///     wrong, so the chain is flat and a type ends when the next one begins.
/// </remarks>
public interface IResourceTypeBuilder : IProviderBuilder {
    /// <summary>Declares an api-version and the body shape at it.</summary>
    /// <param name="version">
    ///     A <c>yyyy-MM-dd</c> date. ⚠ Immutable once published — adding a field is a <i>new</i>
    ///     version, per docs/plan/08 § The provider registry.
    /// </param>
    /// <param name="schema">
    ///     The body shape at that version. This is the object the write path validates against
    ///     <i>and</i> the object ADR-012's emitters read, which is the identity that makes drift
    ///     impossible rather than merely detectable.
    /// </param>
    /// <returns>The same builder.</returns>
    IResourceTypeBuilder ApiVersion(string version, ResourceSchema schema);

    /// <summary>Names the reconciler that converges this type.</summary>
    /// <typeparam name="TReconciler">
    ///     The implementation. Resolved from the container as a singleton — see the remarks on
    ///     <see cref="IResourceReconciler" /> for why a transient registration would hide a
    ///     clause-2 violation.
    /// </typeparam>
    /// <returns>The same builder.</returns>
    IResourceTypeBuilder Reconciler<TReconciler>()
        where TReconciler : IResourceReconciler;

    /// <summary>
    ///     Declares which quota meters a resource of this type draws on, one unit each.
    /// </summary>
    /// <param name="meters">The meters, from docs/plan/06 § Quota's per-family list.</param>
    /// <returns>The same builder.</returns>
    /// <remarks>
    ///     ⚠ <b>docs/plan/08 § The provider registry writes <c>Meter.VCpuHours</c>,
    ///     <c>Meter.StorageGbMonths</c> and <c>Meter.BackupGbMonths</c>, which are <i>billing</i>
    ///     meters and not the quota meters step 6 reserves against.</b> docs/plan/06 § Quota's
    ///     families are <c>vcpu</c>, <c>memoryGb</c>, <c>storageGb</c>, <c>publicIps</c>,
    ///     <c>clusters</c> and <c>resources</c>, which is what <see cref="QuotaMeter" /> implements
    ///     and what <c>IQuotaGrain.TryReserveAsync</c> takes. The two documents disagree; this takes
    ///     06's, because 06 owns the grain the write path actually calls and because a "GB-month" is
    ///     not a quantity a reservation can hold. Billing meters are docs/plan/22's and are a separate
    ///     declaration when they land.
    ///     <para>
    ///         Use <see cref="Meter(QuotaMeter, string, decimal)" /> when the amount comes from the
    ///         body rather than being one unit.
    ///     </para>
    /// </remarks>
    IResourceTypeBuilder Meters(params QuotaMeter[] meters);

    /// <summary>
    ///     Declares a quota meter whose amount is read out of the request body.
    /// </summary>
    /// <param name="meter">Which limit to draw against.</param>
    /// <param name="amountPointer">
    ///     An RFC 6901 pointer to a number in the body, for example <c>/properties/sku/vcpu</c>. ⚠ A
    ///     pointer rather than a delegate on purpose: a delegate validates and reserves but cannot be
    ///     <i>generated</i> from, and docs/plan/08 § The provider registry requires this one object to
    ///     do both.
    /// </param>
    /// <param name="fallback">
    ///     What to reserve when the pointer is absent from the body — for a property with a server
    ///     default.
    /// </param>
    /// <returns>The same builder.</returns>
    IResourceTypeBuilder Meter(QuotaMeter meter, string amountPointer, decimal fallback = 1m);

    /// <summary>
    ///     Names the ReBAC permissions the three verbs check.
    /// </summary>
    /// <param name="readPermission">The permission a <c>GET</c> needs. ⚠ Also what decides <c>404</c> versus <c>403</c>.</param>
    /// <param name="writePermission">The permission a <c>PUT</c> or <c>PATCH</c> needs.</param>
    /// <param name="deletePermission">The permission a <c>DELETE</c> needs.</param>
    /// <returns>The same builder.</returns>
    /// <remarks>
    ///     ⚠ <b><paramref name="readPermission" /> is load-bearing beyond reads.</b> docs/plan/07 § The
    ///     enforcement seam returns <c>404</c> to a caller who cannot read and <c>403</c> to one who
    ///     can read but cannot act, so a write that is refused consults this permission to decide
    ///     which. A type that declared no read permission would have to answer <c>404</c> to
    ///     everything, including to the caller who owns it.
    /// </remarks>
    IResourceTypeBuilder Permissions(string readPermission, string writePermission, string deletePermission);

    /// <summary>Declares an action — a <c>POST</c> on an existing resource.</summary>
    /// <param name="name">The action, for example <c>restart</c> or <c>listKeys</c>.</param>
    /// <param name="kind">How it is invoked.</param>
    /// <param name="permission">The ReBAC permission it needs, which is often not <c>write</c>.</param>
    /// <param name="secret">
    ///     Whether the response carries secret material. ⚠ A <c>secret: true</c> action is the only
    ///     path a secret value leaves by, is always audited, and its response is never cached.
    /// </param>
    /// <returns>The same builder.</returns>
    /// <remarks>
    ///     ⚠ An action never creates — docs/plan/08 § The write path, end to end. A <c>POST</c> to a
    ///     name that does not exist is a <c>404</c>, and that is checked by the manager rather than by
    ///     each action's handler.
    /// </remarks>
    IResourceTypeBuilder Action(string name, ActionKind kind, string permission, bool secret = false);

    /// <summary>Names the Helm chart this type renders.</summary>
    /// <param name="chart">
    ///     The chart, for example <c>managed/postgres</c>. Rendered in-process and applied with
    ///     server-side apply — docs/plan/09 § The command builder, "Why not <c>HelmRelease</c> and
    ///     Flux".
    /// </param>
    /// <returns>The same builder.</returns>
    IResourceTypeBuilder Chart(string chart);

    /// <summary>Declares that a deleted resource of this type is recoverable.</summary>
    /// <param name="days">
    ///     For how long. docs/plan/06 § Tags, locks gives 7 for types carrying data — <i>"a dropped
    ///     production database is not a support ticket you want to have to say no to"</i>.
    /// </param>
    /// <returns>The same builder.</returns>
    IResourceTypeBuilder SupportsSoftDelete(int days);

    /// <summary>Declares that this type carries tags.</summary>
    /// <returns>The same builder.</returns>
    /// <remarks>
    ///     At most 50 pairs, indexed into the resource-graph projection —
    ///     docs/plan/06 § Tags, locks. A type that does not declare this refuses a body with tags
    ///     rather than accepting and dropping them.
    /// </remarks>
    IResourceTypeBuilder SupportsTags();

    /// <summary>Declares that this type is placed into a cluster.</summary>
    /// <returns>The same builder.</returns>
    /// <remarks>
    ///     docs/plan/06 § The hierarchy makes a cluster a first-class resource that others are placed
    ///     into, so a managed Postgres has a required <c>clusterId</c>. A type that does not declare
    ///     this is a clusterless provider — a DNS zone, a mail domain, a role assignment — and its
    ///     <see cref="ReconcileContext.Cluster" /> is <see langword="null" />.
    /// </remarks>
    IResourceTypeBuilder RequiresCluster();
}

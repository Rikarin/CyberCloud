using CyberCloud.ResourceManager.Contracts.Registry;

namespace CyberCloud.ResourceManager.Registry;

/// <summary>
///     A builder that only wants to know which reconciler types a provider names.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The container has to know the reconcilers before the registry is built, and the
///         registry is built after the container is finished. This resolves that.</b>
///         <c>AddCyberCloudProvider</c> runs the provider's <c>Describe</c> a second time against
///         this collector, purely to learn the <c>Reconciler&lt;T&gt;()</c> calls so it can register
///         them. The real <c>Describe</c> — the one that produces the registrations — runs later,
///         when <see cref="IProviderRegistry" /> is first resolved.
///     </para>
///     <para>
///         Running <c>Describe</c> twice is safe because <see cref="IResourceProvider" /> requires it
///         to be pure (see its remarks), and this is the one place that requirement is cashed in. A
///         provider that read configuration in <c>Describe</c> could describe two different platforms
///         to the two calls, which is exactly why the requirement is stated on the interface rather
///         than assumed.
///     </para>
///     <para>
///         Every method other than <c>ResourceType</c>, <c>Reconciler</c> and <c>Action</c> is a
///         no-op here: this collector is not building a registration and has nothing to do with a
///         schema or a meter.
///     </para>
/// </remarks>
sealed class DiscoveringProviderBuilder : IResourceTypeBuilder {
    /// <summary>The reconciler types the provider named, in declaration order.</summary>
    public List<Type> Reconcilers { get; } = [];

    /// <summary>The action handler types the provider named, in declaration order.</summary>
    /// <remarks>
    ///     ⚠ Collected for the same reason the reconcilers are, and the consequence of forgetting is
    ///     worse: a reconciler nothing registered fails the whole provision with a message naming the
    ///     type, while an unregistered handler fails only the one action — and the one action most
    ///     likely to have one is <c>listKeys</c>, whose absence a tenant meets as "this service has no
    ///     credentials" rather than as a wiring error.
    /// </remarks>
    public List<Type> Handlers { get; } = [];

    /// <inheritdoc />
    public IResourceTypeBuilder ResourceType(string type) => this;

    /// <inheritdoc />
    public IResourceTypeBuilder ApiVersion(string version, ResourceSchema schema) => this;

    /// <inheritdoc />
    public IResourceTypeBuilder Reconciler<TReconciler>()
        where TReconciler : IResourceReconciler {
        if (!Reconcilers.Contains(typeof(TReconciler))) {
            Reconcilers.Add(typeof(TReconciler));
        }

        return this;
    }

    /// <inheritdoc />
    public IResourceTypeBuilder Meters(params QuotaMeter[] meters) => this;

    /// <inheritdoc />
    public IResourceTypeBuilder Meter(QuotaMeter meter, string amountPointer, decimal? fallback = null) => this;

    /// <inheritdoc />
    public IResourceTypeBuilder Meter(QuotaMeter meter, MeterDerivation derivation) => this;

    /// <inheritdoc />
    public IResourceTypeBuilder Permissions(string read, string write, string delete) => this;

    /// <inheritdoc />
    public IResourceTypeBuilder Action(
        string name,
        ActionKind kind,
        string permission,
        bool secret = false,
        ResourceSchema? request = null,
        ResourceSchema? response = null,
        bool longRunning = false,
        Type? handler = null
    ) {
        if (handler is not null && !Handlers.Contains(handler)) {
            Handlers.Add(handler);
        }

        return this;
    }

    /// <inheritdoc />
    public IResourceTypeBuilder Display(string name, string plural, string shortName = "", string summary = "") => this;

    /// <inheritdoc />
    public IResourceTypeBuilder Chart(string chart) => this;

    /// <inheritdoc />
    public IResourceTypeBuilder SupportsSoftDelete(int days) => this;

    /// <inheritdoc />
    public IResourceTypeBuilder SupportsTags() => this;

    /// <inheritdoc />
    public IResourceTypeBuilder RequiresCluster(string clusterIdPointer = ClusterPlacement.DefaultPointer) => this;
}

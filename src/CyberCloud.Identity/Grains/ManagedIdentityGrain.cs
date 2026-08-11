using CyberCloud.Core.Contracts;
using CyberCloud.Core.Time;

namespace CyberCloud.Identity.Grains;

/// <summary>
///     <see cref="IManagedIdentityGrain" /> — Entity, Durable, key <c>mi/{managedIdentityId:N}</c>.
///     docs/plan/11 § Managed identity.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>THE ORDERING IN <see cref="ExchangeAsync" /> IS THE SECURITY PROPERTY.</b> The issuer
///         comes from this grain's own state, the signature is checked against <i>that</i> issuer's
///         key set, and only then is the verified service account compared to the binding. Nothing
///         about the presented token selects which keys are trusted, and nothing outside this grain
///         can hand it a "already validated" value — which is why
///         <see cref="IManagedIdentityGrain.ExchangeAsync" /> takes the raw token rather than a
///         <see cref="ValidatedServiceAccount" />.
///     </para>
///     <para>
///         ⚠ <b>Every exchange failure is the same sentence.</b> The caller is an unauthenticated
///         workload, so "no such identity", "not bound", "bound to a different namespace" and "bad
///         signature" must be indistinguishable — otherwise the token endpoint enumerates a tenant's
///         managed identities and their bindings. This is the same rule, for the same reason, as
///         <c>UniformFailures.SignIn</c>.
///     </para>
/// </remarks>
public sealed class ManagedIdentityGrain(
    [PersistentState("managedIdentity", StorageTiers.Durable)] IPersistentState<ManagedIdentityGrainState> state,
    IClusterOidcDiscovery discovery,
    IProjectedTokenValidator validator,
    IClock clock
)
    : Grain, IManagedIdentityGrain {
    Guid managedIdentityId;
    Guid tenantId;

    /// <inheritdoc />
    public override Task OnActivateAsync(CancellationToken cancellationToken) {
        tenantId = IdentityGrainKeys.TenantOf(this);
        managedIdentityId = IdentityGrainKeys.Decode(this, GrainKeyKind.ManagedIdentity).Id;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task<Result<ManagedIdentityDescriptor>> CreateAsync(string name) {
        if (state.State.Descriptor is not null) {
            return Result<ManagedIdentityDescriptor>.Failure(
                ErrorCode.Conflict,
                $"Managed identity {managedIdentityId:D} already exists."
            );
        }

        // ⚠ The same naming rule as every other resource, because docs/plan/11 § Managed identity
        // step 1 creates one as `CyberCloud.ManagedIdentity/userAssignedIdentities/app-prod` — a
        // resource path — and a name this grain accepted but the resource manager would not is a
        // grain that cannot be reached through the API that owns it.
        if (ResourceNaming.Validate(name, "managed identity name").TryGetError(out var nameError)) {
            return Result<ManagedIdentityDescriptor>.Failure(nameError);
        }

        state.State.Descriptor = new() {
            ManagedIdentityId = managedIdentityId,
            TenantId = tenantId,
            Name = name,
            CreatedAt = clock.UtcNow
        };

        await state.WriteStateAsync();
        return Result<ManagedIdentityDescriptor>.Success(state.State.Descriptor);
    }

    /// <inheritdoc />
    public Task<Result<ManagedIdentityDescriptor>> GetAsync() =>
        Task.FromResult(
            state.State.Descriptor is { } descriptor
                ? Result<ManagedIdentityDescriptor>.Success(descriptor)
                : NotFound()
        );

    /// <inheritdoc />
    public async Task<Result<ManagedIdentityDescriptor>> BindAsync(
        WorkloadBinding binding,
        string clusterIssuerUrl
    ) {
        ArgumentNullException.ThrowIfNull(binding);

        if (state.State.Descriptor is not { } descriptor) {
            return NotFound();
        }

        var valid = WorkloadBinding.Create(binding.ClusterId, binding.Namespace, binding.ServiceAccount);
        if (valid.TryGetError(out var bindingError)) {
            return Result<ManagedIdentityDescriptor>.Failure(bindingError);
        }

        // ⚠ THE BINDING-TIME REACHABILITY CHECK, AND IT IS THE POINT OF THIS METHOD. docs/plan/11
        // § Managed identity: the flow "requires the tenant's cluster to expose a publicly reachable
        // OIDC discovery document, or that we fetch the JWKS through the AgentInitiated tunnel — for
        // BYO clusters that is not automatic, and the portal must say so AT BINDING TIME rather than
        // failing at token exchange."
        //
        // Nothing forces this order except this line, and reversing it is the tempting optimisation:
        // record the binding, read the issuer lazily on first exchange. That would make every BYO
        // cluster bind cleanly and fail in production, weeks later, to a workload that has no idea
        // what a discovery document is.
        var issuer = await discovery.DiscoverAsync(clusterIssuerUrl, CancellationToken.None);
        if (issuer.TryGetError(out var issuerError)) {
            return Result<ManagedIdentityDescriptor>.Failure(issuerError);
        }

        state.State.Descriptor = descriptor with {
            Binding = valid.GetValueOrThrow(),
            Issuer = issuer.GetValueOrThrow(),
            BoundAt = clock.UtcNow
        };

        await state.WriteStateAsync();
        return Result<ManagedIdentityDescriptor>.Success(state.State.Descriptor);
    }

    /// <inheritdoc />
    public async Task<Result<ManagedIdentityDescriptor>> UnbindAsync() {
        if (state.State.Descriptor is not { } descriptor) {
            return NotFound();
        }

        // ⚠ The issuer record goes with the binding. Keeping it "in case they rebind" would leave a
        // trusted key set attached to an identity that is bound to nothing, and the next bind would
        // silently inherit it — including if the next bind names a different cluster.
        state.State.Descriptor = descriptor with {
            Binding = WorkloadBinding.None,
            Issuer = ClusterOidcIssuer.None,
            BoundAt = default
        };

        await state.WriteStateAsync();
        return Result<ManagedIdentityDescriptor>.Success(state.State.Descriptor);
    }

    /// <inheritdoc />
    public async Task<Result<ClusterOidcIssuer>> RefreshIssuerAsync() {
        if (state.State.Descriptor is not { } descriptor) {
            return Result<ClusterOidcIssuer>.Failure(
                ErrorCode.ResourceNotFound,
                $"Managed identity {managedIdentityId:D} does not exist."
            );
        }

        if (descriptor.Binding.IsEmpty || descriptor.Issuer.IsEmpty) {
            return Result<ClusterOidcIssuer>.Failure(
                ErrorCode.Conflict,
                $"Managed identity {managedIdentityId:D} is not bound to a workload, so there is no "
                + "cluster issuer to refresh. Bind it first — docs/plan/11 § Managed identity, step 2."
            );
        }

        // ⚠ The URL is the one already recorded, never a caller-supplied one. A refresh that took an
        // issuer URL would be a rebind wearing a refresh's name: it could repoint an identity at a
        // cluster the tenant never approved, without going through the reachability check's audit.
        var reread = await discovery.DiscoverAsync(descriptor.Issuer.Issuer, CancellationToken.None);
        if (reread.TryGetError(out var error)) {
            return Result<ClusterOidcIssuer>.Failure(error);
        }

        state.State.Descriptor = descriptor with { Issuer = reread.GetValueOrThrow() };
        await state.WriteStateAsync();

        return Result<ClusterOidcIssuer>.Success(state.State.Descriptor.Issuer);
    }

    /// <inheritdoc />
    public Task<Result<ExchangedSubject>> ExchangeAsync(string subjectToken, string subjectTokenType) {
        // ⚠ Read every branch below as producing the SAME answer. The reasons differ and are worth
        // writing down next to the branch; what the caller learns is one sentence.
        if (!string.Equals(subjectTokenType, TokenExchange.JwtSubjectTokenType, StringComparison.Ordinal)) {
            return Rejected();
        }

        // The deleted-identity and deleted-binding cases. ⚠ Both must land here rather than earlier:
        // a caller that could tell "no such identity" from "not bound" could map a tenant's identities
        // by trying GUIDs against an endpoint that needs no credential to reach.
        if (state.State.Descriptor is not { } descriptor || !descriptor.IsExchangeable) {
            return Rejected();
        }

        // The trust anchor is this grain's state and nothing in the presented token.
        var validated = validator.Validate(subjectToken, descriptor.Issuer, clock.UtcNow);
        if (!validated.TryGetValue(out var account)) {
            return Rejected();
        }

        // ⚠ Ordinal on both halves. Kubernetes namespaces and service-account names are DNS-1123
        // labels, which are already lower-case, so a case-insensitive comparison could only ever
        // accept something Kubernetes itself would call a different account.
        if (!string.Equals(account.Namespace, descriptor.Binding.Namespace, StringComparison.Ordinal)
            || !string.Equals(account.ServiceAccount, descriptor.Binding.ServiceAccount, StringComparison.Ordinal)) {
            return Rejected();
        }

        return Task.FromResult(
            Result<ExchangedSubject>.Success(
                new() {
                    TenantId = tenantId,
                    ManagedIdentityId = managedIdentityId,
                    SubjectType = SubjectTypes.ManagedIdentity,
                    SubjectId = managedIdentityId.ToString("N"),
                    SubjectTokenExpiresAt = account.ExpiresAt
                }
            )
        );
    }

    /// <inheritdoc />
    public async Task<Result> DeleteAsync() {
        if (state.State.Descriptor is null) {
            return Result.Failure(
                ErrorCode.ResourceNotFound,
                $"Managed identity {managedIdentityId:D} does not exist."
            );
        }

        state.State.Descriptor = null;
        await state.WriteStateAsync();

        return Result.Success;
    }

    /// <inheritdoc />
    public Task DeactivateAsync() {
        DeactivateOnIdle();
        return Task.CompletedTask;
    }

    static Task<Result<ExchangedSubject>> Rejected() =>
        Task.FromResult(ManagedIdentityFailures.RejectExchange());

    Result<ManagedIdentityDescriptor> NotFound() =>
        Result<ManagedIdentityDescriptor>.Failure(
            ErrorCode.ResourceNotFound,
            $"Managed identity {managedIdentityId:D} does not exist."
        );
}

using CyberCloud.Core.Contracts;
using CyberCloud.Core.Time;

namespace CyberCloud.Identity.Grains;

/// <summary>
///     <see cref="IServicePrincipalGrain" /> — Entity, Durable, key <c>sp/{servicePrincipalId:N}</c>.
/// </summary>
/// <remarks>
///     ⚠ <b>The credential is a <see cref="SecretRef" />, never a value.</b> That is the whole
///     difference between this and the "client secret in a Kubernetes <c>Secret</c>" that
///     docs/plan/11 § Managed identity calls the bad answer — the platform's own copy is a handle,
///     so a durable-tier backup carries no credential.
/// </remarks>
public sealed class ServicePrincipalGrain(
    [PersistentState("servicePrincipal", StorageTiers.Durable)] IPersistentState<ServicePrincipalGrainState> state,
    IClock clock
)
    : Grain, IServicePrincipalGrain {
    Guid servicePrincipalId;
    Guid tenantId;

    /// <inheritdoc />
    public override Task OnActivateAsync(CancellationToken cancellationToken) {
        tenantId = IdentityGrainKeys.TenantOf(this);
        servicePrincipalId = IdentityGrainKeys.Decode(this, GrainKeyKind.ServicePrincipal).Id;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task<Result<ServicePrincipalDescriptor>> CreateAsync(ServicePrincipalDescriptor descriptor) {
        ArgumentNullException.ThrowIfNull(descriptor);

        if (state.State.Descriptor is not null) {
            return Result<ServicePrincipalDescriptor>.Failure(
                ErrorCode.Conflict,
                $"Service principal {servicePrincipalId:D} already exists."
            );
        }

        state.State.Descriptor = descriptor with {
            ServicePrincipalId = servicePrincipalId,
            TenantId = tenantId,
            CreatedAt = clock.UtcNow
        };

        await state.WriteStateAsync();
        return Result<ServicePrincipalDescriptor>.Success(state.State.Descriptor);
    }

    /// <inheritdoc />
    public Task<Result<ServicePrincipalDescriptor>> GetAsync() =>
        Task.FromResult(
            state.State.Descriptor is { } descriptor
                ? Result<ServicePrincipalDescriptor>.Success(descriptor)
                : NotFound()
        );

    /// <inheritdoc />
    public async Task<Result<ServicePrincipalDescriptor>> SetEnabledAsync(bool enabled) {
        if (state.State.Descriptor is not { } descriptor) {
            return NotFound();
        }

        state.State.Descriptor = descriptor with { Enabled = enabled };
        await state.WriteStateAsync();

        return Result<ServicePrincipalDescriptor>.Success(state.State.Descriptor);
    }

    /// <inheritdoc />
    public async Task<Result<ServicePrincipalDescriptor>> RotateCredentialAsync(SecretRef credentialSecretRef) {
        ArgumentNullException.ThrowIfNull(credentialSecretRef);

        if (state.State.Descriptor is not { } descriptor) {
            return NotFound();
        }

        if (credentialSecretRef.IsEmpty) {
            return Result<ServicePrincipalDescriptor>.Failure(
                ErrorCode.InvalidRequestBody,
                "Rotation needs a vault handle for the new secret. Write the secret to the vault "
                + "first — a crash between the two writes then leaves an unused secret rather than a "
                + "principal pointing at nothing."
            );
        }

        state.State.Descriptor = descriptor with { CredentialSecretRef = credentialSecretRef };
        await state.WriteStateAsync();

        return Result<ServicePrincipalDescriptor>.Success(state.State.Descriptor);
    }

    /// <inheritdoc />
    public async Task<Result> DeleteAsync() {
        if (state.State.Descriptor is null) {
            return Result.Failure(
                ErrorCode.ResourceNotFound,
                $"Service principal {servicePrincipalId:D} does not exist."
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

    Result<ServicePrincipalDescriptor> NotFound() =>
        Result<ServicePrincipalDescriptor>.Failure(
            ErrorCode.ResourceNotFound,
            $"Service principal {servicePrincipalId:D} does not exist."
        );
}

using CyberCloud.Core;
using CyberCloud.Core.Contracts;
using CyberCloud.Identity.Contracts;
using CyberCloud.Identity.Tests.Infrastructure;
using System.Reflection;

namespace CyberCloud.Identity.Tests;

/// <summary>
///     docs/plan/11 § Managed identity calls "a client secret in a Kubernetes <c>Secret</c>" the bad
///     answer, and a service principal is the platform's own version of the same shape. What keeps it
///     from being the same mistake is that the platform stores a <see cref="SecretRef" /> — a handle —
///     and never a value.
/// </summary>
/// <remarks>
///     ⚠ <b>"The credential is a handle" is a property of the durable state, not of an intention.</b>
///     The first test below is structural for that reason: it fails when somebody adds a field that
///     could hold the secret itself, which is the change that would make a durable-tier backup carry
///     every tenant's client secrets.
/// </remarks>
[Collection(IdentitySuite.Name)]
public sealed class ServicePrincipalCredentialTests(IdentityCluster cluster) {
    static readonly SecretRef Handle = new() { Path = "tenants/x/sp/ci", Field = "secret" };

    static ServicePrincipalDescriptor Valid() =>
        new() {
            DisplayName = "CI",
            ApplicationId = Guid.NewGuid(),
            CredentialSecretRef = Handle
        };

    [Fact]
    public void TheDescriptorHasNowhereToPutASecretValue() {
        // ⚠ THE STRUCTURAL ASSERTION. Every string-ish member of the wire type is named here, and a
        // new one whose name reads like a credential fails this rather than being noticed in review.
        // The escape hatch is deliberate and narrow: a SecretRef is a handle, and CertificateThumbprints
        // holds public thumbprints.
        var suspicious = typeof(ServicePrincipalDescriptor)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(x => x.PropertyType == typeof(string) || x.PropertyType == typeof(List<string>))
            .Where(x => x.Name.Contains("Secret", StringComparison.OrdinalIgnoreCase)
                || x.Name.Contains("Password", StringComparison.OrdinalIgnoreCase)
                || x.Name.Contains("Credential", StringComparison.OrdinalIgnoreCase)
                || x.Name.Contains("Key", StringComparison.OrdinalIgnoreCase)
                || x.Name.Contains("Token", StringComparison.OrdinalIgnoreCase))
            .Select(x => x.Name)
            .ToList();

        suspicious.ShouldBeEmpty(
            "a service principal's credential is a SecretRef and never a value — that is the whole "
            + "difference between this and the client secret in a Kubernetes Secret that "
            + "docs/plan/11 § Managed identity calls the bad answer. A string-typed member holding "
            + "the secret would put every tenant's client secret in a durable-tier backup."
        );
    }

    [Fact]
    public async Task RotationRefusesAnEmptyHandleAndLeavesTheOldOneInPlace() {
        var principal = cluster.ServicePrincipal(Guid.NewGuid());
        (await principal.CreateAsync(Valid())).IsSuccess.ShouldBeTrue();

        // An address with no path or no field resolves to nothing. Accepting it would leave a
        // principal pointing at a secret that does not exist, which fails at authentication time
        // rather than here.
        foreach (var empty in new[] {
            new SecretRef(),
            new SecretRef { Path = "tenants/x/sp/ci" },
            new SecretRef { Field = "secret" }
        }) {
            var rotated = await principal.RotateCredentialAsync(empty);

            rotated.IsSuccess.ShouldBeFalse($"'{empty}' is an address that resolves to nothing");
            rotated.Error!.Code.ShouldBe(ErrorCode.InvalidRequestBody);
        }

        (await principal.GetAsync()).GetValueOrThrow().CredentialSecretRef.ShouldBe(Handle);
    }

    [Fact]
    public async Task RotationReplacesTheHandleAndChangesNothingElse() {
        var principal = cluster.ServicePrincipal(Guid.NewGuid());
        var created = (await principal.CreateAsync(Valid())).GetValueOrThrow();

        var next = new SecretRef { Path = "tenants/x/sp/ci", Field = "secret", Version = "2" };
        var rotated = (await principal.RotateCredentialAsync(next)).GetValueOrThrow();

        rotated.CredentialSecretRef.ShouldBe(next);
        rotated.ServicePrincipalId.ShouldBe(created.ServicePrincipalId);
        rotated.ApplicationId.ShouldBe(created.ApplicationId);
        rotated.DisplayName.ShouldBe(created.DisplayName);
        rotated.Enabled.ShouldBe(created.Enabled);
        rotated.CreatedAt.ShouldBe(created.CreatedAt);
    }

    [Fact]
    public async Task DisablingIsSeparateFromDeletingAndIsReversible() {
        var principal = cluster.ServicePrincipal(Guid.NewGuid());
        (await principal.CreateAsync(Valid())).GetValueOrThrow().Enabled.ShouldBeTrue();

        (await principal.SetEnabledAsync(false)).GetValueOrThrow().Enabled.ShouldBeFalse();

        // ⚠ Disabling keeps the record. An audit trail that lost the principal when someone stopped
        // it authenticating would lose the name attached to everything it had already done.
        var disabled = (await principal.GetAsync()).GetValueOrThrow();
        disabled.Enabled.ShouldBeFalse();
        disabled.CredentialSecretRef.ShouldBe(Handle);

        (await principal.SetEnabledAsync(true)).GetValueOrThrow().Enabled.ShouldBeTrue();
    }

    [Fact]
    public async Task TheIdentityComesFromTheKeyAndNotFromTheBody() {
        var servicePrincipalId = Guid.NewGuid();

        var created = await cluster
            .ServicePrincipal(servicePrincipalId)
            .CreateAsync(Valid() with { ServicePrincipalId = Guid.NewGuid(), TenantId = Guid.NewGuid() });

        var descriptor = created.GetValueOrThrow();
        descriptor.ServicePrincipalId.ShouldBe(servicePrincipalId);
        descriptor.TenantId.ShouldBe(IdentityCluster.Tenant);
    }

    [Fact]
    public async Task EveryOperationOnAPrincipalThatDoesNotExistIsNotFound() {
        var principal = cluster.ServicePrincipal(Guid.NewGuid());

        foreach (var (name, result) in new (string, Result)[] {
            ("Get", (await principal.GetAsync()).ToResult()),
            ("SetEnabled", (await principal.SetEnabledAsync(false)).ToResult()),
            ("RotateCredential", (await principal.RotateCredentialAsync(Handle)).ToResult()),
            ("Delete", await principal.DeleteAsync())
        }) {
            result.IsSuccess.ShouldBeFalse($"{name} on a principal that was never created");
            result.Error!.Code.ShouldBe(
                ErrorCode.ResourceNotFound,
                $"{name} must say the principal does not exist rather than create one implicitly"
            );
        }
    }

    [Fact]
    public async Task CreatingTwiceIsAConflict() {
        var principal = cluster.ServicePrincipal(Guid.NewGuid());
        (await principal.CreateAsync(Valid() with { DisplayName = "CI" })).IsSuccess.ShouldBeTrue();

        var again = await principal.CreateAsync(Valid() with { DisplayName = "Not CI" });
        again.IsSuccess.ShouldBeFalse();
        again.Error!.Code.ShouldBe(ErrorCode.Conflict);

        (await principal.GetAsync()).GetValueOrThrow().DisplayName.ShouldBe("CI");
    }

    [Fact]
    public async Task APrincipalIsNotVisibleToAnotherTenant() {
        var servicePrincipalId = Guid.NewGuid();

        (await cluster.ServicePrincipal(servicePrincipalId).CreateAsync(Valid())).IsSuccess.ShouldBeTrue();

        (await cluster.ServicePrincipal(servicePrincipalId, IdentityCluster.OtherTenant).GetAsync())
            .IsSuccess
            .ShouldBeFalse(
                "a service principal GUID is unique within a tenant, and the credential handle it "
                + "carries must not be readable by a tenant that guessed the GUID"
            );
    }
}

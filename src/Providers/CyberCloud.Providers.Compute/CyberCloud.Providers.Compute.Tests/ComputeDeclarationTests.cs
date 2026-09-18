using CyberCloud.ResourceManager.Contracts.Generation;
using CyberCloud.ResourceManager.Registry;
using System.Text.Json;

namespace CyberCloud.Providers.Compute.Tests;

/// <summary>What the provider declares, checked the way a silo checks it at start.</summary>
public sealed class ComputeDeclarationTests {
    [Fact]
    public void TheProviderBuildsIntoARegistryTheSiloWouldAccept() {
        var registry = ProviderRegistry.Build([new ComputeProvider()]);

        registry.TryGetType(VirtualMachines.Type, out var machine).ShouldBeTrue();
        machine.ReconcilerType.ShouldBe(typeof(VirtualMachineReconciler));
        machine.ReadPermission.ShouldBe("read");
        machine.ClusterIdPointer.ShouldBe(VirtualMachines.ClusterIdPointer);

        registry.TryGetType(Disks.Type, out var disk).ShouldBeTrue();
        disk.ReconcilerType.ShouldBe(typeof(DiskReconciler));

        registry.TryGetType(Images.Type, out var image).ShouldBeTrue();
        image.ReconcilerType.ShouldBe(typeof(ImageReconciler));

        registry.Types.Length.ShouldBe(3);
    }

    [Fact]
    public void ThreeRootTypesAndNoChild() {
        // ⚠ A disk looks like a machine's child and is not one: a managed disk outlives the machine,
        // and a child shares its parent's lifetime by construction — ComputeProvider says so.
        VirtualMachines.Type.Depth.ShouldBe(1);
        Disks.Type.Depth.ShouldBe(1);
        Images.Type.Depth.ShouldBe(1);

        VirtualMachines.Type.ToString().ShouldBe("CyberCloud.Compute/virtualMachines");
        Disks.Type.ToString().ShouldBe("CyberCloud.Compute/disks");
        Images.Type.ToString().ShouldBe("CyberCloud.Compute/images");
    }

    [Fact]
    public void NoShortNameHereGivesACycTokenTwoMeanings() {
        // Derived rather than listed, for the reason ManagedClusterDeclarationTests gives: the group
        // key is `compute`, the command names are the kebab type paths, and the three aliases must
        // stay clear of all of them and of each other.
        CliTokens.Collisions(
            ProviderRegistry.Build([new ComputeProvider()])
                .Types.Select(static x => new CliDeclaration(x.Type.Namespace, x.Type.Type, x.Display.Alias))
        )
            .ShouldBeEmpty();

        ComputeProvider.MachineShortName.ShouldBe("vm");
    }

    // ── The meters ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheMachineDrawsWhatTheGuestGetsAndTheRootDiskOnly() {
        using var body = JsonDocument.Parse(
            VirtualMachines.Body(Compute.ClusterId, size: "s1.medium", osDiskSize: "40Gi", dataDisks: ["data"])
        );

        var drawn = Derived(VirtualMachines.Type).ToDictionary(
            static x => x.Meter,
            x => x.Derivation!.Amount(body.RootElement).GetValueOrThrow()
        );

        drawn[QuotaMeter.Vcpu].ShouldBe(2, "the size's cores reach quota and the guest alike");
        drawn[QuotaMeter.MemoryGb].ShouldBe(8);
        drawn[QuotaMeter.StorageGb].ShouldBe(
            40,
            "a data disk is metered on its own type; summing it here would reserve the same gibibytes twice"
        );

        Registration(VirtualMachines.Type).Meters.Select(static x => x.Meter).ShouldContain(QuotaMeter.Resources);
        Registration(VirtualMachines.Type).Meters.Select(static x => x.Meter).ShouldNotContain(QuotaMeter.Clusters);
    }

    [Fact]
    public void ADiskAndAnImageDrawTheirSizeAndOneResource() {
        using var disk = JsonDocument.Parse(Disks.Body(Compute.ClusterId, "64Gi"));
        using var image = JsonDocument.Parse(Images.Body(Compute.ClusterId, size: "10Gi"));

        Derived(Disks.Type).Single().Derivation!.Amount(disk.RootElement).GetValueOrThrow().ShouldBe(64);
        Derived(Images.Type).Single().Derivation!.Amount(image.RootElement).GetValueOrThrow().ShouldBe(10);

        foreach (var type in new[] { Disks.Type, Images.Type }) {
            Registration(type).Meters.Select(static x => x.Meter).ShouldContain(QuotaMeter.Resources);
            Registration(type).Meters.Select(static x => x.Meter)
                .ShouldNotContain(QuotaMeter.Vcpu, "a disk runs nothing");
        }
    }

    [Fact]
    public void EveryDerivedMeterSaysWhatItReads() {
        // MeterDerivation.Reads is what the generated document publishes as a meter's inputs, and the
        // derivation is a delegate, so no gate can infer them.
        foreach (var meter in Derived(VirtualMachines.Type)) {
            meter.Derivation!.Expression.ShouldNotBeNullOrWhiteSpace(meter.Meter.ToString());
            meter.Derivation!.Reads.ShouldContain(
                meter.Meter == QuotaMeter.StorageGb ? "/properties/osDiskSize" : "/properties/size",
                meter.Meter.ToString()
            );
        }

        Derived(Disks.Type).Single().Derivation!.Reads.ShouldBe(["/properties/size"]);
        Derived(Images.Type).Single().Derivation!.Reads.ShouldBe(["/properties/size"]);
    }

    [Fact]
    public void ASizeOutsideTheClosedSetIsRefusedByTheSchemaAndReservesNothing() {
        // ⚠ Two halves. The schema refuses the write; and were a body to reach the derivation anyway,
        // it refuses to reserve zero — a resource provisioned against no quota is one nobody is
        // charged for.
        using var body = JsonDocument.Parse(VirtualMachines.Body(Compute.ClusterId, size: "s1.nano"));

        VirtualMachines.Schema2026.Validate(body.RootElement, allowTags: true).IsFailure.ShouldBeTrue();
        Derived(VirtualMachines.Type).First(static x => x.Meter == QuotaMeter.Vcpu).Derivation!
            .Amount(body.RootElement)
            .IsFailure.ShouldBeTrue();
    }

    // ── The schema ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void EveryDeclaredDefaultIsAValueTheApiWouldAccept() {
        foreach (var (schema, body) in new[] {
                     (VirtualMachines.Schema2026, VirtualMachines.Body(Compute.ClusterId)),
                     (Disks.Schema2026, Disks.Body(Compute.ClusterId)),
                     (Images.Schema2026, Images.Body(Compute.ClusterId)),
                     (Images.Schema2026,
                         Images.Body(
                             Compute.ClusterId,
                             Images.UrlSource,
                             url: "docker://quay.io/kubevirt/cirros-container-disk-demo:v1.9.0"
                         )),
                     (VirtualMachines.Schema2026,
                         VirtualMachines.Body(
                             Compute.ClusterId,
                             dataDisks: ["data"],
                             virtualNetwork: "vnet",
                             subnet: "web",
                             cloudInit: Compute.VaultPath("web") + "#userdata@3"
                         ))
                 }) {
            using var document = JsonDocument.Parse(body);
            var validated = schema.Validate(document.RootElement, allowTags: true);
            validated.IsSuccess.ShouldBeTrue(validated.Error?.Message);
        }
    }

    [Fact]
    public void NoBodyPropertyIsDeclaredSecretAndCloudInitIsAHandle() {
        // ⚠ A Secret property in a resource body lands in durable grain state in plaintext —
        // SchemaProperty.Secret's own remarks. Cloud-init is a vault handle instead, docs/plan/13's rule.
        VirtualMachines.Schema2026.Properties.ShouldAllBe(x => !x.Secret);
        Disks.Schema2026.Properties.ShouldAllBe(x => !x.Secret);
        Images.Schema2026.Properties.ShouldAllBe(x => !x.Secret);

        var handle = VirtualMachines.Schema2026.Properties.Single(static x => x.JsonPointer
            == "/properties/cloudInit/userData"
        );
        handle.Widget.ShouldBe(WidgetHint.SecretRef);
        handle.Pattern.ShouldBe(VirtualMachines.OptionalSecretRefPattern);

        VirtualMachines.ParseCloudInitRef(Compute.VaultPath("web") + "#userdata@3", Compute.TenantA)
            .GetValueOrThrow()
            .ShouldBe(
                new CyberCloud.Core.Contracts.SecretRef {
                    Path = Compute.VaultPath("web"), Field = "userdata", Version = "3"
                }
            );
        VirtualMachines.ParseCloudInitRef("", Compute.TenantA).GetValueOrThrow().IsEmpty.ShouldBeTrue();
        VirtualMachines.ParseCloudInitRef("no-hash", Compute.TenantA).IsFailure.ShouldBeTrue();
    }

    [Fact]
    public void ACloudInitHandleMayNameOnlyTheTenantsOwnVaultPrefix() {
        // ⚠ THE TENANCY CHECK ON THE ONE TENANT-SPELLED VAULT PATH IN THE TREE — VirtualMachines
        // .TenantVaultPrefix carries the argument. Tenant A's body naming a path under tenant B's
        // prefix, a platform path, or the bare prefix itself is refused before any resolver is asked,
        // and the refusal is AuthorizationFailed rather than "not found": it must not say whether the
        // path exists.
        VirtualMachines.TenantVaultPrefix(Compute.TenantA).ShouldBe("tenants/aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa/");

        foreach (var foreign in new[] {
                     Compute.VaultPath("CyberCloud.ContainerRegistry/registries/x", Compute.TenantB) + "#password",
                     "platform/bootstrap#token", "tenants/aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa#userdata",
                     "tenants/aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa/#userdata",
                     "tenants/AAAAAAAA-AAAA-4AAA-8AAA-AAAAAAAAAAAA/web#userdata"
                 }) {
            var refused = VirtualMachines.ParseCloudInitRef(foreign, Compute.TenantA);

            refused.IsFailure.ShouldBeTrue(foreign + " was accepted");
            refused.Error!.Code.ShouldBe(ErrorCode.AuthorizationFailed, foreign);
            refused.Error.Message.ShouldContain(VirtualMachines.TenantVaultPrefix(Compute.TenantA));
        }

        // The tenant's own credential paths — what listKeys and listCredentials already hand it — are inside.
        VirtualMachines.ParseCloudInitRef(
            Compute.VaultPath("CyberCloud.ContainerRegistry/registries/x") + "#password",
            Compute.TenantA
        )
            .IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void ADataDiskNameIsCheckedAsAResourceNameBeforeItReachesAClaim() {
        // ⚠ NOT BY THE SCHEMA, AND THE TEST SAYS SO. The registry could apply a per-element Pattern
        // (SchemaProperty.ElementKind's remarks), and ./build.sh Charts refuses `@pattern` on a
        // `{array}` @param — charts/managed/kafka's `cidr-shape-is-unenforced`. So the schema admits
        // any string here, and VirtualMachines.DataDiskProblem is what stands between the body and
        // `persistentVolumeClaim.claimName`; VirtualMachineReconcilerTests drives it through a pass.
        foreach (var bad in new[] {
                     "Data", "data_1", "-data", new string('d', ResourceNaming.MaxLength + 1),
                     VirtualMachines.RootVolume, VirtualMachines.CloudInitVolume
                 }) {
            using var body = JsonDocument.Parse(VirtualMachines.Body(Compute.ClusterId, dataDisks: ["fine", bad]));

            VirtualMachines.Schema2026.Validate(body.RootElement, allowTags: true)
                .IsSuccess.ShouldBeTrue(
                    "the schema refused a disk name, so the chart surface must have grown the per-element constraint it could not carry — retire DataDiskProblem's first check and this test"
                );
            VirtualMachines.DataDiskProblem(body.RootElement)
                .ShouldContain(bad, Case.Sensitive, $"'{bad}' was accepted as a disk name");
        }

        using var good = JsonDocument.Parse(VirtualMachines.Body(Compute.ClusterId, dataDisks: ["data-1", "logs"]));
        VirtualMachines.DataDiskProblem(good.RootElement).ShouldBeEmpty();
    }

    [Fact]
    public void EverythingOnADiskAndAnImageIsImmutableExceptWhatTheSchemaOwns() {
        // CDI refuses a changed DataVolume spec, so every tenant-facing leaf is immutable and the only
        // legal PUT moves tags — conformance.yaml § owed, `nothing-mutable-reaches-the-cluster`.
        foreach (var schema in new[] { Disks.Schema2026, Images.Schema2026 }) {
            schema.Properties.Where(static x => x.Kind != SchemaKind.Nested).ShouldAllBe(x => x.Immutable);
        }

        // And on the machine, size and dataDisks are the two a tenant may change.
        VirtualMachines.Schema2026.Properties.Where(static x => x.Kind != SchemaKind.Nested && !x.Immutable)
            .Select(static x => x.JsonPointer)
            .OrderBy(static x => x, StringComparer.Ordinal)
            .ShouldBe(["/properties/cloudInit/userData", "/properties/dataDisks", "/properties/size"]);
    }

    [Fact]
    public void TheCatalogueOffersLinuxOnlyAndEveryRowIsADigest() {
        Images.CatalogueNames.ShouldBe(["debian-12", "debian-13", "ubuntu-22.04", "ubuntu-24.04"]);

        foreach (var (name, image) in Images.Catalogue) {
            image.Url.ShouldStartWith(Images.RegistryScheme + "quay.io/containerdisks/", Case.Sensitive, name);
            image.Url.ShouldContain(
                "@sha256:",
                Case.Sensitive,
                $"{name} is pinned by a tag, and a tag names different bytes from one week to the next"
            );
            image.Url.ShouldNotContain(":latest");
        }

        Images.Schema2026.Properties.Single(static x => x.JsonPointer == "/properties/source/name")
            .AllowedValues.ShouldBe(Images.CatalogueNames);
    }

    static ResourceTypeRegistration Registration(ResourceTypeName type) {
        ProviderRegistry.Build([new ComputeProvider()]).TryGetType(type, out var registration).ShouldBeTrue();
        return registration;
    }

    static IReadOnlyList<MeterRegistration> Derived(ResourceTypeName type) =>
        [.. Registration(type).Meters.Where(static x => x.Derivation is not null)];
}

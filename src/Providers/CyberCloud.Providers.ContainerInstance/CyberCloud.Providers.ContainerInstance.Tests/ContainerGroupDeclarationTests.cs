using CyberCloud.ResourceManager.Contracts.Generation;
using CyberCloud.ResourceManager.Registry;
using System.Text.Json;

namespace CyberCloud.Providers.ContainerInstance.Tests;

/// <summary>What the provider declares, checked the way a silo checks it at start.</summary>
public sealed class ContainerGroupDeclarationTests {
    [Fact]
    public void TheProviderBuildsIntoARegistryTheSiloWouldAccept() {
        var registry = ProviderRegistry.Build([new ContainerInstanceProvider()]);

        registry.TryGetType(ContainerGroups.Type, out var group).ShouldBeTrue();
        group.ReconcilerType.ShouldBe(typeof(ContainerGroupReconciler));
        group.ClusterIdPointer.ShouldBe(ContainerGroups.ClusterIdPointer);
        registry.Types.Length.ShouldBe(1);

        ContainerGroups.Type.ToString().ShouldBe("CyberCloud.ContainerInstance/containerGroups");
    }

    [Fact]
    public void NoShortNameHereGivesACycTokenTwoMeanings() {
        CliTokens.Collisions(
            ProviderRegistry.Build([new ContainerInstanceProvider()])
                .Types.Select(static x => new CliDeclaration(x.Type.Namespace, x.Type.Type, x.Display.Alias))
        )
            .ShouldBeEmpty();
    }

    [Fact]
    public void TheGroupDrawsItsCpuInCoresAndItsMemoryInGibibytes() {
        using var body = JsonDocument.Parse(ContainerGroups.Body(Groups.ClusterId, cpu: "1500m", memory: "512Mi"));

        var drawn = Derived().ToDictionary(static x => x.Meter, x => x.Derivation!.Amount(body.RootElement).GetValueOrThrow());

        drawn[QuotaMeter.Vcpu].ShouldBe(1.5m, "500m is half a core, and billing's vCPU-hours integrate this");
        drawn[QuotaMeter.MemoryGb].ShouldBe(0.5m);
        drawn.Keys.ShouldNotContain(QuotaMeter.StorageGb, "a group has no volume");

        Registration().Meters.Select(static x => x.Meter).ShouldContain(QuotaMeter.Resources);
        Registration().Meters.Select(static x => x.Meter).ShouldNotContain(QuotaMeter.PublicIps, "the address is metered on its own type");
    }

    [Fact]
    public void AZeroBudgetIsRefusedByTheReconcilerAndReservesNothing() {
        using var body = JsonDocument.Parse(ContainerGroups.Body(Groups.ClusterId, cpu: "0"));

        Derived().Single(static x => x.Meter == QuotaMeter.Vcpu).Derivation!.Amount(body.RootElement).IsFailure.ShouldBeTrue();
        ContainerGroups.BodyProblem(body.RootElement, Groups.TenantA)!.Code.ShouldBe(ErrorCode.InvalidRequestBody);
    }

    [Fact]
    public void EveryDeclaredDefaultIsAValueTheApiWouldAccept() {
        foreach (var body in new[] {
                     ContainerGroups.Body(Groups.ClusterId),
                     ContainerGroups.Body(
                         Groups.ClusterId,
                         ["web=nginx:1.27", "log=busybox:1.37"],
                         ["sh", "-c", "echo"],
                         environment: ["A=b"],
                         secureEnvironment: ["S=" + Groups.VaultPath("x") + "#y@2"],
                         ports: ["80", "53/UDP"],
                         registryServer: "registry.example.com:5000",
                         registryUsername: "admin",
                         registryPassword: Groups.VaultPath("r") + "#p",
                         virtualNetwork: "vnet",
                         subnet: "apps",
                         ipAddress: "10.20.1.30",
                         publicIpAddress: "front"
                     )
                 }) {
            using var document = JsonDocument.Parse(body);
            var validated = ContainerGroups.Schema2026.Validate(document.RootElement, allowTags: true);
            validated.IsSuccess.ShouldBeTrue(validated.Error?.Message);
            ContainerGroups.BodyProblem(document.RootElement, Groups.TenantA).ShouldBeNull();
        }
    }

    [Fact]
    public void NoBodyPropertyIsDeclaredSecretAndEveryVaultReferenceIsAHandle() {
        // ⚠ A Secret property in a body lands in durable grain state in plaintext — SchemaProperty.Secret's
        // remarks. The two vault-backed properties are handles: the scalar one says so to the generated
        // form, and the array cannot — `@widget` renders one scalar field and ./build.sh Charts refuses it
        // on an array — so its description carries the word and its elements the tenancy check.
        ContainerGroups.Schema2026.Properties.ShouldAllBe(x => !x.Secret);

        ContainerGroups.Schema2026.Properties.Single(static x => x.JsonPointer == "/properties/registry/password")
            .Widget.ShouldBe(WidgetHint.SecretRef);
        ContainerGroups.Schema2026.Properties.Single(static x => x.JsonPointer == "/properties/secureEnvironment")
            .Description.ShouldContain("vault handles");
    }

    [Theory]
    [InlineData("containers", """["Web=nginx"]""", "not a container name")]
    [InlineData("containers", """["web"]""", "names no image")]
    [InlineData("containers", """["web=nginx", "web=busybox"]""", "Two containers")]
    [InlineData("containers", """[]""", "at least one container")]
    [InlineData("environment", """["1BAD=x"]""", "NAME=value")]
    [InlineData("environment", """["A=1", "A=2"]""", "set twice")]
    [InlineData("ports", """["http"]""", "not a port")]
    [InlineData("ports", """["70000"]""", "not a port")]
    [InlineData("ports", """["80/ICMP"]""", "not a port")]
    [InlineData("ports", """["80", "80/TCP"]""", "listed twice")]
    public void AnElementTheSchemaCannotCheckIsRefusedWithThePropertyNamed(string property, string value, string says) {
        // ⚠ NOT BY THE SCHEMA, AND THE TEST SAYS SO: ./build.sh Charts refuses @pattern on an {array}
        // @param, so the schema admits every element and BodyProblem is what stands in front of the pod.
        var node = System.Text.Json.Nodes.JsonNode.Parse(ContainerGroups.Body(Groups.ClusterId))!;
        node["properties"]![property] = System.Text.Json.Nodes.JsonNode.Parse(value);
        using var body = JsonDocument.Parse(node.ToJsonString());

        ContainerGroups.Schema2026.Validate(body.RootElement, allowTags: true)
            .IsSuccess.ShouldBeTrue("the schema refused an element, so the chart surface grew a per-element constraint — move the check there");

        var problem = ContainerGroups.BodyProblem(body.RootElement, Groups.TenantA);

        problem.ShouldNotBeNull($"{property} = {value} was accepted");
        problem.Code.ShouldBe(ErrorCode.InvalidRequestBody);
        problem.Target.ShouldBe("/properties/" + property);
        problem.Message.ShouldContain(says);
    }

    [Theory]
    [InlineData("vnet", "", "", "")]
    [InlineData("", "", "10.0.0.5", "")]
    [InlineData("", "", "", "front")]
    public void AnAddressWithoutASubnetIsRefused(string network, string subnet, string ip, string publicIp) {
        using var body = JsonDocument.Parse(
            ContainerGroups.Body(Groups.ClusterId, virtualNetwork: network, subnet: subnet, ipAddress: ip, publicIpAddress: publicIp)
        );

        ContainerGroups.BodyProblem(body.RootElement, Groups.TenantA)!.Target.ShouldBe("/properties/network");
    }

    [Fact]
    public void ARegistryPasswordNeedsItsServerAndUser() {
        using var body = JsonDocument.Parse(ContainerGroups.Body(Groups.ClusterId, registryPassword: Groups.VaultPath("r") + "#p"));

        ContainerGroups.BodyProblem(body.RootElement, Groups.TenantA)!.Target.ShouldBe("/properties/registry");
    }

    [Fact]
    public void AVariableIsEitherPlainOrSecureAndNeverBoth() {
        using var body = JsonDocument.Parse(
            ContainerGroups.Body(Groups.ClusterId, environment: ["A=1"], secureEnvironment: ["A=" + Groups.VaultPath("x") + "#y"])
        );

        ContainerGroups.BodyProblem(body.RootElement, Groups.TenantA)!.Message.ShouldContain("both");
    }

    static ResourceTypeRegistration Registration() {
        ProviderRegistry.Build([new ContainerInstanceProvider()]).TryGetType(ContainerGroups.Type, out var registration).ShouldBeTrue();
        return registration;
    }

    static IReadOnlyList<MeterRegistration> Derived() => [.. Registration().Meters.Where(static x => x.Derivation is not null)];
}

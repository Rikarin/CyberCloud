using CyberCloud.Core;
using CyberCloud.Core.Resources;
using CyberCloud.ResourceManager.Contracts;
using CyberCloud.ResourceManager.Contracts.Registry;
using CyberCloud.ResourceManager.Registry;

namespace CyberCloud.Providers.Resources.Tests;

/// <summary>What only the deployment type's declaration can be wrong about.</summary>
public sealed class DeploymentDeclarationTests {
    [Fact]
    public void TheProviderBuildsIntoARegistryWithNoReconcilerNoMeterAndNoCluster() {
        var registry = ProviderRegistry.Build([new ResourcesProvider()]);

        registry.TryGetType(Deployments.Type, out var registration).ShouldBeTrue();

        // ⚠ No reconciler: OperationGrain drives a deployment through DeploymentDriver, and a
        // reconciler here would be the platform acting where the deployment's creator must.
        registration.ReconcilerType.ShouldBeNull();
        registration.Meters.ShouldBeEmpty();
        registration.RequiresCluster.ShouldBeFalse();
        registration.SoftDeleteDays.ShouldBe(0);
        registration.ApiVersions.Select(static x => x.Version.Value).ShouldBe([Deployments.V2026]);
    }

    [Fact]
    public void WhatIfIsASynchronousActionServedByTheEntryPointAndNotByAHandler() {
        var registry = ProviderRegistry.Build([new ResourcesProvider()]);
        registry.TryGetType(Deployments.Type, out var registration).ShouldBeTrue();

        registration.TryGetAction(Deployments.WhatIfAction, out var action).ShouldBeTrue();

        action.EntryPoint.ShouldBe(nameof(IDeploymentManager));
        action.HandlerType.ShouldBeNull();
        action.LongRunning.ShouldBeFalse();

        // Contributor, as Azure's deployments/whatIf/action is — a dry run prepares a deployment.
        action.Permission.ShouldBe("write");
        action.Request.ShouldNotBeNull();
        action.Request!.Declares(Deployments.TemplatePointer).ShouldBeTrue();
    }

    [Fact]
    public void TheRunRecordIsReadOnlyAndTheTemplateIsRequired() {
        var properties = Deployments.Schema2026.Properties.ToDictionary(static x => x.JsonPointer);

        properties[Deployments.TemplatePointer].Required.ShouldBeTrue();
        properties[Deployments.ParametersPointer].Required.ShouldBeFalse();

        foreach (var pointer in (string[]) [
                     Deployments.OutputResourcesPointer,
                     Deployments.StepsPointer,
                     Deployments.ErrorPointer,
                     Deployments.RollbackPointer
                 ]) {
            properties[pointer].ReadOnly.ShouldBeTrue(pointer);
        }
    }

    [Fact]
    public void AnEntryPointActionRefusesAHandlerOrLongRunningBesideIt() {
        var withHandler = Should.Throw<ArgumentException>(static () => ProviderRegistry.Build([new BothProvider(true)]));
        withHandler.Message.ShouldContain("entry point");

        var longRunning = Should.Throw<ArgumentException>(static () => ProviderRegistry.Build([new BothProvider(false)]));
        longRunning.Message.ShouldContain("longRunning");
    }

    sealed class BothProvider(bool handler) : IResourceProvider {
        public string ProviderNamespace => "CyberCloud.Testing";

        public void Describe(IProviderBuilder builder) =>
            builder
                .ResourceType("things")
                .ApiVersion("2026-08-01", ResourceSchema.Empty)
                .Action(
                    "look",
                    ActionKind.Post,
                    "write",
                    longRunning: !handler,
                    handler: handler ? typeof(NoHandler) : null,
                    entryPoint: "ISomeEntryPoint"
                );
    }

    sealed class NoHandler : IResourceActionHandler {
        public ResourceTypeName Type => default;

        public string Action => "";

        public Task<Result<string>> InvokeAsync(ActionContext context, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}

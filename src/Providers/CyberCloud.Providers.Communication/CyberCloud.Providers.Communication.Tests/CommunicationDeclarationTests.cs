using CyberCloud.ResourceManager.Registry;

namespace CyberCloud.Providers.Communication.Tests;

/// <summary>
///     What the registry builds from <see cref="CommunicationProvider" />, checked the way a silo
///     checks it at start — and the coherence the schemas and the module have to keep between them.
/// </summary>
public sealed class CommunicationDeclarationTests {
    static ProviderRegistry Registry => ProviderRegistry.Build([new CommunicationProvider()]);

    [Fact]
    public void FourTypesAreDeclaredAndNoneRequiresACluster() {
        var registry = Registry;

        registry.Namespaces.ShouldBe([CommunicationServices.ProviderNamespace]);

        string[] declared = [.. registry.Types.Select(x => x.Type.Type).Order(StringComparer.Ordinal)];

        declared.ShouldBe(
            [
                CommunicationServices.TypePath,
                CommunicationChannels.TypePath,
                CommunicationSuppressions.TypePath,
                CommunicationTemplates.TypePath
            ],
            customMessage: "docs/plan/24 § Phase 3's Communication row: channels, templates, suppression — and the service they hang off"
        );

        foreach (var type in registry.Types) {
            // ⚠ THE FIRST CLUSTERLESS FAMILY. A RequiresCluster here would demand a clusterId no
            // schema declares, and ProviderBuilder would have refused the type at silo start; this
            // is the assertion in the other direction, that nobody adds one.
            type.RequiresCluster.ShouldBeFalse($"'{type.Type}' declares RequiresCluster and converges grains, not objects");
            type.ClusterIdPointer.ShouldBeEmpty();
            type.Chart.ShouldBeEmpty($"'{type.Type}' names a chart and renders nothing");
            type.ReconcilerType.ShouldNotBeNull();
            type.ReadPermission.ShouldBe("read");
            type.WritePermission.ShouldBe("write");
            type.DeletePermission.ShouldBe("delete");
            type.SupportsTags.ShouldBeTrue();
            type.ApiVersions.Select(x => x.Version.Value).ShouldBe([CommunicationServices.V2026]);
        }
    }

    [Fact]
    public void EverySynchronousActionNamesAHandlerThatReportsItsOwnTypeAndName() {
        // The Action handlers gate says this too; here so a provider test says it first, with the
        // action named. A handler whose Type or Action disagreed with the registration would be
        // refused by the dispatcher on the first call rather than at start.
        foreach (var type in Registry.Types) {
            foreach (var action in type.Actions) {
                action.LongRunning.ShouldBeFalse($"'{action.Name}' is declared long-running and this family has no long-running work");
                action.HandlerType.ShouldNotBeNull($"'{type.Type}/{action.Name}' names no handler");
                action.Request.ShouldNotBeNull($"'{action.Name}' declares no request shape");
                action.Response.ShouldNotBeNull($"'{action.Name}' declares no response shape");

                var handler = (IResourceActionHandler)Activator.CreateInstance(action.HandlerType!, ArgumentsFor(action.HandlerType!))!;
                handler.Type.ShouldBe(type.Type);
                handler.Action.ShouldBe(action.Name);
            }
        }
    }

    [Fact]
    public void TheServiceDeclaresTheFourActionsAndTheTemplateDeclaresRender() {
        var registry = Registry;

        registry.TryGetType(CommunicationServices.Type, out var service).ShouldBeTrue();
        service.Actions.Select(x => x.Name)
            .Order(StringComparer.Ordinal)
            .ShouldBe([
                CommunicationServices.CheckSuppressionAction,
                CommunicationServices.ListSuppressionsAction,
                CommunicationServices.SendAction,
                CommunicationServices.StatusAction
            ]);

        service.TryGetAction(CommunicationServices.SendAction, out var send).ShouldBeTrue();
        send.Permission.ShouldBe("write", "a send is a write, not a read: it costs money and reaches a person");
        send.Secret.ShouldBeFalse();

        registry.TryGetType(CommunicationTemplates.Type, out var template).ShouldBeTrue();
        template.Actions.Select(x => x.Name).ShouldBe([CommunicationTemplates.RenderAction]);

        registry.TryGetType(CommunicationChannels.Type, out var channel).ShouldBeTrue();
        channel.Actions.ShouldBeEmpty();

        registry.TryGetType(CommunicationSuppressions.Type, out var suppression).ShouldBeTrue();
        suppression.Actions.ShouldBeEmpty();
    }

    [Fact]
    public void TheFiveSpelledKindsAreTheFiveTheModuleHas() {
        // ⚠ A published api-version is immutable, so the five words are a contract; the enum is the
        // module's. A sixth ChannelKind with no spelling would be a channel no body could configure,
        // and a sixth spelling with no kind would parse to Unknown and be refused after the 202.
        var kinds = Enum.GetValues<ChannelKind>().Where(x => x != ChannelKind.Unknown).ToArray();

        ChannelKinds.AllowedValues.Length.ShouldBe(kinds.Length);

        foreach (var kind in kinds) {
            var spelled = ChannelKinds.Spell(kind);
            ChannelKinds.AllowedValues.ShouldContain(spelled, $"{kind} has no body spelling");
            ChannelKinds.Parse(spelled).ShouldBe(kind);
        }

        ChannelKinds.Parse("carrier-pigeon").ShouldBe(ChannelKind.Unknown);
        ChannelKinds.Parse("Email").ShouldBe(ChannelKind.Unknown, "spellings are compared ordinally, as AllowedValues are");
    }

    [Fact]
    public void EveryStatusAndReasonTheResponsesSpellIsOneTheSchemasAllow() {
        foreach (var status in Enum.GetValues<MessageStatus>().Where(x => x != MessageStatus.Unknown)) {
            CommunicationServices.StatusValues.ShouldContain(CommunicationServices.SpellStatus(status));
        }

        foreach (var reason in Enum.GetValues<SuppressionReason>().Where(x => x != SuppressionReason.Unknown)) {
            CommunicationServices.SpellReason(reason).ShouldNotBeEmpty($"{reason} has no response spelling");
        }
    }

    [Fact]
    public void TheSchemasDefaultsAreWhatTheReadersApply() {
        // The generated surfaces show a caller the schema's default; the readers apply their own.
        // Two defaults that disagree is a document that lies about one of them.
        using var empty = System.Text.Json.JsonDocument.Parse("""{"location":"eu-central","properties":{}}""");

        CommunicationServices.DefaultLocaleOf(empty.RootElement).ShouldBe(string.Empty);
        CommunicationTemplates.BodyOf(empty.RootElement).Locale.ShouldBe(CommunicationTemplates.DefaultLocale);

        var channel = CommunicationChannels.ToConfiguration(CommunicationTestCluster.Service("x"), WithKind(empty.RootElement)).GetValueOrThrow();
        channel.Enabled.ShouldBeTrue("the schema publishes enabled: true as the default");
        channel.Credentials.Mode.ShouldBe(CredentialMode.PlatformAccount);
        channel.Limits.ShouldBe(ChannelLimits.None, "the schema publishes zero limits, which is the module's safe default");
        channel.Limits.Currency.ShouldBe("EUR");
    }

    static System.Text.Json.JsonElement WithKind(System.Text.Json.JsonElement _) {
        var document = System.Text.Json.JsonDocument.Parse("""{"location":"eu-central","properties":{"kind":"email"}}""");
        return document.RootElement.Clone();
    }

    /// <summary>Constructor arguments for a handler, so the declaration test can instantiate one.</summary>
    static object[] ArgumentsFor(Type handler) =>
        [
            .. handler.GetConstructors()[0]
                .GetParameters()
                .Select(object (x) => x.ParameterType == typeof(IMessageSender)
                    ? new RefusingSender()
                    : x.ParameterType == typeof(ICommunicationControlPlane) ? new RefusingPlane()
                    : throw new InvalidOperationException($"{handler.Name} takes a {x.ParameterType.Name}, which this test cannot supply")
                )
        ];

    sealed class RefusingSender : IMessageSender {
        public Task<Result<MessageSnapshot>> SendAsync(Guid tenantId, SendRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Result<MessageSnapshot>> GetStatusAsync(Guid tenantId, Guid serviceId, string idempotencyKey, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    sealed class RefusingPlane : ICommunicationControlPlane {
        public Task<Result<CommunicationService>> EnsureServiceAsync(Guid tenantId, Guid serviceId, string name, string defaultLocale, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Result<CommunicationService>> DescribeServiceAsync(Guid tenantId, Guid serviceId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Result> RetireServiceAsync(Guid tenantId, Guid serviceId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Result<CommunicationService>> ConfigureChannelAsync(Guid tenantId, Guid serviceId, ChannelConfiguration configuration, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Result<ChannelConfiguration>> GetChannelAsync(Guid tenantId, Guid serviceId, ChannelKind channel, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Result> RemoveChannelAsync(Guid tenantId, Guid serviceId, ChannelKind channel, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Result<MessageTemplateVersion>> EnsureTemplateAsync(Guid tenantId, Guid serviceId, Guid templateId, string name, ChannelKind channel, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Result<MessageTemplateVersion>> AddTemplateVersionAsync(Guid tenantId, Guid templateId, System.Collections.Immutable.ImmutableArray<TemplateParameter> parameters, System.Collections.Immutable.ImmutableArray<LocalizedBody> bodies, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Result<Guid>> ResolveTemplateAsync(Guid tenantId, Guid serviceId, string name, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Result<System.Collections.Immutable.ImmutableArray<MessageTemplateVersion>>> ListTemplateVersionsAsync(Guid tenantId, Guid templateId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Result> UnregisterTemplateAsync(Guid tenantId, Guid serviceId, string name, Guid templateId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Result<SuppressionEntry>> SuppressAsync(Guid tenantId, Guid serviceId, ChannelKind channel, string destination, SuppressionReason reason, string note, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Result<SuppressionCheck>> CheckSuppressionAsync(Guid tenantId, Guid serviceId, ChannelKind channel, string destination, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Result> ReleaseSuppressionAsync(Guid tenantId, Guid serviceId, ChannelKind channel, string destination, string reason, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Result<System.Collections.Immutable.ImmutableArray<SuppressionEntry>>> ListSuppressionsAsync(Guid tenantId, Guid serviceId, ChannelKind channel, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}

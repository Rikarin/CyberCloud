using CyberCloud.ResourceManager.Contracts.Generation;
using System.Text.Json.Nodes;

namespace CyberCloud.ResourceManager.Contracts.Tests.Generation;

/// <summary>
///     Policy's addresses on every surface generated from the document — the review of issue #46.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The state this suite refuses is the one the review found: a silent surface.</b> The
///         gateway served <c>{scope}/providers/CyberCloud.Policy/…</c> and no generated document, SDK,
///         CLI verb or portal client knew it, because the namespace is reserved and no provider
///         registers it — #63's gap, a fifth time. So the assertions are about the surfaces, and about
///         the document agreeing with the router's own grammar rather than with a list typed beside it.
///     </para>
/// </remarks>
public sealed class PolicySurfaceTests {
    static JsonObject Document => OpenApiEmitter.Emit(Fixtures.Postgres(), ApiVersion.Parse(Fixtures.FirstVersion));

    /// <summary>
    ///     ⚠ <b>Every address the document declares is one the router parses, as the kind and on the
    ///     scope the document says — and every pair the router allows is declared.</b>
    /// </summary>
    /// <remarks>
    ///     Both directions against <see cref="PolicyAddress" />, the grammar <c>GatewayRoute</c> routes
    ///     by. A path the document declared and the router refused would generate a method that answers
    ///     <c>400</c> every time; a pair the router allowed and the document left out is the silence the
    ///     review found.
    /// </remarks>
    [Fact]
    public void EveryDeclaredAddressIsOneTheRouterParsesAndEveryAllowedPairIsDeclared() {
        var document = Document;

        OpenApiStructure.Validate(document).ShouldBeEmpty();

        var objects = DocumentReader.ScopeObjectsOf(document);
        var declared = new HashSet<(PolicyObjectKind, ScopeKind)>();

        foreach (var scoped in objects) {
            var collection = PolicyAddress.ParsePath(Filled(scoped.CollectionPath)).GetValueOrThrow();

            collection.IsCollection.ShouldBeTrue(scoped.CollectionPath);
            collection.Segment.ShouldBe(scoped.TypePath);
            KindName(collection.Scope.Kind).ShouldBe(scoped.Scope);
            declared.Add((collection.Kind, collection.Scope.Kind)).ShouldBeTrue("one entry per object and scope");

            if (scoped.Path.Length > 0) {
                var item = PolicyAddress.ParsePath(Filled(scoped.Path)).GetValueOrThrow();

                item.Kind.ShouldBe(collection.Kind);
                item.Name.ShouldBe("sample", scoped.Path);
            }
        }

        foreach (var kind in new[] { PolicyObjectKind.Definition, PolicyObjectKind.Assignment, PolicyObjectKind.State }) {
            foreach (var scope in new[] { ScopeKind.Tenant, ScopeKind.ManagementGroup, ScopeKind.Subscription, ScopeKind.ResourceGroup }) {
                declared.Contains((kind, scope)).ShouldBe(PolicyAddress.AllowsScope(kind, scope), $"{kind} on a {scope}");
            }
        }

        // ⚠ And a state is never addressed one at a time, on the router or here.
        objects.Where(static x => x.TypePath == PolicyAddress.StatesSegment).ShouldAllBe(static x => x.Path.Length == 0 && !x.Writable);
    }

    /// <summary>⚠ <b>A policy object is neither a resource type nor a scope, and reads as neither.</b></summary>
    [Fact]
    public void APolicyObjectIsNotReadAsAResourceTypeOrAScope() {
        var document = Document;

        DocumentReader.TypesOf(document).ShouldNotContain(static x => x.ResourceType.StartsWith(PolicyAddress.ProviderNamespace, StringComparison.Ordinal));
        DocumentReader.ScopesOf(document).Select(static x => x.Kind).ShouldBe(["tenant", "managementGroup", "subscription", "resourceGroup"]);
    }

    /// <summary>
    ///     ⚠ <b>The write bodies are the manager's own member names, and the rule is a JSON value on
    ///     every surface, never the tag bag's string map.</b>
    /// </summary>
    [Fact]
    public void TheBodiesUseTheManagersNamesAndTheRuleIsAJsonValueEverywhere() {
        var document = Document;
        var definition = DocumentReader.ScopeObjectsOf(document).First(static x => x.TypePath == PolicyAddress.DefinitionsSegment);
        var assignment = DocumentReader.ScopeObjectsOf(document).First(static x => x.TypePath == PolicyAddress.AssignmentsSegment);

        DocumentReader.LeavesOf(definition.Content).Select(static x => x.JsonPointer).ShouldBe([
            "/properties",
            "/properties/" + PolicyBodyProperties.Description,
            "/properties/" + PolicyBodyProperties.DisplayName,
            "/properties/" + PolicyBodyProperties.PolicyRule
        ]);

        DocumentReader.LeavesOf(assignment.Content).Select(static x => x.JsonPointer).ShouldBe([
            "/properties",
            "/properties/" + PolicyBodyProperties.DisplayName,
            "/properties/" + PolicyBodyProperties.NotScopes,
            "/properties/" + PolicyBodyProperties.PolicyDefinitionId
        ]);

        SdkEmitter.Emit(document).ShouldContain("public required JsonNode PolicyRule { get; set; }");
        PythonSdkEmitter.Emit(document).Single(static x => x.Key.EndsWith("/models.py", StringComparison.Ordinal))
            .Value.ShouldContain("policy_rule: Any");
        GoSdkEmitter.Emit(document).Single(static x => x.Key.EndsWith("/models.go", StringComparison.Ordinal))
            .Value.ShouldContain("PolicyRule json.RawMessage `json:\"policyRule\"`");
        TypeScriptEmitter.Emit(document)["src/models.ts"].ShouldContain("policyRule: unknown;");

        var create = CliEmitter.Emit(document)["groups"]!["policy"]!["commands"]!["subscription-definitions"]!["verbs"]!["create"]!;
        var rule = create["flags"]!.AsArray().Single(static x => DocumentReader.Text(x?["name"]) == "--policy-rule")!;

        DocumentReader.Text(rule["type"]).ShouldBe("json");
        rule["required"]!.GetValue<bool>().ShouldBeTrue();
    }

    /// <summary>
    ///     ⚠ <b>Nothing policy does is long-running, on any surface.</b> One catalog write converges
    ///     before the call returns, so a <c>--wait</c> or an <c>Operation&lt;T&gt;</c> would poll an
    ///     operation that was never started.
    /// </summary>
    [Fact]
    public void NoPolicyVerbIsLongRunningAndTheStatesAreListOnly() {
        var document = Document;
        var group = CliEmitter.Emit(document)["groups"]!["policy"]!["commands"]!.AsObject();

        group.Select(static x => x.Key).Order(StringComparer.Ordinal).ShouldBe([
            "management-group-assignments",
            "management-group-definitions",
            "management-group-states",
            "resource-group-assignments",
            "resource-group-states",
            "subscription-assignments",
            "subscription-definitions",
            "subscription-states",
            "tenant-definitions"
        ]);

        foreach (var command in group) {
            foreach (var verb in command.Value!["verbs"]!.AsObject()) {
                verb.Value!["longRunning"]!.GetValue<bool>().ShouldBeFalse($"policy {command.Key} {verb.Key}");
                verb.Value!["waitFlags"].ShouldBeNull($"policy {command.Key} {verb.Key}");
            }
        }

        group["subscription-states"]!["verbs"]!.AsObject().Select(static x => x.Key).ShouldBe(["list"]);
        group["subscription-definitions"]!["verbs"]!.AsObject().Select(static x => x.Key).ShouldBe(["create", "delete", "list", "show"]);

        var sdk = SdkEmitter.Emit(document);

        sdk.ShouldContain("public partial Task<Response<PolicyDefinition>> CreateOrUpdatePolicyDefinitionAtSubscriptionAsync(");
        sdk.ShouldContain("public partial Task<Response> DeletePolicyAssignmentAtResourceGroupAsync(");
        sdk.ShouldContain("public partial AsyncPageable<PolicyState> ListPolicyStatesAtManagementGroupAsync(");
        sdk.ShouldNotContain("Operation<PolicyDefinition>");
    }

    /// <summary>
    ///     ⚠ <b>A management group is named on every policy verb that addresses one, and never taken
    ///     from a profile.</b> A policy written a level too high applies to everything beneath that
    ///     level, so the one scope a profile doesn't hold is the one the caller always types.
    /// </summary>
    [Fact]
    public void AManagementGroupIsARequiredFlagAndNeverTheProfiles() {
        var verbs = CliEmitter.Emit(Document)["groups"]!["policy"]!["commands"]!["management-group-assignments"]!["verbs"]!.AsObject();

        foreach (var verb in verbs) {
            var flag = verb.Value!["flags"]!.AsArray().Single(static x => DocumentReader.Text(x?["name"]) == "--management-group")!;

            flag["required"]!.GetValue<bool>().ShouldBeTrue(verb.Key);
            DocumentReader.Text(flag["pathPlaceholder"]).ShouldBe("managementGroupName");
            flag["env"].ShouldBeNull(verb.Key);
        }
    }

    /// <summary>⚠ <b>Every surface's client reaches every address, and every model it names is one it declares.</b></summary>
    [Fact]
    public void EveryClientReachesEveryAddressAndDeclaresWhatItNames() {
        var document = Document;
        var python = PythonSdkEmitter.Emit(document);
        var go = GoSdkEmitter.Emit(document);
        var typescript = TypeScriptEmitter.Emit(document);

        TypeScriptEmitter.Problems(typescript).ShouldBeEmpty();
        PythonSdkEmitter.Problems(python).ShouldBeEmpty();
        GoSdkEmitter.Problems(go).ShouldBeEmpty();

        var pythonClient = python.Single(static x => x.Key.EndsWith("/client.py", StringComparison.Ordinal)).Value;
        var goClient = go.Single(static x => x.Key.EndsWith("/client.go", StringComparison.Ordinal)).Value;

        pythonClient.ShouldContain("        self.policy = PolicyClient(transport)\n");
        goClient.ShouldContain(" &PolicyClient{transport: transport},");

        foreach (var scoped in DocumentReader.ScopeObjectsOf(document)) {
            pythonClient.ShouldContain("def list_" + Snake(scoped.PluralStem) + "(");
            goClient.ShouldContain(") List" + scoped.PluralStem + "(");
            typescript["src/client.ts"].ShouldContain("  list" + scoped.PluralStem + "(");

            if (scoped.Writable) {
                pythonClient.ShouldContain("def delete_" + Snake(scoped.SingularStem) + "(");
                goClient.ShouldContain(") CreateOrUpdate" + scoped.SingularStem + "(ctx context.Context");
                typescript["src/client.ts"].ShouldContain("  createOrUpdate" + scoped.SingularStem + "(");
            }
        }
    }

    /// <summary>A template with every placeholder filled — a GUID where one is a GUID, <c>sample</c> elsewhere.</summary>
    static string Filled(string template) =>
        template
            .Replace("{tenantId}", "11111111-1111-1111-1111-111111111111", StringComparison.Ordinal)
            .Replace("{subscriptionId}", "22222222-2222-2222-2222-222222222222", StringComparison.Ordinal)
            .Replace("{resourceGroupName}", "prod", StringComparison.Ordinal)
            .Replace("{managementGroupName}", "platform", StringComparison.Ordinal)
            .Replace("{" + OpenApiEmitter.PolicyDefinitionPlaceholder + "}", "sample", StringComparison.Ordinal)
            .Replace("{" + OpenApiEmitter.PolicyAssignmentPlaceholder + "}", "sample", StringComparison.Ordinal);

    /// <summary><c>PolicyDefinitionsAtSubscription</c> as <c>policy_definitions_at_subscription</c> — the Python names.</summary>
    static string Snake(string pascal) =>
        string.Concat(pascal.Select(static (x, i) => i > 0 && char.IsUpper(x) ? "_" + char.ToLowerInvariant(x) : char.ToLowerInvariant(x).ToString()));

    static string KindName(ScopeKind kind) =>
        kind switch {
            ScopeKind.Tenant => "tenant",
            ScopeKind.ManagementGroup => "managementGroup",
            ScopeKind.Subscription => "subscription",
            _ => "resourceGroup"
        };
}

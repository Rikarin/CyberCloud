using CyberCloud.ResourceManager.Contracts.Generation;
using System.Text.Json.Nodes;

namespace CyberCloud.ResourceManager.Contracts.Tests.Generation;

/// <summary>
///     The scope API on the four surfaces generated from the document — issue #63.
/// </summary>
/// <remarks>
///     <para>
///         ⚠
///         <b>
///             The state this suite exists to refuse is not a broken surface, it is a silent
///             one.
///         </b> <c>RouteKind.Scope</c> has served <c>PUT</c> and <c>GET</c> on two addresses
///         since #1 and every generated surface was correct, complete, gated and unaware of them —
///         because all five were emitted from the provider registry and a scope has no provider. A
///         tenant could therefore create a subscription only by hand, which makes the first two steps
///         of the M1 exit story unreachable from any tool this project ships.
///     </para>
///     <para>
///         ⚠ <b>So the assertions are about the surfaces rather than about the document alone.</b>
///         A document that declares a path no emitter reads is the same silence one file further on.
///     </para>
/// </remarks>
public sealed class ScopeSurfaceTests {
    static JsonObject Document => OpenApiEmitter.Emit(Fixtures.Postgres(), ApiVersion.Parse(Fixtures.FirstVersion));

    [Fact]
    public void TheDocumentDeclaresTheAddressesTheGatewayServes() {
        var document = Document;

        OpenApiStructure.Validate(document).ShouldBeEmpty();

        var paths = document["paths"]!.AsObject();

        paths.ShouldContainKey("/tenants/{tenantId}");
        paths.ShouldContainKey("/tenants/{tenantId}/subscriptions/{subscriptionId}");
        paths.ShouldContainKey("/tenants/{tenantId}/subscriptions/{subscriptionId}/resourceGroups/{resourceGroupName}");
    }

    /// <summary>
    ///     ⚠
    ///     <b>
    ///         docs/plan/10 § Shape's "the scope API is the first four and six segments of the
    ///         resource path" is a mechanism and not only a sentence.
    ///     </b>
    /// </summary>
    /// <remarks>
    ///     Every resource path is built from <c>ResourceGroupPathTemplate</c>, so the two cannot come
    ///     to disagree about the envelope. Written the other way — the prefix typed once in
    ///     <c>PathOf</c> and once in the scope emitter — a change to one would leave a document whose
    ///     scope paths were no longer prefixes of its resource paths, and every path in it would
    ///     still parse.
    /// </remarks>
    [Fact]
    public void EveryResourcePathBeginsWithTheResourceGroupScopesPath() {
        var document = Document;
        var group = DocumentReader.ScopesOf(document).Single(x => x.Kind == "resourceGroup");

        foreach (var type in DocumentReader.TypesOf(document)) {
            type.Path.ShouldStartWith(group.Path + "/providers/");
            type.CollectionPath.ShouldStartWith(group.Path + "/providers/");
        }
    }

    /// <summary>
    ///     ⚠ <b>A scope is not a resource type and must not read as one.</b>
    /// </summary>
    /// <remarks>
    ///     The same failure the collection path had before <c>x-cybercloud-collection</c> existed: a
    ///     path carrying a type name that is not a type makes <c>CliEmitter</c> throw on a duplicate
    ///     command, <c>SdkEmitter</c> throw on a duplicate model, and <c>FormsEmitter</c> silently
    ///     replace the real one. A scope carries no <c>x-cybercloud-resource-type</c> at all, so it is
    ///     already invisible — asserted rather than assumed, because "it happens not to have one" and
    ///     "it may not have one" are different facts.
    /// </remarks>
    [Fact]
    public void AScopeIsNotReadAsAResourceType() {
        var document = Document;

        DocumentReader.TypesOf(document)
            .Select(x => x.Path)
            .ShouldNotContain("/tenants/{tenantId}/subscriptions/{subscriptionId}");

        foreach (var scope in DocumentReader.ScopesOf(document)) {
            document["paths"]![scope.Path]!["x-cybercloud-resource-type"].ShouldBeNull(scope.Kind);
        }
    }

    /// <summary>
    ///     ⚠
    ///     <b>
    ///         The tenant has no <c>PUT</c> on any surface, and that is a decision rather than an
    ///         omission.
    ///     </b>
    /// </summary>
    /// <remarks>
    ///     A request's tenant is resolved from its token and every path naming a different one is
    ///     refused before routing runs, so a create route could not authenticate —
    ///     <c>IScopeManager.CreateTenantAsync</c> is off the request pipeline entirely. A document
    ///     that declared the <c>PUT</c> would generate a <c>cyc</c> verb, an SDK method and a portal
    ///     form that fail every single time they are used.
    /// </remarks>
    [Fact]
    public void TheTenantIsReadableAndNotCreatableEverywhere() {
        var document = Document;
        var tenant = DocumentReader.ScopesOf(document).Single(x => x.Kind == "tenant");

        tenant.Creatable.ShouldBeFalse();
        document["paths"]![tenant.Path]!["put"].ShouldBeNull();

        var verbs = CliEmitter.Emit(document)["groups"]![CliEmitter.ScopeGroupName]!["commands"]!["tenant"]!["verbs"]!
            .AsObject();

        verbs.ShouldContainKey("show");
        verbs.ShouldNotContainKey("create");

        FormsEmitter.Emit(document)["scopeForms"]!.AsObject().ShouldNotContainKey("tenant");
        SdkEmitter.Emit(document).ShouldNotContain("CreateTenantAsync(");
    }

    /// <summary>
    ///     ⚠
    ///     <b>
    ///         The flag that names what is being created is never the flag that remembers where you
    ///         are working.
    ///     </b>
    /// </summary>
    /// <remarks>
    ///     <c>{subscriptionId}</c> is filled by <c>--subscription</c> on every resource verb, and
    ///     that flag is profile-backed and optional. Reusing it here would make
    ///     <c>cyc scope subscription create --display-name X</c> — with the address flag simply left
    ///     off — create the subscription the profile already points at, which is a write the caller
    ///     never named.
    /// </remarks>
    [Fact]
    public void AScopesOwnSegmentIsARequiredNameAndNotTheProfileFlag() {
        var create = CliEmitter.Emit(Document)["groups"]![CliEmitter.ScopeGroupName]!
            ["commands"]!["subscription"]!["verbs"]!["create"]!;

        var flags = create["flags"]!.AsArray();

        var name = flags.Single(x => DocumentReader.Text(x?["name"]) == "--name")!;

        name["required"]!.GetValue<bool>().ShouldBeTrue();
        DocumentReader.Text(name["pathPlaceholder"]).ShouldBe("subscriptionId");
        name["env"].ShouldBeNull("a create must never fall back to the profile");

        // The tenant above it stays context, which is the asymmetry: your tenant is where you are,
        // the subscription id is what you are typing.
        var tenant = flags.Single(x => DocumentReader.Text(x?["name"]) == "--tenant")!;

        tenant["required"]!.GetValue<bool>().ShouldBeFalse();
        DocumentReader.Text(tenant["env"]).ShouldBe("CYC_TENANT");

        // …and the body property is a flag, which is the whole point of generating this rather than
        // telling people to use `cyc rest`.
        DocumentReader.Text(flags.Single(x => DocumentReader.Text(x?["name"]) == "--display-name")!["jsonPointer"])
            .ShouldBe("/" + ScopeBodyProperties.DisplayName);
    }

    /// <summary>
    ///     ⚠ <b>No scope verb is long-running, on any surface.</b>
    /// </summary>
    /// <remarks>
    ///     A scope is one grain activation and converges before the call returns, so there is nothing
    ///     to poll. A <c>--wait</c> would follow an <c>Azure-AsyncOperation</c> URL that answers
    ///     <c>404</c>, an <c>Operation&lt;T&gt;</c> would poll an operation that was never started,
    ///     and a portal progress bar would never move.
    /// </remarks>
    [Fact]
    public void NoScopeVerbIsLongRunning() {
        var document = Document;
        var group = CliEmitter.Emit(document)["groups"]![CliEmitter.ScopeGroupName]!["commands"]!.AsObject();

        foreach (var command in group) {
            foreach (var verb in command.Value!["verbs"]!.AsObject()) {
                verb.Value!["longRunning"]!.GetValue<bool>()
                    .ShouldBeFalse($"scope {command.Key} {verb.Key}");

                verb.Value!["waitFlags"].ShouldBeNull($"scope {command.Key} {verb.Key}");
            }
        }

        foreach (var form in FormsEmitter.Emit(document)["scopeForms"]!.AsObject()) {
            form.Value!["longRunning"]!.GetValue<bool>().ShouldBeFalse(form.Key);
        }

        var sdk = SdkEmitter.Emit(document);

        sdk.ShouldContain("Task<Response<ScopeResource>> CreateSubscriptionAsync(");
        sdk.ShouldNotContain("Operation<ScopeResource>");
    }

    /// <summary>
    ///     ⚠ <b>The generated SDK declares every type it names.</b>
    /// </summary>
    /// <remarks>
    ///     The first version of the scope emitter produced a property typed <c>ScopeResourceType</c>
    ///     and never emitted that enum. Nothing in this repository compiled
    ///     <c>generated/sdk/*.cs</c> at the time — it is an artifact, not a source file — so the byte
    ///     gate was green, every test passed, and the first person to find out would have been the
    ///     first person to consume the SDK. This is the cheapest check that would have caught it.
    ///     ⚠ It is not the only one any more: <c>Generated SDK compiles</c> (issue #73) hands the
    ///     CHECKED-IN file to Roslyn, and a <c>CS0246</c> is exactly what it reports. This assertion
    ///     reads what the emitter PRODUCES from a fixture no document carries, so neither replaces
    ///     the other — the same division <c>TypeScriptSurfaceTests</c> records.
    /// </remarks>
    [Fact]
    public void TheSdkDeclaresTheScopeTypesItReferences() {
        var sdk = SdkEmitter.Emit(Document);

        sdk.ShouldContain("public sealed partial class ScopeResource {");
        sdk.ShouldContain("public sealed partial class SubscriptionCreateContent {");
        sdk.ShouldContain("public sealed partial class ResourceGroupCreateContent {");
        sdk.ShouldContain("public enum ScopeResourceType {");

        // ⚠ A non-nullable string with no initialiser and no `required` is CS8618 wherever this file
        // is compiled — which was nowhere in this repository until issue #73, and is `Generated SDK
        // compiles` today. That gate is errors-only by design and CS8618 is a warning, so it would
        // still let this one through; this assertion is what does not.
        sdk.ShouldContain("public required string Id { get; set; }");
    }

    /// <summary>
    ///     ⚠
    ///     <b>
    ///         The property names on the wire are <c>ScopeBodyProperties</c>', not spellings the
    ///         emitters chose.
    ///     </b>
    /// </summary>
    /// <remarks>
    ///     They lived on <c>ScopeManagerService</c>, in an assembly the generators cannot see. A CLI
    ///     offering <c>--display-name</c> that wrote <c>/name</c> would be a flag spelled correctly
    ///     writing a property the manager does not read, and the answer would be a <c>400</c> saying
    ///     a subscription needs a <c>displayName</c> to somebody who supplied one.
    /// </remarks>
    [Fact]
    public void TheCreateBodiesUseTheManagersOwnPropertyNames() {
        var document = Document;
        var scopes = DocumentReader.ScopesOf(document);

        DocumentReader.LeavesOf(scopes.Single(x => x.Kind == "subscription").Body)
            .Select(x => x.Name)
            .ShouldBe([ScopeBodyProperties.DisplayName]);

        DocumentReader.LeavesOf(scopes.Single(x => x.Kind == "resourceGroup").Body)
            .Select(x => x.Name)
            .ShouldBe([ScopeBodyProperties.Location]);
    }

    /// <summary>
    ///     ⚠ <b>A provider whose namespace produces the scope group is refused, not merged.</b>
    /// </summary>
    /// <remarks>
    ///     <c>groups[key] = …</c> would have merged the two, leaving one command reachable and the
    ///     other nowhere — the failure <c>CliEmitter</c> already throws on for two colliding command
    ///     names, one level up and with a group that no provider registered.
    /// </remarks>
    [Fact]
    public void AProviderNamespaceThatWouldTakeTheScopeGroupIsRefused() {
        var registry = new FakeRegistry {
            Namespaces = ["CyberCloud.Scope"],
            Types = [
                new ResourceTypeRegistration {
                    Type = new("CyberCloud.Scope", "widgets"),
                    ApiVersions = [new(ApiVersion.Parse(Fixtures.FirstVersion), Fixtures.DatabaseSchema())]
                }
            ]
        };

        var document = OpenApiEmitter.Emit(registry, ApiVersion.Parse(Fixtures.FirstVersion));

        var failure = Should.Throw<InvalidOperationException>(() => CliEmitter.Emit(document));

        failure.Message.ShouldContain(CliEmitter.ScopeGroupName);
    }

    // ── The two collections — #63's answer applied a second time ───────────────────────────────

    /// <summary>
    ///     ⚠
    ///     <b>
    ///         A scope collection is read as a collection <i>on</i> a scope, never as a fourth or
    ///         fifth scope.
    ///     </b>
    /// </summary>
    /// <remarks>
    ///     Both collection items carry <c>x-cybercloud-scope</c>, because a surface has to find
    ///     which kind a page holds — and that is exactly the key <c>DocumentReader.ScopesOf</c>
    ///     reads a scope by. Without the second discriminator the subscription collection would
    ///     read as a second <c>subscription</c> scope: <c>CliEmitter</c> writes
    ///     <c>commands[kind]</c> twice and keeps the last, and the create verb goes with it. The
    ///     same failure the resource collection had before <c>x-cybercloud-collection</c> existed,
    ///     one grammar over.
    /// </remarks>
    [Fact]
    public void AScopeCollectionIsReadAsACollectionOnItsScopeAndNotAsAScope() {
        var document = Document;
        var scopes = DocumentReader.ScopesOf(document);

        scopes.Select(x => x.Kind).ShouldBe(["tenant", "subscription", "resourceGroup"]);

        scopes.Single(x => x.Kind == "subscription").CollectionPath.ShouldBe(OpenApiEmitter.SubscriptionCollectionPathTemplate);
        scopes.Single(x => x.Kind == "resourceGroup").CollectionPath.ShouldBe(OpenApiEmitter.ResourceGroupCollectionPathTemplate);

        // ⚠ The tenant has none, and the absence is the contract: the only tenant a request can
        // address is its own, so there is nothing to enumerate.
        scopes.Single(x => x.Kind == "tenant").CollectionPath.ShouldBe("");

        // The paging pair, read off the document rather than assumed — the same two a resource
        // collection declares, so cyc's --top and --skip-token are one flag pair everywhere.
        scopes.Single(x => x.Kind == "subscription").CollectionQuery
            .Select(x => x.Name)
            .ShouldBe(["$skipToken", "$top", "api-version"]);
    }

    /// <summary>
    ///     ⚠ <b><c>cyc scope subscription list</c> and <c>cyc scope resource-group list</c> exist,
    ///     page, and name no scope of their own.</b>
    /// </summary>
    /// <remarks>
    ///     The collection path ends on the parent, so the verb's flags are the ancestors' profile
    ///     flags plus the paging pair — no <c>--name</c>, or the host would have a flag that binds
    ///     to no placeholder. <c>DerivedSurfaceTests.EveryPlaceholderInAVerbsPathIsFilledByExactlyOneOfItsFlags</c>
    ///     holds every verb to that; this pins the two verbs by name so a regression that dropped
    ///     them would fail here rather than pass an invariant over a smaller tree.
    /// </remarks>
    [Fact]
    public void ScopeListVerbsExist() {
        var commands = CliEmitter.Emit(Document)["groups"]![CliEmitter.ScopeGroupName]!["commands"]!.AsObject();

        var subscriptions = commands["subscription"]!["verbs"]!["list"]!;
        subscriptions["path"]!.GetValue<string>().ShouldBe(OpenApiEmitter.SubscriptionCollectionPathTemplate);
        subscriptions["method"]!.GetValue<string>().ShouldBe("GET");
        subscriptions["paged"]!.GetValue<bool>().ShouldBeTrue();
        subscriptions["longRunning"]!.GetValue<bool>().ShouldBeFalse();
        subscriptions["pageFlags"]!.AsArray().Select(x => x!.GetValue<string>()).ShouldBe(["--all"]);

        Flags(subscriptions).ShouldBe(["--skip-token", "--tenant", "--top"]);

        var groups = commands["resource-group"]!["verbs"]!["list"]!;
        groups["path"]!.GetValue<string>().ShouldBe(OpenApiEmitter.ResourceGroupCollectionPathTemplate);
        Flags(groups).ShouldBe(["--skip-token", "--subscription", "--tenant", "--top"]);

        // ⚠ --subscription on the resource-group list is the profile-backed ancestor flag and not
        // the scope's own required --name: a group's subscription is context, its name is not.
        groups["flags"]!.AsArray()
            .Single(x => x!["name"]!.GetValue<string>() == "--subscription")!["required"]!
            .GetValue<bool>()
            .ShouldBeFalse();

        commands["tenant"]!["verbs"]!.AsObject().ShouldNotContainKey("list");

        static List<string> Flags(JsonNode verb) =>
            [.. verb["flags"]!.AsArray().Select(x => x!["name"]!.GetValue<string>()).Order(StringComparer.Ordinal)];
    }

    /// <summary>
    ///     ⚠ <b>The four SDKs page the two collections, and none of them pages a tenant list.</b>
    /// </summary>
    /// <remarks>
    ///     The .NET method is a partial declaration over <c>AsyncPageable&lt;ScopeResource&gt;</c>,
    ///     as a resource collection's <c>GetAllAsync</c> is; the TypeScript one returns
    ///     <c>Page&lt;ScopeResource&gt;</c> through the same <c>pageQuery</c>; Python and Go reuse
    ///     their <c>Pager</c>. Each is asserted on the signature the caller types, because that is
    ///     the surface the contract names.
    /// </remarks>
    [Fact]
    public void EverySdkListsTheTwoCollectionsAndNoTenantList() {
        var document = Document;

        var sdk = SdkEmitter.Emit(document);
        sdk.ShouldContain("public partial AsyncPageable<ScopeResource> ListSubscriptionsAsync(\n        string tenantId,");
        sdk.ShouldContain("public partial AsyncPageable<ScopeResource> ListResourceGroupsAsync(\n        string tenantId,\n        string subscriptionId,");
        sdk.ShouldContain("public const string SubscriptionCollectionPathTemplate = \"" + OpenApiEmitter.SubscriptionCollectionPathTemplate + "\";");
        sdk.ShouldNotContain("ListTenantsAsync");

        var client = TypeScriptEmitter.Emit(document)["src/client.ts"];
        client.ShouldContain("listSubscriptions(tenantId: string, page: PageRequest = {}): Promise<ApiResponse<Page<ScopeResource>>>");
        client.ShouldContain("listResourceGroups(tenantId: string, subscriptionId: string, page: PageRequest = {}): Promise<ApiResponse<Page<ScopeResource>>>");
        client.ShouldNotContain("listTenants(");

        var python = PythonSdkEmitter.Emit(document)["cybercloud/v2026_08_01/client.py"];
        python.ShouldContain("def list(self, tenant_id: str, *, top: Optional[int] = None) -> Pager[ScopeResource]:");
        python.ShouldContain("def list(self, tenant_id: str, subscription_id: str, *, top: Optional[int] = None) -> Pager[ScopeResource]:");

        var go = GoSdkEmitter.Emit(document)["api20260801/client.go"];
        go.ShouldContain("func (c *SubscriptionsClient) List(tenantID string, options *ListOptions) *Pager[ScopeResource] {");
        go.ShouldContain("func (c *ResourceGroupsClient) List(tenantID, subscriptionID string, options *ListOptions) *Pager[ScopeResource] {");
        go.ShouldNotContain("func (c *TenantsClient) List(");
    }
}

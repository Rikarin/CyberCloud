using CyberCloud.ResourceManager.Contracts.Generation;
using System.Collections.Immutable;
using System.Text.Json.Nodes;

namespace CyberCloud.ResourceManager.Contracts.Tests.Generation;

/// <summary>
///     The Python and Go SDKs — issue #40 — held to the same shape the other clients are.
/// </summary>
/// <remarks>
///     <para>
///         ⚠
///         <b>
///             These assertions are about names an interpreter or a compiler would refuse, and
///             about facts of the document reaching both clients, not about text a reader would
///             like.
///         </b> Nothing in the .NET build runs Python or Go; the real checks are
///         <c>Generated Python SDK compiles</c> and <c>Generated Go SDK compiles</c> in
///         <c>build/Build.Architecture.cs</c>, one toolchain over each, and both report ○ rather
///         than ✔ when their toolchain is absent. What lives here is the part that can be asserted
///         from a document without either process — the same division of labour
///         <c>TypeScriptSurfaceTests</c> draws for <c>pnpm typecheck:api</c>.
///     </para>
///     <para>
///         ⚠ <b>Both surfaces read the document, and the fixture is the one document.</b> Every
///         test starts from <see cref="OpenApiEmitter.Emit" />'s output over the Postgres fixture,
///         so a fact that reached the document and stopped there is a gap these tests see, and a
///         fact hard-coded into an emitter is one they would not.
///     </para>
/// </remarks>
public sealed class PythonGoSurfaceTests {
    static JsonObject Document => OpenApiEmitter.Emit(Fixtures.Postgres(), ApiVersion.Parse(Fixtures.FirstVersion));

    static ImmutableSortedDictionary<string, string> Python => PythonSdkEmitter.Emit(Document);

    static ImmutableSortedDictionary<string, string> Go => GoSdkEmitter.Emit(Document);

    const string PythonModule = "cybercloud/v2026_08_01/";

    const string GoPackage = "api20260801/";

    static string PythonModels => Python[PythonModule + "models.py"];

    static string PythonClient => Python[PythonModule + "client.py"];

    static string PythonRuntime => Python[PythonModule + "_runtime.py"];

    static string GoModels => Go[GoPackage + "models.go"];

    static string GoClient => Go[GoPackage + "client.go"];

    static string GoRuntime => Go[GoPackage + "runtime.go"];

    [Fact]
    public void EachPackageIsTheFilesItsToolchainExpects() {
        Python.Keys.ShouldBe(
            [
                PythonModule + "__init__.py",
                PythonModule + "_runtime.py",
                PythonModule + "client.py",
                PythonModule + "models.py"
            ]
        );

        PythonSdkEmitter.Root([Fixtures.FirstVersion]).Keys.ShouldBe(["cybercloud/__init__.py", "cybercloud/py.typed", "pyproject.toml"]);
        Go.Keys.ShouldBe([GoPackage + "client.go", GoPackage + "models.go", GoPackage + "runtime.go"]);
        GoSdkEmitter.Root().Keys.ShouldBe(["go.mod"]);

        PythonSdkEmitter.Problems(Python).ShouldBeEmpty();
        GoSdkEmitter.Problems(Go).ShouldBeEmpty();
    }

    /// <summary>
    ///     ⚠ <b>The package directory carries the api-version, in the one spelling each language
    ///     allows.</b>
    /// </summary>
    /// <remarks>
    ///     A Python module cannot start with a digit or contain a hyphen; a Go path element of the
    ///     form <c>vN</c> is a major-version suffix to the module system and would resolve
    ///     differently from the way it reads. Neither is <c>2026-08-01</c> verbatim, and the
    ///     transport still sends that string on every request.
    /// </remarks>
    [Fact]
    public void ThePackageIsNamedForItsApiVersionInTheSpellingTheLanguageAllows() {
        PythonSdkEmitter.ModuleOf("2026-08-01").ShouldBe("v2026_08_01");
        GoSdkEmitter.PackageOf("2026-08-01").ShouldBe("api20260801");
        GoSdkEmitter.PackageOf("2026-08-01").ShouldNotStartWith("v");

        PythonRuntime.ShouldContain("API_VERSION = \"2026-08-01\"");
        PythonRuntime.ShouldContain("query[\"api-version\"] = API_VERSION");
        GoRuntime.ShouldContain("const APIVersion = \"2026-08-01\"");
        GoRuntime.ShouldContain("query.Set(\"api-version\", APIVersion)");

        foreach (var file in Go) {
            file.Value.ShouldContain("\npackage api20260801\n", customMessage: file.Key);
        }
    }

    /// <summary>⚠ <b>Every file says it is generated, in its first line.</b></summary>
    [Fact]
    public void EveryFileCarriesItsBannerBeforeAnythingElse() {
        foreach (var file in Python.Concat(PythonSdkEmitter.Root([Fixtures.FirstVersion]))) {
            file.Value.Split('\n')[0].ShouldContain("@generated", customMessage: file.Key);
            file.Value.Split('\n')[0].ShouldContain("DO NOT EDIT", customMessage: file.Key);
        }

        foreach (var file in Go.Concat(GoSdkEmitter.Root())) {
            // Go's own convention, which golint and gopls honour: `// Code generated … DO NOT EDIT.`
            file.Value.Split('\n')[0].ShouldStartWith("// Code generated ", customMessage: file.Key);
            file.Value.Split('\n')[0].ShouldEndWith(" DO NOT EDIT.", customMessage: file.Key);
        }
    }

    /// <summary>
    ///     ⚠ <b>Neither client names a type its models do not declare.</b>
    /// </summary>
    /// <remarks>
    ///     The check that would have caught both of the .NET SDK's defects, one language over
    ///     twice: an import of a name that is not there fails at Python's import and at Go's
    ///     build, and only once somebody runs either.
    /// </remarks>
    [Fact]
    public void TheClientNamesNothingTheModelsDoNotDeclare() {
        var declared = PythonModels.Split('\n')
            .Where(x => x.StartsWith("class ", StringComparison.Ordinal))
            .Select(x => x[6..].TrimEnd(':', ' '))
            .ToHashSet(StringComparer.Ordinal);

        var importing = false;

        foreach (var line in PythonClient.Split('\n')) {
            if (line.StartsWith("from .models import (", StringComparison.Ordinal)) {
                importing = true;
                continue;
            }

            if (!importing || line.StartsWith(')')) {
                importing = importing && !line.StartsWith(')');
                continue;
            }

            declared.ShouldContain(line.Trim().TrimEnd(','), $"client.py imports {line.Trim()} and models.py does not declare it");
        }

        // …and the models file exports exactly what it declares, which is what `import *` and
        // mypy's strict mode agree on.
        foreach (var name in declared) {
            PythonModels.ShouldContain("    \"" + name + "\",\n", customMessage: name + " in __all__");
        }

        var goTypes = (GoModels + GoClient + GoRuntime).Split('\n')
            .Where(x => x.StartsWith("type ", StringComparison.Ordinal))
            .Select(x => x[5..].Split(' ', '[')[0])
            .ToList();

        goTypes.ShouldBeUnique();

        foreach (var line in GoClient.Split('\n').Where(x => x.StartsWith("func ", StringComparison.Ordinal))) {
            foreach (var token in line.Split(' ', '(', ')', '*', '[', ']', ',')) {
                if (token.EndsWith("Resource", StringComparison.Ordinal) || token.EndsWith("Data", StringComparison.Ordinal)) {
                    goTypes.ShouldContain(token, line.Trim());
                }
            }
        }
    }

    /// <summary>
    ///     ⚠ <b>Two closed sets whose leaf names are equal declare two identifiers, not one twice.</b>
    /// </summary>
    /// <remarks>
    ///     The pair that gave <c>generated/sdk/2026-08-01.cs</c> its <c>CS0101</c>:
    ///     <c>CyberCloud.Cache/redis</c> has <c>mode</c> at <c>/properties/mode</c> and again at
    ///     <c>/properties/persistence/mode</c>. Python would let the second alias silently replace
    ///     the first; Go refuses the redeclaration at build.
    /// </remarks>
    [Fact]
    public void TwoClosedSetsWithTheSameLeafNameGetDistinctNames() {
        var document = OpenApiEmitter.Emit(Colliding(), ApiVersion.Parse(Fixtures.FirstVersion));
        var python = PythonSdkEmitter.Emit(document)[PythonModule + "models.py"];
        var go = GoSdkEmitter.Emit(document)[GoPackage + "models.go"];

        python.ShouldContain("\nServersMode = Literal[");
        python.ShouldContain("\nServersPersistenceMode = Literal[");
        Declarations(python, "class ").Concat(python.Split('\n').Where(x => x.Contains(" = Literal[", StringComparison.Ordinal)).Select(x => x.Split(' ')[0])).ShouldBeUnique();

        go.ShouldContain("\ntype ServersMode string\n");
        go.ShouldContain("\ntype ServersPersistenceMode string\n");
        Declarations(go, "type ").ShouldBeUnique();
    }

    /// <summary>
    ///     ⚠ <b>Both clients nest every container the document declares — the #79 convention,
    ///     in the one spelling each language has.</b>
    /// </summary>
    /// <remarks>
    ///     Python nests a class inside the class that holds it; Go has no nested types, so a
    ///     container is a named struct after its parent. Either way a leaf keeps the member name
    ///     the document gives it at its own depth, which is the property a flat class cannot have
    ///     and the one the .NET SDK shipped fourteen duplicate wire names without.
    /// </remarks>
    [Fact]
    public void BothClientsNestEveryContainerTheDocumentDeclares() {
        var containers = 0;

        foreach (var type in DocumentReader.TypesOf(Document)) {
            var model = SdkEmitter.ModelNames(DocumentReader.TypesOf(Document))[type.ResourceType];

            foreach (var leaf in DocumentReader.LeavesOf(type.Body)) {
                if (!leaf.IsObject) {
                    continue;
                }

                containers++;

                var depth = leaf.JsonPointer.Count(x => x == '/');
                var indent = new string(' ', 4 * depth);

                PythonModels.ShouldContain(
                    "\n" + indent + "class " + SdkEmitter.Pascal(leaf.Name) + ":\n",
                    customMessage: $"{type.ResourceType} {leaf.JsonPointer} on the Python client"
                );

                var goStruct = model + string.Concat(leaf.JsonPointer.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(SdkEmitter.Pascal));

                GoModels.ShouldContain(
                    "\ntype " + goStruct + " struct {\n",
                    customMessage: $"{type.ResourceType} {leaf.JsonPointer} on the Go client"
                );
            }
        }

        // The fixture declares /properties and /properties/sku on the server and /properties on the
        // database; a fixture with no container would make this a test of nothing.
        containers.ShouldBeGreaterThanOrEqualTo(3);

        // …and the nested leaf keeps its own wire name at its own depth, on both: /properties/sku/name
        // is `name` inside the sku class, beside no other `name`, so its closed set is the bare
        // {Model}Name — the "disambiguate only what collides" rule the other emitters apply.
        Block(PythonModels, "        class Sku:").ShouldContain("wire[\"name\"] = self.name");
        Block(GoModels, "type PostgreSQLServerPropertiesSku struct {").ShouldContain("Name PostgreSQLServerName `json:\"name\"`");
    }

    /// <summary>
    ///     ⚠ <b>Both clients read the envelope from the document, and neither writes it —
    ///     issue #85, on two more surfaces.</b>
    /// </summary>
    [Fact]
    public void BothClientsReadTheEnvelopeFromTheDocumentAndNeitherWritesIt() {
        var types = DocumentReader.TypesOf(Document);
        var names = SdkEmitter.ModelNames(types);

        PythonModels.ShouldContain("\nProvisioningState = Literal[");
        GoModels.ShouldContain("\ntype ProvisioningState string\n");
        GoModels.ShouldContain("\ntype Resource struct {\n");

        foreach (var type in types) {
            var envelope = DocumentReader.LeavesOf(type.Envelope);
            var served = DocumentReader.ReadRequiredOf(type.Envelope);
            var model = names[type.ResourceType];

            envelope.Length.ShouldBe(5, type.ResourceType);

            var pyResource = Block(PythonModels, "class " + model + "Resource:");
            var pyData = Block(PythonModels, "class " + model + "Data:");
            var goResource = Block(GoModels, "type " + model + "Resource struct {");
            var goData = Block(GoModels, "type " + model + "Data struct {");

            pyResource.ShouldContain("\n    data: " + model + "Data\n");
            goResource.ShouldContain("\n\tResource\n");
            goResource.ShouldContain("\n\tData " + model + "Data\n");

            foreach (var leaf in envelope) {
                var member = PythonSdkEmitter.Snake(leaf.Name);

                pyResource.ShouldContain("\n    " + member + ": ", customMessage: leaf.Name + " on the Python read model");
                pyResource.ShouldContain("=wire[\"" + leaf.Name + "\"]", customMessage: leaf.Name + " read off the wire");
                // At the write body's own depth — four spaces for a member, twelve for its from_wire
                // line — because a provider may nest a `name` of its own under /properties/sku, and
                // the fixture does.
                pyData.ShouldNotContain("\n    " + member + ":", customMessage: leaf.Name + " on the Python write body");
                pyData.ShouldNotContain("\n            " + member + "=", customMessage: leaf.Name + " read into the Python write body");

                // Present as a value, not a pointer, because the document says a read always carries it.
                served.ShouldContain(leaf.Name);
                Block(GoModels, "type Resource struct {").ShouldContain(" `json:\"" + leaf.Name + "\"`", customMessage: leaf.Name + " on the Go envelope");
                goData.ShouldNotContain("json:\"" + leaf.Name + "\"", customMessage: leaf.Name + " on the Go write body");
            }
        }
    }

    /// <summary>
    ///     ⚠ <b>A read-only leaf inside a body is read and never written, on both.</b>
    /// </summary>
    /// <remarks>
    ///     The write path refuses a read-only member rather than ignoring it, so a body read off a
    ///     <c>GET</c> and sent back on a <c>PUT</c> would be refused for a member the caller never
    ///     set. Python's <c>to_wire</c> leaves it out; Go's <c>MarshalJSON</c> clears it, which is
    ///     the only way <c>encoding/json</c> can be told.
    /// </remarks>
    [Fact]
    public void AReadOnlyLeafIsReadAndNeverWritten() {
        var properties = Block(PythonModels, "    class Properties:");

        properties.ShouldContain("provisioning_state=wire.get(\"provisioningState\")");
        properties.ShouldNotContain("wire[\"provisioningState\"]");

        var go = Block(GoModels, "type PostgreSQLServerProperties struct {");

        go.ShouldContain("ProvisioningState *string `json:\"provisioningState,omitempty\"`");
        GoModels.ShouldContain("func (v PostgreSQLServerProperties) MarshalJSON() ([]byte, error) {");
        GoModels.ShouldContain("\tstripped.ProvisioningState = nil\n");

        // …and a struct with nothing read-only has no such method to promote onto anything.
        GoModels.ShouldNotContain("func (v PostgreSQLServerData) MarshalJSON()");
    }

    /// <summary>⚠ <b>A path is built with every segment encoded, on both.</b></summary>
    [Fact]
    public void EveryPathSegmentIsEncoded() {
        var paths = 0;

        foreach (var line in PythonClient.Split('\n')) {
            if (!line.Contains("f\"/", StringComparison.Ordinal)) {
                continue;
            }

            paths++;

            for (var at = line.IndexOf('{'); at >= 0; at = line.IndexOf('{', at + 1)) {
                line[at..].ShouldStartWith("{_segment(", customMessage: line.Trim());
            }
        }

        foreach (var line in GoClient.Split('\n')) {
            if (!line.Contains("path := \"/", StringComparison.Ordinal)) {
                continue;
            }

            paths++;

            foreach (var term in line[(line.IndexOf('=') + 1)..].Split(" + ", StringSplitOptions.TrimEntries)) {
                (term.StartsWith('"') || term.StartsWith("segment(", StringComparison.Ordinal)).ShouldBeTrue(line.Trim());
            }
        }

        paths.ShouldBeGreaterThan(10);
    }

    /// <summary>⚠ <b>The scope API reaches both clients with no emitter change — issue #63.</b></summary>
    [Fact]
    public void TheScopeApiReachesBothClientsWithNoEmitterChange() {
        PythonModels.ShouldContain("class ScopeResource:");
        PythonModels.ShouldContain("class SubscriptionCreateContent:");
        PythonClient.ShouldContain("class SubscriptionsClient:");
        Block(PythonClient, "class SubscriptionsClient:").ShouldContain("def create(self, tenant_id: str, subscription_id: str, content: SubscriptionCreateContent) -> ScopeResource:");
        Block(PythonClient, "class TenantsClient:").ShouldNotContain("def create(");
        PythonClient.ShouldContain("self.tenants = TenantsClient(transport)");
        PythonClient.ShouldContain("self.resource_groups = ResourceGroupsClient(transport)");

        GoModels.ShouldContain("type ScopeResource struct {");
        GoModels.ShouldContain("type SubscriptionCreateContent struct {");
        GoClient.ShouldContain("func (c *SubscriptionsClient) Create(ctx context.Context, tenantID, subscriptionID string, content SubscriptionCreateContent) (*ScopeResource, error) {");
        GoClient.ShouldNotContain("func (c *TenantsClient) Create(");
        GoClient.ShouldContain("\tTenants ");
        GoClient.ShouldContain("\tResourceGroups ");
    }

    /// <summary>
    ///     ⚠ <b>Every long-running verb's 202 can be followed to the resource, on both, and the
    ///     poll speaks the document's own vocabulary.</b>
    /// </summary>
    [Fact]
    public void ALongRunningOperationCanBePolledThroughBothClients() {
        var states = DocumentReader.EnumOf(Document["components"]?["schemas"]?[OpenApiEmitter.OperationStateSchema] as JsonObject ?? []);

        states.ShouldNotBeEmpty();

        foreach (var state in states) {
            PythonModels.ShouldContain("\"" + state + "\"");
            GoModels.ShouldContain("\tOperationState" + state + " ");
        }

        PythonModels.ShouldContain("class OperationStatus:");
        PythonModels.ShouldContain("error: Optional[CyberCloudError] = None");
        PythonModels.ShouldContain("progress: List[OperationProgress]");
        PythonRuntime.ShouldContain("class Operation(Generic[T]):");
        PythonRuntime.ShouldContain("def poll(self) -> OperationStatus:");
        PythonRuntime.ShouldContain("def wait(self, on_progress: Optional[Callable[[OperationProgress], None]] = None");
        PythonRuntime.ShouldContain("accepted.header(\"azure-asyncoperation\")");
        PythonClient.ShouldContain("def begin_create_or_update(self, tenant_id: str, subscription_id: str, resource_group_name: str, resource_name: str, data: PostgreSQLServerData) -> Operation[PostgreSQLServerResource]:");
        PythonClient.ShouldContain("def begin_delete(self, tenant_id: str, subscription_id: str, resource_group_name: str, resource_name: str) -> Operation[None]:");
        PythonClient.ShouldContain("def begin_restart(self, tenant_id: str, subscription_id: str, resource_group_name: str, resource_name: str) -> Operation[PostgreSQLServerResource]:");
        PythonClient.ShouldContain("def get(self, operation_id: str) -> OperationStatus:");

        GoModels.ShouldContain("type OperationStatus struct {");
        GoModels.ShouldContain("Error *Error `json:\"error,omitempty\"`");
        GoRuntime.ShouldContain("func (o *Operation[T]) Poll(ctx context.Context) (*OperationStatus, error) {");
        GoRuntime.ShouldContain("func (o *Operation[T]) Wait(ctx context.Context, onProgress func(OperationProgress)) (*T, error) {");
        GoRuntime.ShouldContain("accepted.Header.Get(\"Azure-AsyncOperation\")");
        GoClient.ShouldContain("BeginCreateOrUpdate(ctx context.Context, tenantID, subscriptionID, resourceGroupName, resourceName string, data PostgreSQLServerData) (*Operation[PostgreSQLServerResource], error) {");
        GoClient.ShouldContain("BeginDelete(ctx context.Context, tenantID, subscriptionID, resourceGroupName, resourceName string) (*Operation[struct{}], error) {");
        GoClient.ShouldContain("BeginRestart(ctx context.Context, tenantID, subscriptionID, resourceGroupName, resourceName string) (*Operation[PostgreSQLServerResource], error) {");
        GoClient.ShouldContain("func (c *OperationsClient) Get(ctx context.Context, operationID string) (*OperationStatus, error) {");

        // ⚠ The id is the URL's last segment and the poll goes to the transport's endpoint, never
        // to the URL itself: a bearer token must not follow an origin the response chose.
        PythonRuntime.ShouldContain("\"/operations/\" + urllib_parse.quote(self.id, safe=\"\")");
        GoRuntime.ShouldContain("\"/operations/\" + url.PathEscape(o.id)");
    }

    /// <summary>
    ///     ⚠ <b>A listing pages by <c>$top</c> and the <c>nextLink</c>'s <c>$skipToken</c>, and
    ///     the link's origin is dropped.</b>
    /// </summary>
    /// <remarks>
    ///     The two names carry their sigil, which is what the gateway reads; a misspelling is not
    ///     an error anywhere but a <c>200</c> holding page one for ever. The link's query is sent
    ///     as members, so the transport's own <c>api-version</c> replaces the one the link
    ///     carried rather than being appended after it.
    /// </remarks>
    [Fact]
    public void APagedListFollowsTheSkipTokenAndNeverTheOrigin() {
        PythonClient.ShouldContain("def list(self, tenant_id: str, subscription_id: str, resource_group_name: str, *, top: Optional[int] = None) -> Pager[PostgreSQLServerResource]:");
        PythonRuntime.ShouldContain("{\"$top\": str(self._top)}");
        PythonRuntime.ShouldContain("wire.get(\"nextLink\")");
        PythonRuntime.ShouldContain("return Request(\"GET\", parts.path, dict(urllib_parse.parse_qsl(parts.query, keep_blank_values=True)))");

        GoClient.ShouldContain("List(tenantID, subscriptionID, resourceGroupName string, options *ListOptions) *Pager[PostgreSQLServerResource] {");
        GoRuntime.ShouldContain("query.Set(\"$top\", strconv.Itoa(options.Top))");
        GoRuntime.ShouldContain("NextLink *string `json:\"nextLink,omitempty\"`");
        GoRuntime.ShouldContain("return &Request{Method: \"GET\", Path: parsed.EscapedPath(), Query: parsed.Query()}, nil");
    }

    /// <summary>⚠ <b>The one error shape, with the document's codes, on both.</b></summary>
    [Fact]
    public void BothClientsCarryTheOneErrorShape() {
        var codes = DocumentReader.EnumOf(Document["components"]?["schemas"]?["ErrorCode"] as JsonObject ?? []);

        codes.ShouldNotBeEmpty();

        foreach (var code in codes) {
            PythonModels.ShouldContain("\"" + code + "\"");
            GoModels.ShouldContain("\tErrorCode" + code + " ");
        }

        Block(PythonModels, "class CyberCloudError:").ShouldContain("target: Optional[str] = None");
        Block(PythonModels, "class CyberCloudError:").ShouldContain("details: List[CyberCloudError]");
        PythonRuntime.ShouldContain("class RequestFailedError(Exception):");
        PythonRuntime.ShouldContain("isinstance(body.get(\"error\"), dict)");

        Block(GoModels, "type Error struct {").ShouldContain("Target *string `json:\"target,omitempty\"`");
        Block(GoModels, "type Error struct {").ShouldContain("Details []Error `json:\"details,omitempty\"`");
        GoRuntime.ShouldContain("type RequestFailedError struct {");
        GoRuntime.ShouldContain("func (e *RequestFailedError) Unwrap() error {");
    }

    /// <summary>
    ///     ⚠ <b>A secret action's response is a model and an action's own closed set is declared,
    ///     on both.</b>
    /// </summary>
    [Fact]
    public void AnActionsShapesAreDeclaredOnBothSurfaces() {
        PythonModels.ShouldContain("\nPostgreSQLServerListKeysContentKeyName = Literal[\"primary\", \"secondary\"]");
        Block(PythonModels, "class PostgreSQLServerListKeysResult:").ShouldContain("Secret material");
        PythonClient.ShouldContain("def list_keys(self, tenant_id: str, subscription_id: str, resource_group_name: str, resource_name: str, content: PostgreSQLServerListKeysContent) -> PostgreSQLServerListKeysResult:");

        GoModels.ShouldContain("\ntype PostgreSQLServerListKeysContentKeyName string\n");
        GoModels.ShouldContain("\tPostgreSQLServerListKeysContentKeyNamePrimary ");
        GoClient.ShouldContain("ListKeys(ctx context.Context, tenantID, subscriptionID, resourceGroupName, resourceName string, content PostgreSQLServerListKeysContent) (*PostgreSQLServerListKeysResult, error) {");
    }

    /// <summary>
    ///     ⚠ <b>A wire name that is a Python keyword is suffixed and still writes its own name; a
    ///     Go field never needs to be, because an exported name is never a keyword.</b>
    /// </summary>
    /// <remarks>
    ///     <c>CyberCloud.Cache/redis</c> declares <c>/properties/persistence/class</c>, which is
    ///     PEP 8's <c>class_</c> and nothing else can be. The wire name is what goes out.
    /// </remarks>
    [Fact]
    public void AKeywordWireNameIsSuffixedAndStillWritesItsOwnName() {
        var document = OpenApiEmitter.Emit(Colliding(), ApiVersion.Parse(Fixtures.FirstVersion));
        var python = PythonSdkEmitter.Emit(document)[PythonModule + "models.py"];
        var go = GoSdkEmitter.Emit(document)[GoPackage + "models.go"];

        python.ShouldContain("class_: Optional[str] = None");
        python.ShouldContain("class_=wire.get(\"class\")");
        python.ShouldContain("wire[\"class\"] = self.class_");
        python.ShouldNotContain("\n    class: ");

        go.ShouldContain("Class *string `json:\"class,omitempty\"`");

        PythonSdkEmitter.Snake("clusterId").ShouldBe("cluster_id");
        PythonSdkEmitter.Snake("storageGb").ShouldBe("storage_gb");
        PythonSdkEmitter.Snake("SQLServer").ShouldBe("sql_server");
        PythonSdkEmitter.Snake("managedClustersName").ShouldBe("managed_clusters_name");
        PythonSdkEmitter.Snake("2xlarge").ShouldBe("n2xlarge");
    }

    /// <summary>
    ///     ⚠ <b>Two leaves that are one identifier fail rather than declaring one name twice — on
    ///     both, for <c>SdkEmitter</c>'s reason.</b>
    /// </summary>
    [Fact]
    public void TwoSiblingsThatAreOneIdentifierFailRatherThanDeclaringOneNameTwice() {
        var registry = new FakeRegistry {
            Namespaces = [Fixtures.Namespace],
            Types = [
                new ResourceTypeRegistration {
                    Type = new(Fixtures.Namespace, "servers"),
                    ApiVersions = [
                        new(
                            ApiVersion.Parse(Fixtures.FirstVersion),
                            ResourceSchema.Of(
                                [
                                    new("/properties", SchemaKind.Nested, Required: true),
                                    new("/properties/maxMemory", SchemaKind.Text),
                                    new("/properties/max_memory", SchemaKind.Text)
                                ]
                            )
                        )
                    ]
                }
            ]
        };

        var document = OpenApiEmitter.Emit(registry, ApiVersion.Parse(Fixtures.FirstVersion));

        Should.Throw<InvalidOperationException>(() => PythonSdkEmitter.Emit(document)).Message.ShouldContain("max_memory");
        Should.Throw<InvalidOperationException>(() => GoSdkEmitter.Emit(document)).Message.ShouldContain("MaxMemory");
    }

    /// <summary>
    ///     ⚠ <b>Both packages are written under <c>generated/</c>, one row per file, and the
    ///     self-check is reported once.</b>
    /// </summary>
    [Fact]
    public void DerivedSurfacesWritesBothPackagesAndReportsEveryFile() {
        var directory = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

        try {
            var report = DerivedSurfaces.Generate(
                new Dictionary<string, JsonObject> { [Fixtures.FirstVersion] = Document },
                directory,
                write: true
            );

            var python = report.Documents.Where(x => x.Surface == PythonSdkEmitter.DirectoryName).Select(x => x.FileName).ToList();
            var go = report.Documents.Where(x => x.Surface == GoSdkEmitter.DirectoryName).Select(x => x.FileName).ToList();

            python.ShouldBe(
                [
                    "sdk-python/cybercloud/__init__.py",
                    "sdk-python/cybercloud/py.typed",
                    "sdk-python/cybercloud/v2026_08_01/__init__.py",
                    "sdk-python/cybercloud/v2026_08_01/_runtime.py",
                    "sdk-python/cybercloud/v2026_08_01/client.py",
                    "sdk-python/cybercloud/v2026_08_01/models.py",
                    "sdk-python/pyproject.toml"
                ]
            );

            go.ShouldBe(["sdk-go/api20260801/client.go", "sdk-go/api20260801/models.go", "sdk-go/api20260801/runtime.go", "sdk-go/go.mod"]);

            foreach (var file in python.Concat(go)) {
                File.Exists(Path.Combine(directory, file.Replace('/', Path.DirectorySeparatorChar))).ShouldBeTrue(file);
            }

            report.Documents.SelectMany(x => x.Problems).ShouldBeEmpty();
            report.Stale.ShouldBeEmpty();

            // A second run over the same tree is clean: nothing drifted, nothing is new.
            DerivedSurfaces.Generate(new Dictionary<string, JsonObject> { [Fixtures.FirstVersion] = Document }, directory, write: false).IsClean.ShouldBeTrue();
        } finally {
            if (Directory.Exists(directory)) {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void BothSurfacesAreStableAcrossRuns() {
        foreach (var file in Python) {
            PythonSdkEmitter.Emit(Document)[file.Key].ShouldBe(file.Value, file.Key);
        }

        foreach (var file in Go) {
            GoSdkEmitter.Emit(Document)[file.Key].ShouldBe(file.Value, file.Key);
        }
    }

    /// <summary>The text of one declaration, from its opening line to the first line that is a bare <c>}</c> or the next top-level declaration.</summary>
    static string Block(string source, string opening) {
        var start = source.IndexOf(opening, StringComparison.Ordinal);
        start.ShouldBeGreaterThanOrEqualTo(0, opening);

        var end = opening.StartsWith("type ", StringComparison.Ordinal)
            ? source.IndexOf("\n}\n", start, StringComparison.Ordinal)
            : source.IndexOf("\n\n\n", start, StringComparison.Ordinal);

        // The newline that ends the block's last line is part of the block, so the last member
        // can be asserted with the same "\n…\n" shape as the others.
        return source[start..(end < 0 ? source.Length : end + 1)];
    }

    static IEnumerable<string> Declarations(string source, string keyword) =>
        source.Split('\n')
            .Where(x => x.StartsWith(keyword, StringComparison.Ordinal))
            .Select(x => x[keyword.Length..].Split(' ')[0].TrimEnd(':', '{', ' '));

    /// <summary>A body with <c>mode</c> at two depths, both closed, and a leaf named <c>class</c>.</summary>
    static FakeRegistry Colliding() =>
        new() {
            Namespaces = [Fixtures.Namespace],
            Types = [
                new ResourceTypeRegistration {
                    Type = new(Fixtures.Namespace, "servers"),
                    ApiVersions = [
                        new(
                            ApiVersion.Parse(Fixtures.FirstVersion),
                            ResourceSchema.Of(
                                [
                                    new("/properties", SchemaKind.Nested, Required: true),
                                    new("/properties/mode", SchemaKind.Text, Description: "The top-level one.") {
                                        AllowedValues = ["Sentinel", "Standalone"]
                                    },
                                    new("/properties/persistence", SchemaKind.Nested),
                                    new("/properties/persistence/mode", SchemaKind.Text, Description: "The nested one.") {
                                        AllowedValues = ["None", "RDB", "AOF"]
                                    },
                                    new("/properties/persistence/class", SchemaKind.Text, Description: "A StorageClass name.")
                                ]
                            )
                        )
                    ]
                }
            ]
        };
}

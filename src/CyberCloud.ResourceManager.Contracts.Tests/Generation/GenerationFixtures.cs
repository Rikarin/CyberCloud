using System.Collections.Immutable;

namespace CyberCloud.ResourceManager.Contracts.Tests.Generation;

/// <summary>
///     A registry built by hand, so the generation tests do not need <c>ProviderRegistry</c> and the
///     Orleans-referencing assembly it lives in.
/// </summary>
/// <remarks>
///     ⚠ This is a <i>test double for the shape</i>, not a second implementation of the registry.
///     Everything under test reads <see cref="IProviderRegistry.Types" /> and
///     <see cref="IProviderRegistry.Namespaces" /> and nothing else — an emitter that needed
///     <see cref="IProviderRegistry.Resolve" /> would be doing request-time work at build time, which
///     is why that member throws here rather than being implemented.
/// </remarks>
sealed class FakeRegistry : IProviderRegistry {
    public ImmutableArray<ResourceTypeRegistration> Types { get; init; } = [];

    public ImmutableArray<string> Namespaces { get; init; } = [];

    public bool TryGetType(ResourceTypeName type, out ResourceTypeRegistration registration) {
        foreach (var candidate in Types) {
            if (candidate.Type == type) {
                registration = candidate;
                return true;
            }
        }

        registration = null!;
        return false;
    }

    public Result<TypeResolution> Resolve(ResourceTypeName type, string? apiVersion) =>
        throw new NotSupportedException(
            "Generation reads the registry's declared types and never resolves a request against it."
        );
}

/// <summary>
///     The registries the generation tests run against — docs/plan/08 § The provider registry's own
///     Postgres example, written out.
/// </summary>
static class Fixtures {
    public const string Namespace = "CyberCloud.DBforPostgreSQL";
    public const string FirstVersion = "2026-08-01";
    public const string SecondVersion = "2027-01-01";

    /// <summary>A registry with nothing in it.</summary>
    public static IProviderRegistry Empty { get; } = new FakeRegistry();

    /// <summary>
    ///     One provider, two types — one of them nested — two api-versions, actions, secrets and a
    ///     read-only property.
    /// </summary>
    public static IProviderRegistry Postgres() =>
        new FakeRegistry {
            Namespaces = [Namespace],
            Types = [
                new ResourceTypeRegistration {
                    Type = new(Namespace, "servers"),
                    ApiVersions = [
                        new(ApiVersion.Parse(FirstVersion), ServerSchema()),
                        new(ApiVersion.Parse(SecondVersion), ServerSchema())
                    ],
                    Actions = [
                        // Long-running and declaring nothing: `restart` does work, so it answers 202
                        // like every other write rather than a 200 the emitter used to assume.
                        new("restart", ActionKind.Post, "write", Secret: false) { LongRunning = true },
                        // ⚠ The listKeys case ADR-012's action gap was named for: secrets of known
                        // shape. The response schema is what makes "which values leave the platform"
                        // a reviewable fact rather than a read of the handler.
                        new("listKeys", ActionKind.Post, "listKeys", Secret: true) {
                            Request = ResourceSchema.Of([
                                new("/keyName", SchemaKind.Text, Description: "Which key to read.") {
                                    AllowedValues = ["primary", "secondary"]
                                }
                            ]),
                            Response = ResourceSchema.Of([
                                new("/primary", SchemaKind.Text, Required: true, Secret: true),
                                new("/secondary", SchemaKind.Text, Required: true, Secret: true)
                            ])
                        }
                    ],
                    Display = new("PostgreSQL server", "PostgreSQL servers", "postgres", "A managed Postgres."),
                    ClusterIdPointer = ClusterPlacement.DefaultPointer,
                    Meters = [
                        new(QuotaMeter.Vcpu, "/properties/sku/vcpu", 1m),
                        new(QuotaMeter.StorageGb, "/properties/storageGb", 32m)
                    ],
                    Chart = "managed/postgres",
                    SoftDeleteDays = 7,
                    SupportsTags = true,
                    RequiresCluster = true
                },
                new ResourceTypeRegistration {
                    // ⚠ A nested type, so the emitted path exercises the platform's own id grammar —
                    // /servers/{serversName}/databases/{resourceName}, interleaved exactly as Azure
                    // spells it and exactly as ResourceId.Path renders it. docs/plan/12 § Child
                    // resources records why that is the shape.
                    Type = new(Namespace, "servers/databases"),
                    ApiVersions = [new(ApiVersion.Parse(FirstVersion), DatabaseSchema())]
                }
            ]
        };

    /// <summary>
    ///     docs/plan/08's Postgres example, with every expressiveness the registry has.
    /// </summary>
    /// <remarks>
    ///     ⚠ <c>/properties/sku/name</c> is <b>the</b> case ADR-012's enum gap was named for: four
    ///     values a body may carry, which the schema could not say and the validator could not refuse.
    ///     <c>/properties/allowedRanges</c> is the array gap — an element kind and the element's own
    ///     pattern, where <c>items: {}</c> used to be.
    /// </remarks>
    public static ResourceSchema ServerSchema() =>
        ResourceSchema.Of([
            new("/location", SchemaKind.Text, Required: true, Description: "Where the server runs.") {
                Format = SchemaFormat.Region,
                Widget = WidgetHint.Region,
                Immutable = true
            },
            new("/properties", SchemaKind.Nested, Required: true),
            // ⚠ The property RequiresCluster names. ProviderBuilder refuses a type that declares the
            // flag without it, so a fixture that claims RequiresCluster must carry it too.
            new("/properties/clusterId", SchemaKind.Text, Required: true, Description: "The cluster.") {
                Format = SchemaFormat.Uuid,
                Widget = WidgetHint.Cluster,
                Immutable = true
            },
            new("/properties/sku", SchemaKind.Nested, Required: true),
            new("/properties/sku/name", SchemaKind.Text, Required: true, Description: "The sku.") {
                AllowedValues = ["s1.small", "s1.large", "c1.large", "m1.large"],
                Widget = WidgetHint.Sku,
                ExampleJson = "\"s1.large\""
            },
            new("/properties/sku/vcpu", SchemaKind.WholeNumber, Required: true, Description: "vCPUs.") {
                Minimum = 1,
                Maximum = 64
            },
            new("/properties/storageGb", SchemaKind.WholeNumber, Description: "Storage, in GB.") {
                Minimum = 32,
                Maximum = 16384,
                DefaultJson = "32"
            },
            new("/properties/highAvailability", SchemaKind.Boolean) { DefaultJson = "false" },
            // ⚠ A closed set with NO widget hint, so the portal emitter's shape-based fallback is
            // exercised as well as the declared-hint path. `sku/name` declares WidgetHint.Sku and
            // takes the picker; this one has to fall through to docs/plan/20's "≤ 8 is a select".
            new("/properties/tier", SchemaKind.Text, Description: "Support tier.") {
                AllowedValues = ["free", "standard", "premium"]
            },
            new("/properties/adminPassword", SchemaKind.Text, Secret: true, Description: "The password.") {
                MinLength = 12,
                Widget = WidgetHint.SecretRef
            },
            new("/properties/provisioningState", SchemaKind.Text, ReadOnly: true, Description: "State."),
            new("/properties/retiredOn", SchemaKind.Text, Description: "When it was retired, or null.") {
                Nullable = true,
                Format = SchemaFormat.DateTime
            },
            new("/properties/allowedRanges", SchemaKind.Array, Description: "CIDR ranges.") {
                ElementKind = SchemaKind.Text,
                Pattern = @"\d{1,3}(\.\d{1,3}){3}/\d{1,2}",
                Widget = WidgetHint.Cidr
            }
        ]);

    public static ResourceSchema DatabaseSchema() =>
        ResourceSchema.Of([
            new("/properties", SchemaKind.Nested, Required: true),
            new("/properties/charset", SchemaKind.Text, Description: "The character set.")
        ]);

    /// <summary>The Postgres registry with its first api-version under a retirement notice.</summary>
    public static IProviderRegistry RetiringPostgres(DateOnly retiresOn) =>
        new FakeRegistry {
            Namespaces = [Namespace],
            Types = [
                new ResourceTypeRegistration {
                    Type = new(Namespace, "servers"),
                    ApiVersions = [new(ApiVersion.Parse(FirstVersion), ServerSchema())],
                    Actions = [new("restart", ActionKind.Post, "write", Secret: false)],
                    RetiredOn = new Dictionary<ApiVersion, DateOnly> {
                        [ApiVersion.Parse(FirstVersion)] = retiresOn
                    }.ToImmutableDictionary()
                }
            ]
        };

    /// <summary>The Postgres registry with one type's actions replaced.</summary>
    public static IProviderRegistry PostgresWithActions(ImmutableArray<ActionRegistration> actions) =>
        new FakeRegistry {
            Namespaces = [Namespace],
            Types = [
                new ResourceTypeRegistration {
                    Type = new(Namespace, "servers"),
                    ApiVersions = [new(ApiVersion.Parse(FirstVersion), ServerSchema())],
                    Actions = actions
                }
            ]
        };

    /// <summary>The Postgres registry with one type's schema replaced, for the diff tests.</summary>
    public static IProviderRegistry PostgresWith(ResourceSchema schema) =>
        new FakeRegistry {
            Namespaces = [Namespace],
            Types = [
                new ResourceTypeRegistration {
                    Type = new(Namespace, "servers"),
                    ApiVersions = [new(ApiVersion.Parse(FirstVersion), schema)]
                }
            ]
        };
}

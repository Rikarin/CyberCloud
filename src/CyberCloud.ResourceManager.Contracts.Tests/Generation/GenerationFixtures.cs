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
                        new("restart", ActionKind.Post, "write", Secret: false),
                        new("listKeys", ActionKind.Post, "listKeys", Secret: true)
                    ],
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
                    // /servers/databases/{resourceName}, one name at the end, which is what
                    // ResourceId.Path renders and is not Azure's interleaved shape.
                    Type = new(Namespace, "servers/databases"),
                    ApiVersions = [new(ApiVersion.Parse(FirstVersion), DatabaseSchema())]
                }
            ]
        };

    public static ResourceSchema ServerSchema() =>
        ResourceSchema.Of([
            new("/location", SchemaKind.Text, Required: true, Description: "Where the server runs."),
            new("/properties", SchemaKind.Nested, Required: true),
            new("/properties/sku", SchemaKind.Nested, Required: true),
            new("/properties/sku/name", SchemaKind.Text, Required: true, Description: "The sku."),
            new("/properties/sku/vcpu", SchemaKind.WholeNumber, Required: true, Description: "vCPUs."),
            new("/properties/storageGb", SchemaKind.WholeNumber, Description: "Storage, in GB."),
            new("/properties/highAvailability", SchemaKind.Boolean),
            new("/properties/adminPassword", SchemaKind.Text, Secret: true, Description: "The password."),
            new("/properties/provisioningState", SchemaKind.Text, ReadOnly: true, Description: "State."),
            new("/properties/allowedRanges", SchemaKind.Array, Description: "CIDR ranges.")
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

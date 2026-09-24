using CyberCloud.Authorization;
using CyberCloud.Authorization.Contracts;
using CyberCloud.ResourceManager;
using CyberCloud.ResourceManager.Registry;
using System.Reflection;

namespace CyberCloud.Providers.KeyVault.Tests;

/// <summary>
///     What the family declares, checked the way a silo checks it at start — and the three things
///     it shares with other assemblies by spelling rather than by reference.
/// </summary>
public sealed class KeyVaultDeclarationTests {
    static ResourceTypeRegistration Registration {
        get {
            ProviderRegistry.Build([new KeyVaultProvider()]).TryGetType(KeyVaults.Type, out var registration).ShouldBeTrue();
            return registration;
        }
    }

    [Fact]
    public void EveryActionIsSynchronousHandledAndChecksADataPlanePermission() {
        var declared = Registration.Actions.Where(static x => !SoftDeletePolicy.IsReserved(x.Name)).ToList();

        declared.Count.ShouldBe(25);

        foreach (var action in declared) {
            action.HandlerType.ShouldBe(typeof(KeyVaultActionHandler), action.Name);
            action.LongRunning.ShouldBeFalse($"{action.Name} is long-running, so its result would travel on an operation record any reader can poll");
            action.Response.ShouldNotBeNull($"{action.Name} declares no response shape");
            KeyVaults.DataPlanePermissions.ShouldContain(action.Permission, $"{action.Name} checks '{action.Permission}', a control-plane permission");
        }
    }

    [Theory]
    [InlineData(KeyVaults.GetSecretAction)]
    [InlineData(KeyVaults.DecryptAction)]
    [InlineData(KeyVaults.UnwrapKeyAction)]
    public void AnActionThatReturnsAValueOrAPlaintextIsSecret(string name) {
        // ⚠ secret: true is what makes the resource manager check FullyConsistent — a revoked Secrets
        // User must not read a value off a stale cache (docs/plan/18 § The resource model).
        Registration.TryGetAction(name, out var action).ShouldBeTrue();
        action.Secret.ShouldBeTrue();
    }

    [Theory]
    [InlineData(KeyVaults.SetSecretAction, "/value", true)]
    [InlineData(KeyVaults.ImportKeyAction, "/pkcs8", true)]
    [InlineData(KeyVaults.EncryptAction, "/value", true)]
    [InlineData(KeyVaults.WrapKeyAction, "/value", true)]
    [InlineData(KeyVaults.DecryptAction, "/value", false)]
    [InlineData(KeyVaults.UnwrapKeyAction, "/value", false)]
    public void AnInputThatCarriesAPlaintextIsMarkedSecret(string name, string field, bool secret) {
        // ⚠ Field-level Secret is what gives the input writeOnly in OpenAPI and a masked field in the
        // portal and cyc. A plaintext going into encrypt or wrapKey is as secret as the one coming back
        // out of decrypt or unwrapKey, and the ciphertext going the other way is not.
        Registration.TryGetAction(name, out var action).ShouldBeTrue();

        action.Request.ShouldNotBeNull()
            .Properties.Single(x => x.JsonPointer == field)
            .Secret.ShouldBe(secret, $"{name}'s {field}");
    }

    [Fact]
    public void AVaultCallNeverPrintsItsBody() {
        // ⚠ A record's generated ToString prints every property, and Body is a secret's value, a
        // plaintext or a private key. A log template or a Shouldly message formats it that way.
        var call = new VaultCall {
            Action = KeyVaults.SetSecretAction, Body = """{"secretName":"db","value":"hunter2-not-for-logs"}""", TraceId = "abc"
        };

        var printed = call.ToString();

        printed.ShouldNotContain("hunter2-not-for-logs");
        printed.ShouldContain(KeyVaults.SetSecretAction);
        printed.ShouldContain("abc");
    }

    [Fact]
    public void TheVaultHasASevenDayWindowAndItsPurgeProtectionIsTheBodysFlag() {
        Registration.SoftDeleteDays.ShouldBe(7);
        Registration.PurgeProtectionPointer.ShouldBe(KeyVaults.PurgeProtectionPointer);
        Registration.RequiresCluster.ShouldBeFalse();
    }

    [Fact]
    public void TheSixPermissionsAreTheSchemasAndNoControlPlaneRoleHoldsThem() {
        // ⚠ The provider spells them without referencing the schema; a permission the schema does not
        // define evaluates false for every caller, which is how `purge` shipped broken (docs/plan/07).
        KeyVaults.DataPlanePermissions.ShouldBe(
            [
                Permissions.ReadSecrets, Permissions.WriteSecrets, Permissions.PurgeSecrets, Permissions.UseKeys,
                Permissions.WriteKeys, Permissions.PurgeKeys
            ]
        );

        var resource = CyberCloudSchema.Instance.Type(ObjectTypes.Resource).ShouldNotBeNull();

        foreach (var permission in KeyVaults.DataPlanePermissions) {
            var member = resource.Member(permission).ShouldNotBeNull($"'{permission}' is not defined on resource");
            member.IsPermission.ShouldBeTrue();

            // ⚠ The second half of the name: walk every relation the permission can reach, on this
            // type and up the parent chain, and none may be a control-plane role. A Rel(owner) or a
            // From(parent, contributor) anywhere under it would hand the vault's contents to whoever
            // manages it.
            var reached = Reachable(permission);

            foreach (var controlPlane in new[] { Relations.Owner, Relations.Contributor, Relations.Reader }) {
                reached.ShouldNotContain(controlPlane, $"'{permission}' is reachable from '{controlPlane}': {string.Join(", ", reached)}");
            }
        }
    }

    [Fact]
    public void TheWalkThatProvesNoControlPlaneRoleHoldsThemSeesOneThatDoes() {
        // Without this the test above could pass because the walk finds nothing. `write` is
        // Rel(contributor), which is Rel(owner) in turn.
        var reached = Reachable(Permissions.Write);

        reached.ShouldContain(Relations.Contributor);
        reached.ShouldContain(Relations.Owner);
    }

    /// <summary>
    ///     Every relation name a member of <c>resource</c> can be computed from, followed through
    ///     <c>Rel</c> on the same type and <c>From(parent, …)</c> onto every scope type above it.
    /// </summary>
    /// <remarks>
    ///     A negated operand is not followed: <c>!suspended</c> takes a permission away and never
    ///     grants one.
    /// </remarks>
    static SortedSet<string> Reachable(string permission) {
        var schema = CyberCloudSchema.Instance;
        var reached = new SortedSet<string>(StringComparer.Ordinal);
        var pending = new Stack<(string Type, string Name)>([(ObjectTypes.Resource, permission)]);
        var seen = new HashSet<(string, string)>();

        while (pending.TryPop(out var next)) {
            if (!seen.Add(next) || schema.Member(next.Type, next.Name) is not { } member) {
                continue;
            }

            reached.Add(next.Name);

            foreach (var expression in Granting(member.Expression)) {
                switch (expression) {
                    case RelationRefExpression relation:
                        pending.Push((next.Type, relation.Relation));
                        break;

                    case TuplesetExpression from:
                        foreach (var parent in schema.TypeNames.Where(x => schema.Member(x, from.Computed) is not null)) {
                            pending.Push((parent, from.Computed));
                        }

                        break;
                }
            }
        }

        return reached;
    }

    static IEnumerable<RelationExpression> Granting(RelationExpression expression) {
        if (expression is ExclusionExpression) {
            yield break;
        }

        yield return expression;

        foreach (var child in expression.Children) {
            foreach (var granting in Granting(child)) {
                yield return granting;
            }
        }
    }

    [Fact]
    public void TheFourDataPlaneRolesAreGrantableOnEveryScopeAndDroppedByASoftDelete() {
        string[] roles = [
            Relations.KeyVaultSecretsOfficer, Relations.KeyVaultSecretsUser, Relations.KeyVaultCryptoOfficer,
            Relations.KeyVaultCryptoUser
        ];

        foreach (var type in new[] {
                     ObjectTypes.Tenant, ObjectTypes.ManagementGroup, ObjectTypes.Subscription, ObjectTypes.ResourceGroup,
                     ObjectTypes.Resource
                 }) {
            var defined = CyberCloudSchema.Instance.Type(type).ShouldNotBeNull();

            foreach (var role in roles) {
                defined.Roles.ShouldContain(role, $"'{type}' does not declare '{role}' as a role");
            }
        }

        foreach (var role in roles) {
            RoleAssignmentService.GrantableRoles.ShouldContain(role);
            ReBacResourceRelationWriter.DirectRoles.ShouldContain(role);
        }
    }

    [Fact]
    public void EveryWireTypeCarriesAnAlias() {
        // ⚠ The handler runs in the gateway and the grain on a silo; an in-process TestCluster shares
        // one type manifest and would never notice a missing alias — which is how #39 shipped one.
        var assemblies = new[] { typeof(IKeyVaultGrain).Assembly, typeof(KeyVaultGrain).Assembly };

        var wire = assemblies.SelectMany(static x => x.GetTypes())
            .Where(static x => x.GetCustomAttribute<GenerateSerializerAttribute>() is not null
                || (x.IsInterface && typeof(IGrain).IsAssignableFrom(x)))
            .ToList();

        wire.Count.ShouldBe(7, string.Join(", ", wire.Select(static x => x.Name)));

        foreach (var type in wire) {
            var alias = type.GetCustomAttribute<AliasAttribute>();
            alias.ShouldNotBeNull($"{type.FullName} crosses the gateway-to-silo boundary without an [Alias]");
            alias.Alias.ShouldStartWith("CyberCloud.KeyVault.");
        }
    }
}

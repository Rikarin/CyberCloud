using System.Reflection;

namespace CyberCloud.Vault.Tests;

/// <summary>
///     Where a secret cannot reach from this assembly, asserted by reflection over what it compiles.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>CC1005 IS SWITCHED OFF IN <c>CyberCloud.Vault</c>, WHICH IS WHY THESE ARE TESTS AND
///         NOT A COMMENT SAYING "THE ANALYZER COVERS IT".</b>
///         <c>SecretInGrainStateAnalyzer.OnCompilationStart</c> returns early when
///         <c>Compilation.AssemblyName</c> matches <c>WellKnown.VaultAssembly</c> exactly or by
///         dotted prefix — that exemption is the reason this assembly is allowed to hold a value at
///         all. So the one place in the tree with the most to gain from the rule is the one place it
///         does not run, and everything it would have caught has to be caught here.
///     </para>
///     <para>
///         ⚠ <b>And CC1005 would not have caught most of this even where it runs.</b> It matches on
///         a <i>name suffix</i> — <c>Password</c>, <c>Secret</c>, <c>Token</c>, <c>Key</c>, ordinal —
///         on a member carrying <c>[Id]</c>. The OTP work found a member called <c>Code</c> holding a
///         one-time code that matched none of the four and had to assert the absence by reflecting
///         over serialized state instead. A field here called <c>value</c>, <c>cached</c> or
///         <c>last</c> would be equally invisible, and one of those names is what somebody adding a
///         cache would reach for.
///     </para>
/// </remarks>
public sealed class SecretContainmentTests {
    static readonly Assembly Vault = typeof(OpenBaoSecretResolver).Assembly;

    [Fact]
    public void NothingInThisAssemblyIsOrleansSerializable() {
        // ⚠ THE BROADEST CLAIM THE ASSEMBLY MAKES, AND THE CHEAPEST ONE TO HOLD. docs/plan/05 § What
        // is not in a grain keeps secrets out of the durable tier, and an assembly with no
        // [GenerateSerializer] and no [Id] has nothing Orleans could write to storage or put on the
        // wire — not by mistake, not by a later edit that adds a field to an existing type.
        //
        // This goes red the moment somebody makes any type here a grain-state class, which is the
        // point: a vault client with persistent state is a vault client with a secret in the durable
        // tier, and that is the one rule docs/plan/00 § Non-negotiables calls non-negotiable.
        var serializable = Vault
            .GetTypes()
            .Where(x => x.GetCustomAttributes().Any(a => a.GetType().Name is "GenerateSerializerAttribute"))
            .Select(x => x.FullName)
            .ToArray();

        serializable.ShouldBeEmpty(
            "CyberCloud.Vault must declare nothing Orleans can serialize — see the remarks"
        );

        var identified = Vault
            .GetTypes()
            .SelectMany(x => x.GetMembers(Flags))
            .Where(x => x.GetCustomAttributes().Any(a => a.GetType().Name is "IdAttribute"))
            .Select(x => $"{x.DeclaringType?.Name}.{x.Name}")
            .ToArray();

        identified.ShouldBeEmpty(
            "an [Id]-annotated member here is a member of some grain's state, and CC1005 is not "
            + "running in this assembly to say so"
        );
    }

    [Fact]
    public void TheResolverHoldsNothingAValueCouldBeAssignedTo() {
        // ⚠ THE ASSERTION THAT A VALUE CACHE WOULD GO RED AGAINST, AND IT IS DELIBERATELY A WHITELIST
        // RATHER THAN A BLACKLIST. Naming the forbidden types is the version that fails: a cache
        // written as a ConcurrentDictionary<SecretRef, string>, a MemoryCache, a byte[] or a
        // "last resolved" field all hold a value and only the first two look like it.
        //
        // OpenBaoSecretResolver's remarks give three reasons not to cache — blast radius, rotation,
        // revocation. This is what makes them a property of the code rather than a paragraph.
        FieldTypes(typeof(OpenBaoSecretResolver)).ShouldBe(
            [
                typeof(HttpClient),
                typeof(IVaultTokenSource),
                typeof(VaultOptions),
                typeof(Microsoft.Extensions.Logging.ILogger<OpenBaoSecretResolver>),
            ],
            ignoreOrder: true,
            "the resolver's fields are its collaborators and nothing else; a resolved value lives in "
            + "a local for the length of one call. Adding a field here means a plaintext secret "
            + "living as long as the silo does"
        );
    }

    [Fact]
    public void TheOneFieldInThisAssemblyThatHoldsACredentialIsTheLeasedToken() {
        // ⚠ THE HONEST VERSION OF "NOTHING IS CACHED": ONE THING IS, AND THIS NAMES IT.
        //
        // KubernetesVaultTokenSource caches the login OpenBao issued, for the lease OpenBao chose.
        // That is a different decision from caching a value and the remarks on the resolver argue
        // the asymmetry. What this row does is make the exception exactly one field wide, so a
        // second one has to be argued for rather than added.
        FieldTypes(typeof(KubernetesVaultTokenSource)).ShouldBe(
            [
                typeof(HttpClient),
                typeof(VaultOptions),
                typeof(CyberCloud.Core.Time.IClock),
                typeof(SemaphoreSlim),
                typeof(VaultToken),
            ],
            ignoreOrder: true,
            "the token source holds one credential — the leased token — and its collaborators"
        );
    }

    [Fact]
    public void NoRefusalCanBeHandedTheValue() {
        // ⚠ VaultRefusal travels to a tenant. Nothing that builds one may take the secret, and the
        // way to keep that true across every future builder is to assert it of the class rather than
        // of the five methods that exist today.
        //
        // string parameters are allowed and are the whole design — a path, a field name, an address,
        // a role. What is asserted is that none of the builders is even OFFERED the value, which is
        // the difference between "we are careful with it" and "we do not have it".
        foreach (var method in typeof(VaultFailures).GetMethods(BindingFlags.Public | BindingFlags.Static)) {
            foreach (var parameter in method.GetParameters()) {
                parameter.Name.ShouldNotBe(
                    "value",
                    $"VaultFailures.{method.Name} takes a parameter called 'value'. Every refusal "
                    + "here is built either before a value is read or after a read that produced "
                    + "none; a builder that accepts one is a refusal that can carry a secret to a "
                    + "tenant"
                );

                parameter.Name.ShouldNotBe("secret", $"VaultFailures.{method.Name}");
                parameter.Name.ShouldNotBe("password", $"VaultFailures.{method.Name}");
            }
        }
    }

    const BindingFlags Flags =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

    /// <summary>Every instance field a type declares, including compiler-generated backing fields.</summary>
    /// <param name="type">The type to look at.</param>
    /// <remarks>
    ///     ⚠ Primary-constructor parameters become fields, so a captured parameter shows up here —
    ///     which is exactly what should be checked. A resolver that captured a <c>string</c> would
    ///     be indistinguishable, from a caller, from one that did not.
    /// </remarks>
    static Type[] FieldTypes(Type type) =>
        type.GetFields(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance)
            .Select(x => Nullable.GetUnderlyingType(x.FieldType) ?? x.FieldType)
            .ToArray();
}

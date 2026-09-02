using CyberCloud.Core.Resources;
using Shouldly;

namespace CyberCloud.Core.Tests;

/// <summary>
///     <see cref="ResourceCollectionId" /> — the address of a collection, and its relationship to
///     <see cref="ResourceId" />.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The claim under test is that the two grammars <i>partition</i> the paths that carry
///         the fixed prefix.</b> A resource's tail is even because it ends on a name; a collection's
///         is odd because it ends on a type. If either parser ever accepted the other's shape, the
///         choice between them would move to whatever the caller happened to try first — and the
///         gateway decides which one a path is without looking at the verb.
///     </para>
///     <para>
///         ⚠ <b>The fixed prefix is unchanged and that is asserted rather than assumed.</b>
///         <c>SoftDeletePolicy.RestoreAction</c>'s remarks record that a subscription-scoped
///         collection cannot be built because <c>ResourceId.ParsePath</c> has
///         <c>const int fixedPrefix = 8</c>. This type does not change that: every address below
///         still carries a tenant, a subscription and a resource group.
///     </para>
/// </remarks>
public sealed class ResourceCollectionIdTests {
    static readonly Guid Tenant = Guid.Parse("2b4a1c66-2e70-4a9d-9d0a-1f7ec1f1a4b3");
    static readonly Guid Subscription = Guid.Parse("9f1c8f0e-1b2a-4c3d-8e5f-6a7b8c9d0e1f");

    // ── The grammar ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ATopLevelCollectionEndsOnItsType() {
        var collection = new ResourceCollectionId(
            Tenant,
            Subscription,
            "prod",
            new("CyberCloud.DBforPostgreSQL", "servers")
        );

        collection.Path.ShouldBe(
            $"/tenants/{Tenant:D}/subscriptions/{Subscription:D}/resourceGroups/prod"
            + "/providers/CyberCloud.DBforPostgreSQL/servers"
        );
    }

    [Fact]
    public void ANestedCollectionInterleavesAndEndsOnItsType() {
        var collection = new ResourceCollectionId(
            Tenant,
            Subscription,
            "prod",
            new("CyberCloud.DBforPostgreSQL", "servers/databases"),
            "pg-main"
        );

        collection.Path.ShouldBe(
            $"/tenants/{Tenant:D}/subscriptions/{Subscription:D}/resourceGroups/prod"
            + "/providers/CyberCloud.DBforPostgreSQL/servers/pg-main/databases"
        );
    }

    /// <summary>
    ///     ⚠ <b>Every generated resource id's collection round-trips, and its member round-trips
    ///     back to the id.</b>
    /// </summary>
    /// <remarks>
    ///     The two directions together are what make <see cref="ResourceCollectionId.Of" /> and
    ///     <see cref="ResourceCollectionId.Member" /> inverses. One direction alone would pass for a
    ///     renderer that dropped the ancestors, because parsing its own output would drop them too.
    /// </remarks>
    [Fact]
    public void EveryCollectionRoundTripsAndItsMemberRebuildsTheResource() {
        foreach (var id in Corpus.ResourceIds(500, seed: 20260902)) {
            var collection = ResourceCollectionId.Of(id);

            var parsed = ResourceCollectionId.ParsePath(collection.Path);
            parsed.IsSuccess.ShouldBeTrue($"'{collection.Path}' did not re-parse: {parsed.Error?.Message}");
            parsed.GetValueOrThrow().ShouldBe(collection);

            parsed.GetValueOrThrow().Member(id.Name).ShouldBe(id with { Id = Guid.Empty });
        }
    }

    // ── The partition ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    ///     ⚠ <b>No resource path parses as a collection, and no collection path parses as a
    ///     resource.</b>
    /// </summary>
    /// <remarks>
    ///     This is the property the gateway relies on to decide from the path alone. It is asserted
    ///     over the generated corpus rather than on a handful of examples because the failure it
    ///     guards against is a depth the examples happen not to reach: a parser that counted
    ///     segments rather than parity would agree with this at depth 1 and disagree at depth 2.
    /// </remarks>
    [Fact]
    public void TheTwoGrammarsAreDisjointForEveryGeneratedAddress() {
        foreach (var id in Corpus.ResourceIds(500, seed: 20260903)) {
            ResourceCollectionId.TryParsePath(id.Path, out _)
                .ShouldBeFalse($"the resource path '{id.Path}' parsed as a collection");

            var collection = ResourceCollectionId.Of(id);

            ResourceId.TryParsePath(collection.Path, out _)
                .ShouldBeFalse($"the collection path '{collection.Path}' parsed as a resource");
        }
    }

    [Fact]
    public void AResourcePathIsRefusedWithAMessageThatSaysWhy() {
        var id = new ResourceId(Tenant, Subscription, "prod", new("CyberCloud.Testing", "widgets"), "main", Guid.Empty);

        var refused = ResourceCollectionId.ParsePath(id.Path);

        refused.IsFailure.ShouldBeTrue();
        refused.Error!.Code.ShouldBe(ErrorCode.InvalidResourceId);
        refused.Error.Message.ShouldContain("ends on a name");
    }

    // ── What it refuses ────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("tenants/x")]
    [InlineData("/tenants")]
    public void ANonPathIsRefused(string? path) =>
        ResourceCollectionId.TryParsePath(path, out _).ShouldBeFalse();

    [Fact]
    public void AnEmptySegmentIsRefusedRatherThanAbsorbed() {
        var collection = new ResourceCollectionId(Tenant, Subscription, "prod", new("CyberCloud.Testing", "widgets"));

        ResourceCollectionId.TryParsePath(collection.Path + "/", out _).ShouldBeFalse();
        ResourceCollectionId.TryParsePath(collection.Path.Replace("/prod/", "//prod/", StringComparison.Ordinal), out _)
            .ShouldBeFalse();
    }

    /// <summary>
    ///     ⚠ <b>A GUID in any spelling but <c>D</c> is refused, so one collection has one path.</b>
    /// </summary>
    /// <remarks>
    ///     The same rule <see cref="ResourceId" /> applies, and for the same reason: five spellings
    ///     of one address are five cache entries and five audit rows. A collection has no index entry
    ///     of its own, so the cost lands instead on every log line and every rate-limit bucket keyed
    ///     on the path.
    /// </remarks>
    [Theory]
    [InlineData("{2b4a1c66-2e70-4a9d-9d0a-1f7ec1f1a4b3}")]
    [InlineData("2b4a1c662e704a9d9d0a1f7ec1f1a4b3")]
    [InlineData(" 2b4a1c66-2e70-4a9d-9d0a-1f7ec1f1a4b3 ")]
    public void AGuidInAnyOtherSpellingIsRefused(string tenant) =>
        ResourceCollectionId.TryParsePath(
            $"/tenants/{tenant}/subscriptions/{Subscription:D}/resourceGroups/prod"
            + "/providers/CyberCloud.Testing/widgets",
            out _
        )
        .ShouldBeFalse();

    /// <summary>
    ///     ⚠ <b>Every ancestor name is validated, not just the structural segments.</b>
    /// </summary>
    /// <remarks>
    ///     They are segments of the same path, and an unvalidated one is the same separator-injection
    ///     hole a resource's own name is checked for. The grammar interleaves, so a name carrying a
    ///     <c>/</c> shifts the whole alternation and the path re-parses as something else.
    /// </remarks>
    [Fact]
    public void AnAncestorNameThatIsNotALegalNameIsRefused() {
        foreach (var (value, why) in Corpus.InjectionCharacters) {
            var path = $"/tenants/{Tenant:D}/subscriptions/{Subscription:D}/resourceGroups/prod"
                + $"/providers/CyberCloud.Testing/widgets/pg{value}main/gadgets";

            ResourceCollectionId.TryParsePath(path, out _).ShouldBeFalse($"'{value}' — {why} — was accepted");
        }
    }

    /// <summary>
    ///     ⚠ <b>The structural literals fold case and the values do not.</b>
    /// </summary>
    /// <remarks>
    ///     A support engineer pasting <c>/ResourceGroups/</c> out of a document should not get a parse
    ///     error; a group actually named <c>PROD</c> is not a legal name and folding it would be the
    ///     mangling docs/plan/06 § Identifiers forbids.
    /// </remarks>
    [Fact]
    public void TheStructuralLiteralsFoldCaseAndTheValuesDoNot() {
        ResourceCollectionId.TryParsePath(
            $"/Tenants/{Tenant:D}/Subscriptions/{Subscription:D}/ResourceGroups/prod"
            + "/Providers/CyberCloud.Testing/widgets",
            out var folded
        )
        .ShouldBeTrue();

        folded.ResourceGroup.ShouldBe("prod");

        ResourceCollectionId.TryParsePath(
            $"/tenants/{Tenant:D}/subscriptions/{Subscription:D}/resourceGroups/PROD"
            + "/providers/CyberCloud.Testing/widgets",
            out _
        )
        .ShouldBeFalse();
    }

    // ── Construction ───────────────────────────────────────────────────────────────────────────

    /// <summary>
    ///     ⚠ <b>A nested type with no ancestor name is a construction-time throw, not a bad path.</b>
    /// </summary>
    /// <remarks>
    ///     The quiet version renders a path with a segment missing that re-parses as something else —
    ///     which is why <see cref="ResourceId" /> throws on the same mismatch and why this one does
    ///     too. <see cref="ResourceCollectionId.ParsePath" /> cannot produce the mismatch at all: it
    ///     derives both counts from the same alternation.
    /// </remarks>
    [Fact]
    public void ANestedTypeWithoutItsAncestorsNamesThrows() {
        Should.Throw<ArgumentException>(
            () => new ResourceCollectionId(
                Tenant,
                Subscription,
                "prod",
                new("CyberCloud.Testing", "widgets/gadgets")
            )
        );

        Should.Throw<ArgumentException>(
            () => new ResourceCollectionId(
                Tenant,
                Subscription,
                "prod",
                new("CyberCloud.Testing", "widgets"),
                "one-too-many"
            )
        );
    }
}

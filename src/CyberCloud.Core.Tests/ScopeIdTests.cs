using CyberCloud.Core.Resources;
using Shouldly;
using System.Globalization;

namespace CyberCloud.Core.Tests;

/// <summary>
///     <see cref="ScopeId" /> — docs/plan/06 § The hierarchy, addressed the way § Identifiers
///     addresses a resource.
/// </summary>
/// <remarks>
///     ⚠ <b>The disjointness sweep is the reason this file is not three round-trip assertions.</b>
///     <c>GatewayRouter.Resolve</c> now tries this grammar and <see cref="ResourceId.ParsePath" />
///     against the same string, and it tries this one first. If any input parsed as both, the order
///     would be a precedence rule nobody wrote down, and the loser's shape would be silently
///     unreachable — a resource path swallowed by the scope grammar would answer <c>404</c> where it
///     used to answer <c>400</c>, which sends a client looking for a missing resource instead of at
///     their URL.
/// </remarks>
public class ScopeIdTests {
    static readonly Guid Tenant = Guid.Parse("2b4a1c66-2e70-4a9d-9d0a-1f7ec1f1a4b3");
    static readonly Guid Subscription = Guid.Parse("7f2d4e88-1a3b-4c5d-8e9f-0a1b2c3d4e5f");

    // ── Round trips ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ATenantRoundTrips() {
        var scope = ScopeId.Tenant(Tenant);

        scope.Path.ShouldBe($"/tenants/{Tenant:D}");
        ScopeId.ParsePath(scope.Path).GetValueOrThrow().ShouldBe(scope);
    }

    [Fact]
    public void ASubscriptionRoundTrips() {
        var scope = ScopeId.Subscription(Tenant, Subscription);

        scope.Path.ShouldBe($"/tenants/{Tenant:D}/subscriptions/{Subscription:D}");
        ScopeId.ParsePath(scope.Path).GetValueOrThrow().ShouldBe(scope);
    }

    [Fact]
    public void AResourceGroupRoundTrips() {
        var scope = ScopeId.Group(Tenant, Subscription, "prod");

        scope.Path.ShouldBe($"/tenants/{Tenant:D}/subscriptions/{Subscription:D}/resourceGroups/prod");
        ScopeId.ParsePath(scope.Path).GetValueOrThrow().ShouldBe(scope);
    }

    // ── The parent chain the ReBAC rewrites follow ─────────────────────────────────────────────

    [Fact]
    public void TheParentChainIsGroupThenSubscriptionThenNothing() {
        var group = ScopeId.Group(Tenant, Subscription, "prod");

        group.Parent.ShouldBe(ScopeId.Subscription(Tenant, Subscription));
        group.Parent!.Value.Parent.ShouldBe(ScopeId.Tenant(Tenant));

        // ⚠ A TENANT HAS NO PARENT, AND THAT NULL IS THE WHOLE AUTHORIZATION PROBLEM RATHER THAN AN
        // EDGE CASE. CyberCloudSchema declares no `parent` relation on `tenant`, so no rewrite can
        // produce a grant on one and only a direct tuple can — which is why
        // IScopeManager.CreateTenantAsync requires an owner in its request and refuses without one.
        ScopeId.Tenant(Tenant).Parent.ShouldBeNull();
    }

    // ── Disjointness from the resource grammar ─────────────────────────────────────────────────

    [Fact]
    public void NoScopePathIsAlsoAResourcePath() {
        foreach (var path in EveryShape()) {
            ResourceId.TryParsePath(path, out _).ShouldBeFalse(
                $"'{path}' parses as BOTH a scope and a resource id. GatewayRouter tries the scope "
                + "grammar first, so the resource reading would be unreachable — and which one wins "
                + "would be a precedence rule nobody wrote down."
            );
        }
    }

    [Theory]
    // A top-level resource: eight fixed segments plus one {type}/{name} pair.
    [InlineData("/tenants/{t}/subscriptions/{s}/resourceGroups/prod/providers/CyberCloud.Cache/redis/main")]
    // A child: two pairs.
    [InlineData("/tenants/{t}/subscriptions/{s}/resourceGroups/prod/providers/CyberCloud.DBforPostgreSQL/servers/pg/databases/orders")]
    public void NoResourcePathIsAlsoAScopePath(string template) {
        var path = Fill(template);

        ResourceId.TryParsePath(path, out _).ShouldBeTrue("the fixture is not a resource path");

        ScopeId.TryParsePath(path, out _).ShouldBeFalse(
            $"'{path}' parses as a scope, so the scope grammar would swallow a resource address and "
            + "the router would never reach ResolveResource for it."
        );
    }

    // ── Refusals ───────────────────────────────────────────────────────────────────────────────

    [Theory]
    // Odd segment counts sit between the three legal shapes and are neither.
    [InlineData("/tenants")]
    [InlineData("/tenants/{t}/subscriptions")]
    [InlineData("/tenants/{t}/subscriptions/{s}/resourceGroups")]
    [InlineData("/tenants/{t}/subscriptions/{s}/resourceGroups/prod/extra")]
    // Wrong literals.
    [InlineData("/tenant/{t}")]
    [InlineData("/tenants/{t}/subscription/{s}")]
    // Empty segments: a doubled slash, a trailing slash.
    [InlineData("/tenants//{t}")]
    [InlineData("/tenants/{t}/")]
    // No leading slash.
    [InlineData("tenants/{t}")]
    public void AMalformedScopePathIsRefused(string template) =>
        ScopeId.TryParsePath(Fill(template), out _).ShouldBeFalse();

    [Theory]
    // ⚠ Every GUID form Guid.TryParse accepts and this grammar must not. Five spellings of one
    // address are five cache entries and five audit rows — the same rule ResourceId applies, and
    // Guid.TryParseExact is not enough on its own because it trims surrounding whitespace.
    [InlineData("N")]
    [InlineData("B")]
    [InlineData("P")]
    public void AGuidThatIsNotTheDFormIsRefused(string format) {
        var path = "/tenants/" + Tenant.ToString(format, CultureInfo.InvariantCulture);

        ScopeId.TryParsePath(path, out _).ShouldBeFalse($"the '{format}' GUID form was accepted");
    }

    [Fact]
    public void SurroundingWhitespaceOnAGuidIsRefused() =>
        ScopeId.TryParsePath($"/tenants/ {Tenant:D} ", out _).ShouldBeFalse();

    [Theory]
    // Upper case is not DNS-1123, and folding it would be the mangling docs/plan/06 § Identifiers
    // forbids — the same rule ResourceId applies to the same segment.
    [InlineData("PROD")]
    [InlineData("-leading")]
    [InlineData("trailing-")]
    [InlineData("under_score")]
    public void AnIllegalResourceGroupNameIsRefused(string name) =>
        ScopeId.TryParsePath(
            $"/tenants/{Tenant:D}/subscriptions/{Subscription:D}/resourceGroups/{name}",
            out _
        ).ShouldBeFalse();

    [Fact]
    public void TheStructuralLiteralsAreMatchedCaseInsensitivelyAndTheValuesAreNot() {
        // A support engineer pasting '/ResourceGroups/' out of a document should not get a parse
        // error; '/resourceGroups/PROD' must still fail, because PROD is not a legal name.
        ScopeId.TryParsePath(
            $"/Tenants/{Tenant:D}/Subscriptions/{Subscription:D}/ResourceGroups/prod",
            out var scope
        ).ShouldBeTrue();

        // ⚠ And the rendered path is the canonical spelling, so a round trip normalises.
        scope.Path.ShouldBe($"/tenants/{Tenant:D}/subscriptions/{Subscription:D}/resourceGroups/prod");
    }

    [Fact]
    public void ThePlatformTenantIsAnOrdinaryAddressAndNotAnAbsentOne() {
        // ⚠ docs/plan/06 § Platform administration: Guid.Empty is "an ordinary tenant id that
        // happens to be all zeroes". A parser that treated it as "no tenant" would make the platform
        // tenant unaddressable, which is the tenant every admin action is for.
        var scope = ScopeId.Tenant(Guid.Empty);

        ScopeId.ParsePath(scope.Path).GetValueOrThrow().ShouldBe(scope);
        scope.Kind.ShouldBe(ScopeKind.Tenant);
    }

    [Fact]
    public void AnUnknownScopeRendersNoPath() {
        // A default-constructed ScopeId names nothing, and Path must say so rather than rendering
        // '/tenants/00000000-…' — which is the PLATFORM tenant and a real address.
        default(ScopeId).Path.ShouldBeEmpty();
    }

    static IEnumerable<string> EveryShape() {
        yield return ScopeId.Tenant(Tenant).Path;
        yield return ScopeId.Subscription(Tenant, Subscription).Path;

        foreach (var name in new[] { "prod", "a", new string('z', 63) }) {
            yield return ScopeId.Group(Tenant, Subscription, name).Path;
        }
    }

    static string Fill(string template) =>
        template
            .Replace("{t}", Tenant.ToString("D", CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{s}", Subscription.ToString("D", CultureInfo.InvariantCulture), StringComparison.Ordinal);
}

using CyberCloud.Core.Resources;
using Shouldly;
using System.Globalization;

namespace CyberCloud.Core.Tests;

/// <summary>
///     <see cref="ScopeCollectionId" /> — the third scope grammar, and its disjointness from the
///     other two and from the resource grammar.
/// </summary>
/// <remarks>
///     ⚠ <b>The sweep is the point, for the reason <see cref="ScopeIdTests" /> gives.</b>
///     <c>GatewayRouter.Resolve</c> tries the scope item grammar, then this one, then the resource
///     grammars against the same string. If any input parsed as two of them the order would be a
///     precedence rule nobody wrote down, and the loser's shape would be silently unreachable —
///     a collection swallowed by the item grammar would answer <c>404</c> from a read of a scope
///     nobody named.
/// </remarks>
public class ScopeCollectionIdTests {
    static readonly Guid Tenant = Guid.Parse("2b4a1c66-2e70-4a9d-9d0a-1f7ec1f1a4b3");
    static readonly Guid Subscription = Guid.Parse("7f2d4e88-1a3b-4c5d-8e9f-0a1b2c3d4e5f");

    // ── Round trips ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheSubscriptionCollectionRoundTrips() {
        var collection = ScopeCollectionId.SubscriptionsOf(Tenant);

        collection.Path.ShouldBe($"/tenants/{Tenant:D}/subscriptions");
        collection.MemberKind.ShouldBe(ScopeKind.Subscription);
        collection.Parent.ShouldBe(ScopeId.Tenant(Tenant));
        ScopeCollectionId.ParsePath(collection.Path).GetValueOrThrow().ShouldBe(collection);
    }

    [Fact]
    public void TheResourceGroupCollectionRoundTrips() {
        var collection = ScopeCollectionId.ResourceGroupsOf(Tenant, Subscription);

        collection.Path.ShouldBe($"/tenants/{Tenant:D}/subscriptions/{Subscription:D}/resourceGroups");
        collection.MemberKind.ShouldBe(ScopeKind.ResourceGroup);
        collection.Parent.ShouldBe(ScopeId.Subscription(Tenant, Subscription));
        ScopeCollectionId.ParsePath(collection.Path).GetValueOrThrow().ShouldBe(collection);
    }

    [Fact]
    public void TheParentShortcutOnScopeIdAgrees() {
        ScopeId.TryParseCollectionParent($"/tenants/{Tenant:D}/subscriptions", out var parent).ShouldBeTrue();
        parent.ShouldBe(ScopeId.Tenant(Tenant));

        ScopeId.TryParseCollectionParent($"/tenants/{Tenant:D}", out _).ShouldBeFalse("an item is not a collection");
    }

    /// <summary>
    ///     ⚠ A resource group has no scope children, and the constructor says so rather than
    ///     rendering a path nothing serves.
    /// </summary>
    [Fact]
    public void AResourceGroupCannotBeACollectionsParent() =>
        Should.Throw<ArgumentException>(() => new ScopeCollectionId(ScopeId.Group(Tenant, Subscription, "prod")));

    // ── Disjointness from the other grammars ───────────────────────────────────────────────────

    [Fact]
    public void NoCollectionPathIsAlsoAScopeItemOrAResourcePath() {
        foreach (var path in new[] {
                     ScopeCollectionId.SubscriptionsOf(Tenant).Path,
                     ScopeCollectionId.ResourceGroupsOf(Tenant, Subscription).Path
                 }) {
            ScopeId.TryParsePath(path, out _)
                .ShouldBeFalse($"'{path}' parses as BOTH a scope collection and a scope item.");

            ResourceId.TryParsePath(path, out _)
                .ShouldBeFalse($"'{path}' parses as BOTH a scope collection and a resource id.");

            ResourceCollectionId.TryParsePath(path, out _)
                .ShouldBeFalse($"'{path}' parses as BOTH a scope collection and a resource collection.");
        }
    }

    [Theory]
    [InlineData("/tenants/{t}")]
    [InlineData("/tenants/{t}/subscriptions/{s}")]
    [InlineData("/tenants/{t}/subscriptions/{s}/resourceGroups/prod")]
    [InlineData("/tenants/{t}/subscriptions/{s}/resourceGroups/prod/providers/CyberCloud.Cache/redis/main")]
    [InlineData("/tenants/{t}/subscriptions/{s}/resourceGroups/prod/providers/CyberCloud.Cache/redis")]
    public void NoItemOrResourcePathIsAlsoACollectionPath(string template) =>
        ScopeCollectionId.TryParsePath(Fill(template), out _)
            .ShouldBeFalse($"'{Fill(template)}' parses as a scope collection, so the collection grammar would swallow it.");

    // ── Refusals ───────────────────────────────────────────────────────────────────────────────

    [Theory]
    // ⚠ /tenants is not a collection: the only tenant a request can address is its own.
    [InlineData("/tenants")]
    // Wrong literal under a well-formed parent.
    [InlineData("/tenants/{t}/resourceGroups")]
    [InlineData("/tenants/{t}/subscriptions/{s}/subscriptions")]
    [InlineData("/tenants/{t}/subscriptions/{s}/providers")]
    // Wrong literals higher up.
    [InlineData("/tenant/{t}/subscriptions")]
    [InlineData("/tenants/{t}/subscription/{s}/resourceGroups")]
    // Empty segments and no leading slash.
    [InlineData("/tenants/{t}/subscriptions/")]
    [InlineData("/tenants//subscriptions")]
    [InlineData("tenants/{t}/subscriptions")]
    public void AMalformedCollectionPathIsRefused(string template) =>
        ScopeCollectionId.TryParsePath(Fill(template), out _).ShouldBeFalse();

    [Fact]
    public void TheLiteralsAreMatchedCaseInsensitivelyAndTheGuidIsTheDFormOnly() {
        ScopeCollectionId.TryParsePath($"/Tenants/{Tenant:D}/Subscriptions/{Subscription:D}/ResourceGroups", out var loose)
            .ShouldBeTrue();

        loose.ShouldBe(ScopeCollectionId.ResourceGroupsOf(Tenant, Subscription));

        ScopeCollectionId.TryParsePath($"/tenants/{Tenant:N}/subscriptions", out _)
            .ShouldBeFalse("the 'N' GUID form was accepted — one scope has exactly one path");
    }

    static string Fill(string template) =>
        template
            .Replace("{t}", Tenant.ToString("D", CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{s}", Subscription.ToString("D", CultureInfo.InvariantCulture), StringComparison.Ordinal);
}

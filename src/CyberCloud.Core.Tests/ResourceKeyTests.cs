using System.Globalization;
using CyberCloud.Core.Resources;
using Shouldly;

namespace CyberCloud.Core.Tests;

/// <summary>
///     <see cref="ResourceKey" /> — the only type allowed to format or parse a grain key (ADR-002,
///     docs/plan/02:127-141).
/// </summary>
public class ResourceKeyTests
{
    static readonly ResourceTypeName Postgres = new("CyberCloud.DBforPostgreSQL", "servers");

    static readonly ResourceKey Sample = new(
        Guid.Parse("7f2d4e88-1a3b-4c5d-8e9f-0a1b2c3d4e5f"),
        "prod",
        Postgres,
        Guid.Parse("0a1b2c3d-4e5f-4071-8293-a4b5c6d7e8f9"));

    // ── The shape ADR-002 (docs/plan/02:138) specifies ─────────────────────────────────────────

    [Fact]
    public void KeyIsExactlyTheShapeTheAdrSpecifies() =>
        Sample.ToKeyWithinTenant().ShouldBe(
            "7f2d4e881a3b4c5d8e9f0a1b2c3d4e5f/prod/CyberCloud.DBforPostgreSQL/servers"
            + "/0a1b2c3d4e5f40718293a4b5c6d7e8f9");

    [Fact]
    public void GuidsUseTheThirtyTwoDigitNForm()
    {
        var key = Sample.ToKeyWithinTenant();

        key.ShouldNotContain("-");
        key.Split('/')[0].Length.ShouldBe(32);
        key.Split('/')[^1].Length.ShouldBe(32);
    }

    // ── Round-trip over a generated corpus ─────────────────────────────────────────────────────

    [Fact]
    public void KeyRoundTripsForEveryGeneratedId()
    {
        var count = 0;
        foreach (var id in Corpus.ResourceIds(3_000, seed: 4242))
        {
            count++;
            var key = ResourceKey.From(id);
            var text = key.ToKeyWithinTenant();

            ResourceKey.TryParse(text, out var parsed).ShouldBeTrue($"'{text}' should parse");
            parsed.ShouldBe(key);
            parsed.ToKeyWithinTenant().ShouldBe(text);
        }

        count.ShouldBe(3_000);
    }

    [Fact]
    public void NestedTypesRoundTrip()
    {
        var key = Sample with
        {
            Type = new ResourceTypeName("CyberCloud.DBforPostgreSQL", "servers/databases")
        };

        key.ToKeyWithinTenant().ShouldContain("/CyberCloud.DBforPostgreSQL/servers/databases/");

        ResourceKey.TryParse(key.ToKeyWithinTenant(), out var parsed).ShouldBeTrue();
        parsed.Type.Type.ShouldBe("servers/databases");
        parsed.ShouldBe(key);
    }

    // ── The Orleans.Multitenant contract ───────────────────────────────────────────────────────
    //
    // See OrleansMultitenantEncodingTests for what was verified and how. The property this type
    // must hold is narrow and checkable: the key it produces must pass through tenant
    // qualification unchanged, so that the physical key stored in Redis and printed in a log reads
    // as the key we constructed.

    [Fact]
    public void EveryGeneratedKeyIsSafeForTenantQualification()
    {
        foreach (var id in Corpus.ResourceIds(3_000, seed: 99))
        {
            var text = ResourceKey.From(id).ToKeyWithinTenant();

            ResourceKey.IsTenantQualificationSafe(text).ShouldBeTrue(
                $"'{text}' must contain no '|' and must not start with '|' or '~'");

            OrleansMultitenantKeyModel
                .Qualify("2b4a1c662e704a9d9d0a1f7ec1f1a4b3", text)
                .ShouldBe("2b4a1c662e704a9d9d0a1f7ec1f1a4b3|" + text);
        }
    }

    [Theory]
    [InlineData("|leading", false)]
    [InlineData("~leading", false)]
    [InlineData("has|pipe", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("plain/key", true)]
    [InlineData("has~tilde-not-leading", true)]
    public void TenantQualificationSafetyIsWhatItSaysItIs(string? key, bool expected) =>
        ResourceKey.IsTenantQualificationSafe(key).ShouldBe(expected);

    // ── Separator injection ────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheConstructorRefusesAnInjectedResourceGroup()
    {
        foreach (var (value, why) in Corpus.InjectionCharacters)
        {
            Should.Throw<ArgumentException>(
                () => new ResourceKey(Guid.NewGuid(), "pr" + value + "od", Postgres, Guid.NewGuid()),
                $"a resource group containing {Corpus.Printable(value)} ({why}) must not be constructible");
        }
    }

    [Fact]
    public void WithAlsoValidates() =>
        Should.Throw<ArgumentException>(() => Sample with { ResourceGroup = "prod|evil" });

    [Fact]
    public void AnInjectedKeyStringIsRejectedByTheParser()
    {
        foreach (var (value, why) in Corpus.InjectionCharacters)
        {
            var text =
                "7f2d4e881a3b4c5d8e9f0a1b2c3d4e5f/pr" + value + "od"
                + "/CyberCloud.DBforPostgreSQL/servers/0a1b2c3d4e5f40718293a4b5c6d7e8f9";

            Should.NotThrow(() => ResourceKey.TryParse(text, out _));

            if (ResourceKey.TryParse(text, out var parsed))
            {
                // The only tolerable outcome other than rejection is an exact round trip — the
                // parser must never accept one string and produce a key that formats as another.
                parsed.ToKeyWithinTenant().ShouldBe(
                    text,
                    $"'{Corpus.Printable(text)}' ({why}) parsed but did not round-trip");
            }
        }
    }

    [Fact]
    public void APipeInAKeyStringIsRejected()
    {
        // The one that matters most: a '|' here would be copied verbatim into the physical key
        // (verified — see OrleansMultitenantEncodingTests) and would make the key illegible.
        const string text =
            "7f2d4e881a3b4c5d8e9f0a1b2c3d4e5f/pr|od"
            + "/CyberCloud.DBforPostgreSQL/servers/0a1b2c3d4e5f40718293a4b5c6d7e8f9";

        ResourceKey.TryParse(text, out _).ShouldBeFalse();
        ResourceKey.Parse(text).Error!.Code.ShouldBe(ErrorCode.InvalidGrainKey);
    }

    // ── GUID formats ───────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("7f2d4e88-1a3b-4c5d-8e9f-0a1b2c3d4e5f", "hyphenated (D)")]
    [InlineData("{7f2d4e881a3b4c5d8e9f0a1b2c3d4e5f}", "braced")]
    [InlineData("(7f2d4e881a3b4c5d8e9f0a1b2c3d4e5f)", "parenthesised")]
    [InlineData("7f2d4e881a3b4c5d8e9f0a1b2c3d4e5", "one digit short")]
    public void OnlyTheNFormIsAcceptedInAKey(string subscription, string why)
    {
        var text = subscription
            + "/prod/CyberCloud.DBforPostgreSQL/servers/0a1b2c3d4e5f40718293a4b5c6d7e8f9";

        ResourceKey.TryParse(text, out _).ShouldBeFalse($"the {why} form must not parse");
    }

    // ── Empty and malformed input ──────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("/")]
    [InlineData("//")]
    [InlineData("7f2d4e881a3b4c5d8e9f0a1b2c3d4e5f")]
    [InlineData("7f2d4e881a3b4c5d8e9f0a1b2c3d4e5f/prod")]
    [InlineData("7f2d4e881a3b4c5d8e9f0a1b2c3d4e5f/prod/CyberCloud.DBforPostgreSQL")]
    [InlineData("7f2d4e881a3b4c5d8e9f0a1b2c3d4e5f//CyberCloud.DBforPostgreSQL/servers/0a1b2c3d4e5f40718293a4b5c6d7e8f9")]
    [InlineData("res/0a1b2c3d4e5f40718293a4b5c6d7e8f9")]
    public void MalformedInputReturnsFalseAndNeverThrows(string? text)
    {
        Should.NotThrow(() => ResourceKey.TryParse(text, out _));
        ResourceKey.TryParse(text, out var key).ShouldBeFalse();
        key.ShouldBe(default);
    }

    // ── From(ResourceId) ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void FromAResourceIdCarriesTheFourComponents()
    {
        var id = new ResourceId(
            Guid.NewGuid(),
            Sample.SubscriptionId,
            "prod",
            Postgres,
            "pg-main",
            Sample.ResourceId);

        ResourceKey.From(id).ShouldBe(Sample);
    }

    [Fact]
    public void FromAPathParsedIdIsRefusedBecauseItHasNoGuidYet()
    {
        ResourceId.TryParsePath(
            "/tenants/2b4a1c66-2e70-4a9d-9d0a-1f7ec1f1a4b3"
            + "/subscriptions/7f2d4e88-1a3b-4c5d-8e9f-0a1b2c3d4e5f"
            + "/resourceGroups/prod"
            + "/providers/CyberCloud.DBforPostgreSQL/servers/pg-main",
            out var parsed).ShouldBeTrue();

        // Guid.Empty would collide every unresolved resource in the tenant onto one activation.
        var thrown = Should.Throw<ArgumentException>(() => ResourceKey.From(parsed));
        thrown.Message.ShouldContain("IResourceIndexGrain");
    }

    // ── Degenerate construction ────────────────────────────────────────────────────────────────

    [Fact]
    public void AKeyNeedsARealType() =>
        Should.Throw<ArgumentException>(
            () => new ResourceKey(Guid.NewGuid(), "prod", default, Guid.NewGuid()));

    [Fact]
    public void ToStringIsTheKey() => Sample.ToString().ShouldBe(Sample.ToKeyWithinTenant());

    [Fact]
    public void TheKeyIsBoundedInLengthSoRedisKeysAreNotUnbounded()
    {
        // 32 + 1 + 63 + 1 + 128 + 1 + (3 * 63 + 2) + 1 + 32, worst case. Recorded because a grain
        // key becomes a Redis key (docs/plan/05) and an unbounded one is a memory problem, not a
        // correctness one.
        var longest = new ResourceKey(
            Guid.NewGuid(),
            new string('a', 63),
            new ResourceTypeName(
                new string('a', 63) + "." + new string('b', 63),
                new string('c', 63) + "/" + new string('d', 63) + "/" + new string('e', 63)),
            Guid.NewGuid());

        longest.ToKeyWithinTenant().Length.ShouldBeLessThan(512);
        ResourceKey.TryParse(longest.ToKeyWithinTenant(), out var parsed).ShouldBeTrue();
        parsed.ShouldBe(longest);
    }

    [Fact]
    public void TheKeyUsesInvariantFormattingRegardlessOfCulture()
    {
        // InvariantGlobalization=true makes this belt-and-braces, but a grain key that changes with
        // the ambient culture is the kind of bug that only appears on one machine.
        var key = Sample.ToKeyWithinTenant();

        key.ShouldBe(
            Sample.SubscriptionId.ToString("N", CultureInfo.InvariantCulture)
            + "/prod/CyberCloud.DBforPostgreSQL/servers/"
            + Sample.ResourceId.ToString("N", CultureInfo.InvariantCulture));
    }
}

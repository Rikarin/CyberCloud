using System.Globalization;
using CyberCloud.Core.Resources;
using Shouldly;

namespace CyberCloud.Core.Tests;

/// <summary>
///     <see cref="GrainKeys" /> — the only type allowed to format or parse a grain key (ADR-002,
///     docs/plan/02:161-188; the shape table, docs/plan/06:101-110).
/// </summary>
public class GrainKeysTests
{
    static readonly Guid Subscription = Guid.Parse("7f2d4e88-1a3b-4c5d-8e9f-0a1b2c3d4e5f");
    static readonly Guid Tenant = Guid.Parse("2b4a1c66-2e70-4a9d-9d0a-1f7ec1f1a4b3");
    static readonly Guid Resource = Guid.Parse("0a1b2c3d-4e5f-4071-8293-a4b5c6d7e8f9");

    static readonly ResourceId Sample = new(
        Tenant,
        Subscription,
        "prod",
        new ResourceTypeName("CyberCloud.DBforPostgreSQL", "servers"),
        "orders-db",
        Resource);

    // ── The eight shapes docs/plan/06:101-110 specifies ────────────────────────────────────────

    [Fact]
    public void EveryShapeIsExactlyWhatTheTableSays()
    {
        GrainKeys.Subscription(Subscription)
            .ShouldBe("sub/7f2d4e881a3b4c5d8e9f0a1b2c3d4e5f");

        GrainKeys.ResourceGroup(Subscription, "prod")
            .ShouldBe("sub/7f2d4e881a3b4c5d8e9f0a1b2c3d4e5f/rg/prod");

        GrainKeys.Resource(Resource)
            .ShouldBe("res/0a1b2c3d4e5f40718293a4b5c6d7e8f9");

        GrainKeys.User(Resource)
            .ShouldBe("user/0a1b2c3d4e5f40718293a4b5c6d7e8f9");

        GrainKeys.Operation(Resource)
            .ShouldBe("op/0a1b2c3d4e5f40718293a4b5c6d7e8f9");

        GrainKeys.ClusterConnection(Resource)
            .ShouldBe("cluster/0a1b2c3d4e5f40718293a4b5c6d7e8f9");

        GrainKeys.PathIndex(Sample).ShouldStartWith("idx/path/");
        GrainKeys.EmailIndex(Tenant, "alice@example.com").ShouldStartWith("idx/email/");
    }

    [Fact]
    public void TheResourceKeyIsTheGuidAloneSoARenameAndAMoveAreMetadata()
    {
        // docs/plan/06:112-114 and the correction at docs/plan/02:153-159. This is the whole reason
        // ResourceKey was deleted rather than ported: a key carrying the name would make a rename a
        // grain migration, and one carrying the resource group would make a move one too.
        var renamed = Sample with { Name = "billing-db" };
        var moved = Sample with { ResourceGroup = "staging" };
        var retyped = Sample with { Type = new ResourceTypeName("CyberCloud.Compute", "virtualMachines") };

        GrainKeys.Resource(renamed.Id).ShouldBe(GrainKeys.Resource(Sample.Id));
        GrainKeys.Resource(moved.Id).ShouldBe(GrainKeys.Resource(Sample.Id));
        GrainKeys.Resource(retyped.Id).ShouldBe(GrainKeys.Resource(Sample.Id));

        // …and the index key, which is the thing that DOES move, moves.
        GrainKeys.PathIndex(renamed).ShouldNotBe(GrainKeys.PathIndex(Sample));
        GrainKeys.PathIndex(moved).ShouldNotBe(GrainKeys.PathIndex(Sample));
    }

    [Fact]
    public void GuidsUseTheThirtyTwoDigitLowerCaseNForm()
    {
        string[] guidBearing =
        [
            GrainKeys.Subscription(Subscription),
            GrainKeys.Resource(Resource),
            GrainKeys.User(Resource),
            GrainKeys.Operation(Resource),
            GrainKeys.ClusterConnection(Resource)
        ];

        foreach (var key in guidBearing)
        {
            var guid = key.Split('/')[^1];

            guid.Length.ShouldBe(32, $"'{key}' does not end in the 32-digit N form");
            guid.ShouldNotContain("-");
            guid.ShouldBe(guid.ToLowerInvariant(), $"'{key}' spells a GUID in upper case");
        }
    }

    [Fact]
    public void TheDigestIsSixteenLowerCaseHexCharacters()
    {
        string[] indexKeys =
        [
            GrainKeys.PathIndex(Sample),
            GrainKeys.EmailIndex(Tenant, "alice@example.com")
        ];

        foreach (var key in indexKeys)
        {
            var digest = key.Split('/')[^1];

            digest.Length.ShouldBe(GrainKeys.DigestLength);

            foreach (var c in digest)
            {
                (c is >= '0' and <= '9' or >= 'a' and <= 'f').ShouldBeTrue(
                    $"'{digest}' contains '{c}', which is not lower-case hexadecimal");
            }
        }
    }

    // ── Round trip, over a generated corpus rather than three hand-picked cases ────────────────

    [Fact]
    public void EveryShapeRoundTripsForEveryGeneratedId()
    {
        var count = 0;
        foreach (var id in Corpus.ResourceIds(3_000, seed: 4242))
        {
            foreach (var key in Corpus.EveryGrainKeyShapeFor(id))
            {
                count++;

                GrainKeys.TryParse(key, out var parsed).ShouldBeTrue($"'{key}' should parse");
                parsed.ToString().ShouldBe(key);
            }
        }

        count.ShouldBe(3_000 * 8);
    }

    [Fact]
    public void EveryShapeParsesBackToTheKindThatBuiltIt()
    {
        foreach (var id in Corpus.ResourceIds(500, seed: 11))
        {
            var expected = new[]
            {
                GrainKeyKind.Subscription, GrainKeyKind.ResourceGroup, GrainKeyKind.Resource,
                GrainKeyKind.PathIndex, GrainKeyKind.User, GrainKeyKind.EmailIndex,
                GrainKeyKind.Operation, GrainKeyKind.ClusterConnection
            };

            var actual = Corpus.EveryGrainKeyShapeFor(id)
                .Select(k => GrainKeys.Parse(k).GetValueOrThrow().Kind)
                .ToArray();

            actual.ShouldBe(expected);
        }
    }

    [Fact]
    public void ThePayloadSurvivesTheRoundTripAndNotJustTheString()
    {
        foreach (var id in Corpus.ResourceIds(500, seed: 12))
        {
            GrainKeys.Parse(GrainKeys.Subscription(id.SubscriptionId)).GetValueOrThrow()
                .Id.ShouldBe(id.SubscriptionId);

            var group = GrainKeys.Parse(GrainKeys.ResourceGroup(id.SubscriptionId, id.ResourceGroup))
                .GetValueOrThrow();
            group.Id.ShouldBe(id.SubscriptionId);
            group.Name.ShouldBe(id.ResourceGroup);

            GrainKeys.Parse(GrainKeys.Resource(id.Id)).GetValueOrThrow().Id.ShouldBe(id.Id);
            GrainKeys.Parse(GrainKeys.User(id.Id)).GetValueOrThrow().Id.ShouldBe(id.Id);
            GrainKeys.Parse(GrainKeys.Operation(id.Id)).GetValueOrThrow().Id.ShouldBe(id.Id);
            GrainKeys.Parse(GrainKeys.ClusterConnection(id.Id)).GetValueOrThrow().Id.ShouldBe(id.Id);

            var index = GrainKeys.Parse(GrainKeys.PathIndex(id)).GetValueOrThrow();
            index.Digest.Length.ShouldBe(GrainKeys.DigestLength);
            index.Id.ShouldBe(Guid.Empty);
            index.Name.ShouldBeEmpty();
        }
    }

    // ── Key-shape collision: the highest-severity property in this file ───────────────────────

    [Fact]
    public void NoInputToOneFactoryCanProduceAStringAnotherFactoryCouldAlsoProduce()
    {
        // ⚠ THE ONE THAT MATTERS. If two shapes could ever produce one string, two unrelated
        // entities would share an activation and its state — a subscription reading a user's grain,
        // or an email claim satisfied by a path claim. The proof is not "these prefixes look
        // different": it is that over a large, adversarially-shaped corpus the map from key string
        // to (kind, payload) is injective in both directions.
        var seen = new Dictionary<string, (GrainKeyKind Kind, string Origin)>(StringComparer.Ordinal);

        // Ids sharing GUIDs across roles on purpose: the same GUID as subscription, resource, user,
        // operation and cluster at once is the input most likely to make two shapes coincide.
        foreach (var id in Corpus.ResourceIds(2_000, seed: 5150))
        {
            var shared = id with { SubscriptionId = id.Id };

            foreach (var candidate in Corpus.EveryGrainKeyShapeFor(id)
                         .Concat(Corpus.EveryGrainKeyShapeFor(shared)))
            {
                var kind = GrainKeys.Parse(candidate).GetValueOrThrow().Kind;
                var origin = $"{kind} from {id.Path}";

                if (seen.TryGetValue(candidate, out var previous))
                {
                    previous.Kind.ShouldBe(
                        kind,
                        $"'{candidate}' was produced as {previous.Kind} by {previous.Origin} and "
                        + $"as {kind} by {origin} — two shapes collided");
                }
                else
                {
                    seen[candidate] = (kind, origin);
                }
            }
        }

        // Sanity: the corpus really did exercise all eight shapes.
        seen.Values.Select(x => x.Kind).Distinct().Count().ShouldBe(8);
    }

    [Fact]
    public void ASubscriptionKeyIsAStrictPrefixOfItsResourceGroupKeysAndStillCannotBeConfusedWithOne()
    {
        // The genuine near-miss in the table: sub/{id} is a prefix of sub/{id}/rg/{name}. Prefixing
        // is harmless — Orleans keys on the whole string — but only if the parser cuts on segment
        // count rather than on a prefix match, which is what this asserts.
        var subscription = GrainKeys.Subscription(Subscription);
        var group = GrainKeys.ResourceGroup(Subscription, "prod");

        group.ShouldStartWith(subscription + "/");
        group.ShouldNotBe(subscription);

        GrainKeys.Parse(subscription).GetValueOrThrow().Kind.ShouldBe(GrainKeyKind.Subscription);
        GrainKeys.Parse(group).GetValueOrThrow().Kind.ShouldBe(GrainKeyKind.ResourceGroup);
    }

    [Fact]
    public void AResourceGroupNamedRgIsUnambiguous()
    {
        // 'rg' is the literal that marks the segment; a group actually called "rg" is legal under
        // ResourceNaming, so the parser must not be counting occurrences of it.
        var key = GrainKeys.ResourceGroup(Subscription, "rg");

        key.ShouldBe("sub/7f2d4e881a3b4c5d8e9f0a1b2c3d4e5f/rg/rg");

        var parsed = GrainKeys.Parse(key).GetValueOrThrow();
        parsed.Kind.ShouldBe(GrainKeyKind.ResourceGroup);
        parsed.Name.ShouldBe("rg");
        parsed.Id.ShouldBe(Subscription);
    }

    [Theory]
    // A resource group name that is itself the N form of a GUID, or an index digest, or another
    // shape's prefix word. None of these may shift the parse.
    [InlineData("0a1b2c3d4e5f40718293a4b5c6d7e8f9")]
    [InlineData("0a1b2c3d4e5f4071")]
    [InlineData("sub")]
    [InlineData("res")]
    [InlineData("user")]
    [InlineData("op")]
    [InlineData("cluster")]
    [InlineData("idx")]
    [InlineData("path")]
    [InlineData("email")]
    public void AResourceGroupNamedAfterAnotherShapeIsStillAResourceGroup(string name)
    {
        var key = GrainKeys.ResourceGroup(Subscription, name);
        var parsed = GrainKeys.Parse(key).GetValueOrThrow();

        parsed.Kind.ShouldBe(GrainKeyKind.ResourceGroup);
        parsed.Name.ShouldBe(name);
        parsed.ToString().ShouldBe(key);
    }

    [Fact]
    public void TheFactoryRefusesAnInjectedResourceGroupName()
    {
        // Defence one: you cannot build the string in the first place.
        foreach (var (value, why) in Corpus.InjectionCharacters)
        {
            Should.Throw<ArgumentException>(
                () => GrainKeys.ResourceGroup(Subscription, "pr" + value + "od"),
                $"a resource group containing {Corpus.Printable(value)} ({why}) must not be "
                + "constructible");
        }
    }

    [Fact]
    public void TheParserIsSafeEvenWhenNameValidationIsBypassed()
    {
        // Defence two, and the one that matters if a future caller composes a key by hand or a
        // physical key arrives from storage written by an older build. Nothing here may parse into
        // a DIFFERENT shape, and nothing may throw.
        foreach (var (value, why) in Corpus.InjectionCharacters)
        {
            var forged = "sub/7f2d4e881a3b4c5d8e9f0a1b2c3d4e5f/rg/pr" + value + "od";

            Should.NotThrow(() => GrainKeys.TryParse(forged, out _));

            if (GrainKeys.TryParse(forged, out var parsed))
            {
                // The only tolerable outcome other than rejection is an exact round trip into the
                // same shape — never a re-cut into another one.
                parsed.Kind.ShouldBe(
                    GrainKeyKind.ResourceGroup,
                    $"'{Corpus.Printable(forged)}' ({why}) re-cut into a different shape");
                parsed.ToString().ShouldBe(forged);
            }
        }
    }

    [Theory]
    // Hand-forged strings that a naive parser would mis-cut.
    [InlineData("sub/7f2d4e881a3b4c5d8e9f0a1b2c3d4e5f/rg/pr/od", "a '/' in the name adds a segment")]
    [InlineData("sub/7f2d4e881a3b4c5d8e9f0a1b2c3d4e5f/rg/prod/extra", "trailing junk")]
    [InlineData("sub/7f2d4e881a3b4c5d8e9f0a1b2c3d4e5f/rg", "the marker with no name")]
    [InlineData("sub/7f2d4e881a3b4c5d8e9f0a1b2c3d4e5f/xx/prod", "the wrong marker")]
    [InlineData("res/7f2d4e881a3b4c5d8e9f0a1b2c3d4e5f/rg/prod", "a resource key with a group glued on")]
    [InlineData("idx/path/0a1b2c3d4e5f4071/extra", "an index with a fourth segment")]
    [InlineData("idx/other/0a1b2c3d4e5f4071", "an index that is neither path nor email")]
    [InlineData("sub/7f2d4e881a3b4c5d8e9f0a1b2c3d4e5f|forged", "a pipe in the id segment")]
    [InlineData("|sub/7f2d4e881a3b4c5d8e9f0a1b2c3d4e5f", "a leading pipe")]
    [InlineData("~sub/7f2d4e881a3b4c5d8e9f0a1b2c3d4e5f", "a leading tilde")]
    public void AForgedShapeIsRejectedRatherThanReinterpreted(string forged, string why)
    {
        GrainKeys.TryParse(forged, out _).ShouldBeFalse($"'{forged}' — {why}");
        GrainKeys.Parse(forged).Error!.Code.ShouldBe(ErrorCode.InvalidGrainKey);
    }

    [Theory]
    [InlineData("SUB/7f2d4e881a3b4c5d8e9f0a1b2c3d4e5f")]
    [InlineData("Res/0a1b2c3d4e5f40718293a4b5c6d7e8f9")]
    [InlineData("IDX/path/0a1b2c3d4e5f4071")]
    [InlineData("idx/PATH/0a1b2c3d4e5f4071")]
    [InlineData("sub/7f2d4e881a3b4c5d8e9f0a1b2c3d4e5f/RG/prod")]
    public void PrefixesAreMatchedCaseSensitivelySoOneGrainHasOneKey(string key) =>
        // Orleans addresses an activation by the key string. A case-insensitive prefix match would
        // make 'SUB/…' and 'sub/…' two activations of one subscription.
        GrainKeys.TryParse(key, out _).ShouldBeFalse();

    [Theory]
    [InlineData("res/0A1B2C3D4E5F40718293A4B5C6D7E8F9", "upper-case hex")]
    [InlineData("res/0a1b2c3d-4e5f-4071-8293-a4b5c6d7e8f9", "hyphenated (D)")]
    [InlineData("res/{0a1b2c3d4e5f40718293a4b5c6d7e8f9}", "braced")]
    [InlineData("res/(0a1b2c3d4e5f40718293a4b5c6d7e8f9)", "parenthesised")]
    [InlineData("res/0a1b2c3d4e5f40718293a4b5c6d7e8f", "one digit short")]
    [InlineData("idx/path/0A1B2C3D4E5F4071", "an upper-case digest")]
    [InlineData("idx/path/0a1b2c3d4e5f407", "a short digest")]
    [InlineData("idx/path/0a1b2c3d4e5f40711", "a long digest")]
    [InlineData("idx/path/0a1b2c3d4e5f407g", "a non-hex digest")]
    public void OnlyTheCanonicalSpellingIsAcceptedInAKey(string key, string why) =>
        GrainKeys.TryParse(key, out _).ShouldBeFalse($"the {why} form must not parse");

    // ── Tenant-qualification safety ───────────────────────────────────────────────────────────

    [Fact]
    public void EveryGeneratedKeyIsSafeForTenantQualification()
    {
        // ADR-002's corrected encoding table (docs/plan/02:138-143): Orleans.Multitenant copies the
        // key within the tenant VERBATIM and prefixes '~' only when it starts with '|' or '~'. So a
        // '|' is not corrupted — but the physical key in Redis, in a log line and in a trace stops
        // reading as the key we constructed. Every key here is trivially clean, so one that is not
        // is a bug.
        var tenant = Tenant.ToString("N", CultureInfo.InvariantCulture);

        foreach (var id in Corpus.ResourceIds(2_000, seed: 99))
        {
            foreach (var key in Corpus.EveryGrainKeyShapeFor(id))
            {
                GrainKeys.IsTenantQualificationSafe(key).ShouldBeTrue(
                    $"'{key}' must contain no '|' and must not start with '|' or '~'");

                OrleansMultitenantKeyModel.Qualify(tenant, key).ShouldBe(tenant + "|" + key);
                OrleansMultitenantKeyModel.ExtractKey(tenant + "|" + key).ShouldBe(key);
                OrleansMultitenantKeyModel.ExtractTenant(tenant + "|" + key).ShouldBe(tenant);
            }
        }
    }

    [Theory]
    [InlineData("|leading", false)]
    [InlineData("~leading", false)]
    [InlineData("has|pipe", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("sub/abc", true)]
    [InlineData("has~tilde-not-leading", true)]
    public void TenantQualificationSafetyIsWhatItSaysItIs(string? key, bool expected) =>
        GrainKeys.IsTenantQualificationSafe(key).ShouldBe(expected);

    [Fact]
    public void TheClusterKeyIsANullTenantKeyAndCannotBeMistakenForATenantedOne()
    {
        // docs/plan/06:110 and :116-122 — IClusterConnectionGrain is the one null-tenant grain, and
        // ADR-002's table (docs/plan/02:143) records that the null-tenant branch is a DIFFERENT
        // encoding: no tenant prefix, no '~' rule, and the whole key has its '|' doubled.
        foreach (var id in Corpus.ResourceIds(200, seed: 3).Select(x => x.Id))
        {
            var key = GrainKeys.ClusterConnection(id);

            // Qualified with the null tenant, the key passes through as itself…
            OrleansMultitenantKeyModel.Qualify(null, key).ShouldBe(key);

            // …and can never be read back as belonging to a tenant.
            OrleansMultitenantKeyModel.ExtractTenant(key).ShouldBeNull();
            OrleansMultitenantKeyModel.ExtractKey(key).ShouldBe(key);

            // Not because of a rule about cluster keys, but because it holds no '|' at all.
            key.ShouldNotContain("|");
        }
    }

    [Fact]
    public void ATenantedClusterKeyWouldBeADifferentStringWhichIsWhyTheExceptionIsWorthNoting()
    {
        // The failure mode the null-tenant note prevents: if somebody tenant-qualified this grain,
        // the same cluster would have one activation per tenant, each holding its own client and
        // its own watches — which is precisely the "two connections to one cluster" that
        // docs/plan/06:116-122 exists to stop. Recorded as an assertion so the distinction is
        // visible rather than folkloric.
        var key = GrainKeys.ClusterConnection(Resource);

        OrleansMultitenantKeyModel.Qualify(null, key)
            .ShouldNotBe(OrleansMultitenantKeyModel.Qualify(
                Tenant.ToString("N", CultureInfo.InvariantCulture), key));
    }

    // ── The path index hashes CanonicalPath, never Path ───────────────────────────────────────

    [Fact]
    public void TwoCaseDifferingPathsProduceTheSameIndexKey()
    {
        // ⚠ docs/plan/06:73-78 and docs/plan/02:182-185. The provider namespace and type are
        // case-preserving on the wire, so one resource has more than one Path spelling. Hashing
        // Path would let each spelling claim the name and defeat the two-phase create at
        // docs/plan/06:124-140 — the one place a duplicate claim is a correctness bug rather than
        // a cosmetic one.
        var mixed = Sample with { Type = new ResourceTypeName("CyberCloud.Cache", "Redis") };
        var lower = Sample with { Type = new ResourceTypeName("cybercloud.cache", "redis") };

        // They really are different strings — otherwise this test proves nothing.
        string.Equals(mixed.Path, lower.Path, StringComparison.Ordinal).ShouldBeFalse(
            "the two Path spellings must differ, or this test asserts nothing");
        mixed.CanonicalPath.ShouldBe(lower.CanonicalPath);

        GrainKeys.PathIndex(mixed).ShouldBe(GrainKeys.PathIndex(lower));
    }

    [Fact]
    public void EveryCaseSpellingOfEveryGeneratedPathAgreesOnOneIndexKey()
    {
        foreach (var id in Corpus.ResourceIds(1_000, seed: 17))
        {
            var upper = id with
            {
                Type = new ResourceTypeName(
                    id.Type.Namespace.ToUpperInvariant(),
                    id.Type.Type.ToUpperInvariant())
            };

            GrainKeys.PathIndex(upper).ShouldBe(
                GrainKeys.PathIndex(id),
                $"'{upper.Path}' and '{id.Path}' are one resource");
        }
    }

    [Fact]
    public void TheIndexKeyIgnoresTheResourceGuidBecauseTheGuidIsTheAnswerNotTheQuestion()
    {
        // The index maps path -> GUID (docs/plan/06:44). An id parsed from a path carries
        // Guid.Empty until the index resolves it, and it must hash to the same entry as the
        // resolved one or the claim could never be looked up.
        ResourceId.TryParsePath(Sample.Path, out var unresolved).ShouldBeTrue();
        unresolved.Id.ShouldBe(Guid.Empty);

        GrainKeys.PathIndex(unresolved).ShouldBe(GrainKeys.PathIndex(Sample));
        GrainKeys.PathIndex(Sample.WithId(Guid.NewGuid())).ShouldBe(GrainKeys.PathIndex(Sample));
    }

    [Fact]
    public void DistinctPathsProduceDistinctIndexKeys()
    {
        var byKey = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var id in Corpus.ResourceIds(3_000, seed: 23))
        {
            var key = GrainKeys.PathIndex(id);

            if (byKey.TryGetValue(key, out var existing))
            {
                existing.ShouldBe(
                    id.CanonicalPath,
                    $"'{existing}' and '{id.CanonicalPath}' collided on index key '{key}'");
            }
            else
            {
                byKey[key] = id.CanonicalPath;
            }
        }
    }

    [Fact]
    public void ACanonicalPathCannotContainTheDigestSeparator()
    {
        // The digest input is "{prefix}\n{value}". The separator is only unambiguous while the
        // value cannot contain one; ResourceNaming and ResourceTypeName are what guarantee that.
        foreach (var id in Corpus.ResourceIds(500, seed: 29))
        {
            id.CanonicalPath.ShouldNotContain("\n");
        }
    }

    // ── The email index: hash(tenantId + normalized email), per tenant ────────────────────────

    [Fact]
    public void EmailUniquenessIsPerTenant()
    {
        // docs/plan/11:99-101 — "email uniqueness is per tenant; global email uniqueness would be a
        // global index — the thing we do not have and do not want."
        var a = GrainKeys.EmailIndex(Tenant, "alice@example.com");
        var b = GrainKeys.EmailIndex(Subscription, "alice@example.com");

        a.ShouldNotBe(b);
        a.ShouldStartWith("idx/email/");
        b.ShouldStartWith("idx/email/");
    }

    [Theory]
    [InlineData("alice@example.com")]
    [InlineData("ALICE@EXAMPLE.COM")]
    [InlineData("Alice@Example.Com")]
    [InlineData("  alice@example.com  ")]
    [InlineData("\talice@example.com\n")]
    [InlineData(" alice@example.com ")]
    public void CaseAndSurroundingWhitespaceDoNotChangeTheKey(string email) =>
        GrainKeys.EmailIndex(Tenant, email)
            .ShouldBe(GrainKeys.EmailIndex(Tenant, "alice@example.com"));

    [Fact]
    public void TheNormalizedFormIsTheOneToStore()
    {
        // Storing a differently-normalized address on the user than the one that went into the
        // digest is how an account becomes unfindable by its own email, so the rule is public.
        GrainKeys.NormalizeEmail("  Alice@Example.COM ").GetValueOrThrow()
            .ShouldBe("alice@example.com");
    }

    [Fact]
    public void AsciiCaseFoldingIsTheOnlyThingThatMergesTwoSpellings()
    {
        // ⚠ THE UNICODE TRAP, recorded against the BCL rather than against our code.
        // "K" is KELVIN SIGN. ToLowerInvariant folds it onto 'k', so a normalizer built on it
        // would make aK@x and ak@x one key — one account silently claiming another's identity at
        // sign-up, which the two-phase claim cannot tell from a genuine duplicate.
        "K".ToLowerInvariant().ShouldBe(
            "k",
            "if this ever goes false the BCL changed; the ASCII-only rule is still correct");

        // …and the ASCII-only rule does not.
        GrainKeys.EmailIndex(Tenant, "aK@example.com")
            .ShouldNotBe(GrainKeys.EmailIndex(Tenant, "ak@example.com"));

        // The trap in the other direction: U+0130 LATIN CAPITAL LETTER I WITH DOT ABOVE does NOT
        // lower-case to "i" — it produces two code points — so a rule that assumed it did would
        // canonicalise inconsistently. Here it is simply left alone and stays a different address.
        "İ".ToLowerInvariant().ShouldNotBe("i");

        GrainKeys.EmailIndex(Tenant, "İrem@example.com")
            .ShouldNotBe(GrainKeys.EmailIndex(Tenant, "irem@example.com"));

        // Turkish dotless ı is a third distinct letter, not a spelling of 'i'.
        GrainKeys.EmailIndex(Tenant, "ırem@example.com")
            .ShouldNotBe(GrainKeys.EmailIndex(Tenant, "irem@example.com"));
    }

    [Fact]
    public void TwoDifferentEmailsNeverNormalizeToTheSameKey()
    {
        var byKey = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var email in Corpus.DistinctEmails(5_000, seed: 31))
        {
            var normalized = GrainKeys.NormalizeEmail(email).GetValueOrThrow();
            var key = GrainKeys.EmailIndex(Tenant, email);

            if (byKey.TryGetValue(key, out var existing))
            {
                existing.ShouldBe(
                    normalized,
                    $"'{existing}' and '{normalized}' collided on email index key '{key}'");
            }
            else
            {
                byKey[key] = normalized;
            }
        }

        // The look-alike code points from the injection corpus, each spliced into an address that
        // is otherwise identical. None may share a key with the plain form.
        var keys = new HashSet<string>(StringComparer.Ordinal)
        {
            GrainKeys.EmailIndex(Tenant, "alice@example.com")
        };

        foreach (var (value, why) in Corpus.InjectionCharacters)
        {
            var email = "al" + value + "ice@example.com";
            var normalized = GrainKeys.NormalizeEmail(email);

            if (normalized.IsFailure)
            {
                continue;
            }

            keys.Add(GrainKeys.EmailIndex(Tenant, email)).ShouldBeTrue(
                $"'{Corpus.Printable(email)}' ({why}) shares a key with another address");
        }
    }

    [Fact]
    public void TheSameEmailInTheSameTenantIsAlwaysTheSameKey()
    {
        // Determinism across calls: a digest that varied would make every claim unfindable.
        foreach (var email in Corpus.DistinctEmails(500, seed: 37))
        {
            GrainKeys.EmailIndex(Tenant, email).ShouldBe(GrainKeys.EmailIndex(Tenant, email));
        }
    }

    [Theory]
    [InlineData(null, "null")]
    [InlineData("", "empty")]
    [InlineData("   ", "white space only")]
    [InlineData("alice", "no '@'")]
    [InlineData("@example.com", "empty local part")]
    [InlineData("alice@", "empty domain")]
    [InlineData("alice@@example.com", "two '@'")]
    [InlineData("alice@ex@ample.com", "two '@' again")]
    [InlineData("ali ce@example.com", "an interior space")]
    [InlineData("alice\n@example.com", "an interior newline — a log-injection vector too")]
    [InlineData("alice\0@example.com", "an interior NUL")]
    public void AnAddressThatIsNotOneIsRejected(string? email, string why)
    {
        GrainKeys.NormalizeEmail(email).IsFailure.ShouldBeTrue($"'{email}' — {why}");

        if (email is not null)
        {
            Should.Throw<ArgumentException>(() => GrainKeys.EmailIndex(Tenant, email));
        }
    }

    [Fact]
    public void AnOverlongAddressIsRejectedRatherThanHashed()
    {
        var local = new string('a', GrainKeys.MaxEmailLength);

        GrainKeys.NormalizeEmail(local + "@example.com").IsFailure.ShouldBeTrue();
        GrainKeys.NormalizeEmail(new string('a', GrainKeys.MaxEmailLength - 12) + "@example.com")
            .IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void ANormalizedEmailCannotContainTheDigestSeparator()
    {
        // Same argument as for the path: the digest input is "{prefix}\n{tenant:N}\n{email}", and
        // the tenant's N form is fixed at 32 characters, so the cut points are unambiguous only
        // while neither component can contain a '\n'.
        foreach (var email in Corpus.DistinctEmails(200, seed: 41))
        {
            GrainKeys.NormalizeEmail(email).GetValueOrThrow().ShouldNotContain("\n");
        }

        GrainKeys.NormalizeEmail("a\nb@example.com").IsFailure.ShouldBeTrue();
    }

    // ── Empty and malformed input — none of these may throw ───────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("/")]
    [InlineData("//")]
    [InlineData("   ")]
    [InlineData("sub")]
    [InlineData("sub/")]
    [InlineData("/sub/7f2d4e881a3b4c5d8e9f0a1b2c3d4e5f")]
    [InlineData("sub/7f2d4e881a3b4c5d8e9f0a1b2c3d4e5f/")]
    [InlineData("sub//7f2d4e881a3b4c5d8e9f0a1b2c3d4e5f")]
    [InlineData("nope/7f2d4e881a3b4c5d8e9f0a1b2c3d4e5f")]
    [InlineData("idx")]
    [InlineData("idx/path")]
    [InlineData("7f2d4e881a3b4c5d8e9f0a1b2c3d4e5f")]
    [InlineData("7f2d4e881a3b4c5d8e9f0a1b2c3d4e5f/prod/CyberCloud.DBforPostgreSQL/servers/0a1b2c3d4e5f40718293a4b5c6d7e8f9")]
    public void MalformedInputReturnsFalseAndNeverThrows(string? text)
    {
        Should.NotThrow(() => GrainKeys.TryParse(text, out _));
        GrainKeys.TryParse(text, out var key).ShouldBeFalse();
        key.ShouldBe(default);
    }

    [Fact]
    public void TheOldCompositeResourceKeyShapeNoLongerParses() =>
        // The shape ADR-002 used to specify, and the reason this type replaced ResourceKey. If it
        // ever parses again, two designs are live at once.
        GrainKeys.TryParse(
            "7f2d4e881a3b4c5d8e9f0a1b2c3d4e5f/prod/CyberCloud.DBforPostgreSQL/servers"
            + "/0a1b2c3d4e5f40718293a4b5c6d7e8f9",
            out _).ShouldBeFalse();

    [Fact]
    public void DefaultGrainKeyIsNotAKey()
    {
        default(GrainKey).Kind.ShouldBe(GrainKeyKind.None);
        default(GrainKey).ToString().ShouldBeEmpty();
        default(GrainKey).Name.ShouldBeEmpty();
        default(GrainKey).Digest.ShouldBeEmpty();
    }

    // ── Bounds and culture ────────────────────────────────────────────────────────────────────

    [Fact]
    public void EveryKeyIsBoundedInLengthSoRedisKeysAreNotUnbounded()
    {
        // A grain key becomes a Redis key (docs/plan/05); an unbounded one is a memory problem.
        // The longest shape is sub/{32}/rg/{63} = 4 + 32 + 4 + 63 = 103.
        var longest = GrainKeys.ResourceGroup(Subscription, new string('a', 63));

        longest.Length.ShouldBe(103);

        foreach (var key in Corpus.EveryGrainKeyShapeFor(Sample))
        {
            key.Length.ShouldBeLessThanOrEqualTo(103);
        }
    }

    [Fact]
    public void KeysUseInvariantFormattingRegardlessOfCulture() =>
        // InvariantGlobalization=true makes this belt-and-braces, but a grain key that changes with
        // the ambient culture is the kind of bug that only appears on one machine.
        GrainKeys.Resource(Resource)
            .ShouldBe("res/" + Resource.ToString("N", CultureInfo.InvariantCulture));

    // ── The two shapes docs/plan/06's table does NOT have ────────────────────────────────────
    //
    // ⚠ A DOC DEFECT, and these tests are the repair. docs/plan/04 § Grain taxonomy names
    // ITenantGrain in its Entity row (tenant-qualified, Durable) and ITenantDirectoryGrain /
    // IShardMapGrain / IProviderRegistryGrain in its Platform row (null-tenant, Durable) — and
    // docs/plan/06 § Grain keys has a row for none of them. Its eight rows start at
    // ISubscriptionGrain. Since that table is what closes GrainKeys' set, CyberCloud.Tenancy could
    // not have built any of those grains until one of the two documents moved.

    [Fact]
    public void TheTenantShapeIsTenantSlashTheTenantIdInNForm()
    {
        GrainKeys.Tenant(Tenant).ShouldBe("tenant/2b4a1c662e704a9d9d0a1f7ec1f1a4b3");

        var parsed = GrainKeys.Parse(GrainKeys.Tenant(Tenant)).GetValueOrThrow();
        parsed.Kind.ShouldBe(GrainKeyKind.Tenant);
        parsed.Id.ShouldBe(Tenant);
        parsed.ToString().ShouldBe(GrainKeys.Tenant(Tenant));
    }

    [Fact]
    public void ATenantKeyCannotBeConfusedWithAnyOtherShape()
    {
        // The same GUID under every shape, including the new one, must produce distinct strings
        // that each parse back to their own kind.
        var kinds = new[]
        {
            (GrainKeys.Tenant(Resource), GrainKeyKind.Tenant),
            (GrainKeys.Subscription(Resource), GrainKeyKind.Subscription),
            (GrainKeys.Resource(Resource), GrainKeyKind.Resource),
            (GrainKeys.User(Resource), GrainKeyKind.User),
            (GrainKeys.Operation(Resource), GrainKeyKind.Operation),
            (GrainKeys.ClusterConnection(Resource), GrainKeyKind.ClusterConnection),
        };

        kinds.Select(x => x.Item1).Distinct(StringComparer.Ordinal).Count().ShouldBe(kinds.Length);

        foreach (var (key, kind) in kinds)
        {
            GrainKeys.Parse(key).GetValueOrThrow().Kind.ShouldBe(kind);
        }
    }

    [Fact]
    public void ThePlatformSingletonsAreAClosedSetOfTwo()
    {
        GrainKeys.ShardMap().ShouldBe("platform/shard-map");
        GrainKeys.TenantDirectory().ShouldBe("platform/tenant-directory");

        GrainKeys.PlatformSingletons.ShouldBe(["shard-map", "tenant-directory"]);

        foreach (var name in GrainKeys.PlatformSingletons)
        {
            var key = GrainKeys.PlatformSingleton(name);
            var parsed = GrainKeys.Parse(key).GetValueOrThrow();

            parsed.Kind.ShouldBe(GrainKeyKind.PlatformSingleton);
            parsed.Name.ShouldBe(name);
            parsed.Id.ShouldBe(Guid.Empty);
            parsed.ToString().ShouldBe(key);
        }
    }

    [Theory]
    [InlineData("provider-registry")]
    [InlineData("tenants")]
    [InlineData("")]
    [InlineData("Shard-Map")]
    public void APlatformSingletonOutsideTheSetCannotBeBuilt(string name) =>
        // ⚠ The set is closed so that "platform singleton" never becomes a namespace somebody drops
        // a per-tenant key into — which would be docs/plan/04 § Grain taxonomy's low-cardinality
        // index grain with a different name. IProviderRegistryGrain is in that document's Platform
        // row and is deliberately NOT here: it belongs to the provider registry, not to tenancy,
        // and adding its name before its grain exists would be a key with nothing behind it.
        Should.Throw<ArgumentException>(() => GrainKeys.PlatformSingleton(name));

    [Theory]
    [InlineData("platform/provider-registry", "a singleton outside the closed set")]
    [InlineData("platform/shard-map/extra", "a third segment")]
    [InlineData("platform", "the prefix alone")]
    [InlineData("PLATFORM/shard-map", "upper-case prefix")]
    [InlineData("platform/Shard-Map", "upper-case name")]
    [InlineData("tenant/2b4a1c66-2e70-4a9d-9d0a-1f7ec1f1a4b3", "a hyphenated tenant id")]
    [InlineData("tenant", "the tenant prefix alone")]
    public void AForgedPlatformOrTenantKeyIsRejected(string forged, string why) =>
        GrainKeys.TryParse(forged, out _).ShouldBeFalse($"'{forged}' — {why}");

    [Fact]
    public void ThePlatformKeysAreNullTenantSafeInBothDirections()
    {
        // Same argument as the cluster-connection key: these are null-tenant, so they must pass
        // through Orleans.Multitenant's null-tenant branch as themselves, and must never read back
        // as belonging to a tenant.
        foreach (var key in new[] { GrainKeys.ShardMap(), GrainKeys.TenantDirectory() })
        {
            key.ShouldNotContain("|");

            OrleansMultitenantKeyModel.Qualify(null, key).ShouldBe(key);
            OrleansMultitenantKeyModel.ExtractTenant(key).ShouldBeNull();
            OrleansMultitenantKeyModel.ExtractKey(key).ShouldBe(key);

            GrainKeys.IsTenantQualificationSafe(key).ShouldBeTrue();
        }
    }

    [Fact]
    public void ATenantKeyIsTenantQualificationSafeSoThePhysicalKeySaysTheTenantTwice()
    {
        // The physical key is "{tenantId}|tenant/{tenantId:N}". Redundant on its face and not in
        // fact: a key read outside its qualification — in a repair tool, an audit export, a psql
        // session — still says which tenant it is.
        var tenant = Tenant.ToString("N", CultureInfo.InvariantCulture);
        var key = GrainKeys.Tenant(Tenant);

        GrainKeys.IsTenantQualificationSafe(key).ShouldBeTrue();
        OrleansMultitenantKeyModel.Qualify(tenant, key).ShouldBe(tenant + "|" + key);
        OrleansMultitenantKeyModel.ExtractKey(tenant + "|" + key).ShouldBe(key);
    }

    [Fact]
    public void TheEmptyGuidIsAKeyLikeAnyOtherBecauseItIsTheNullTenantsId() =>
        // docs/plan/06:171 — the platform tenant is Guid.Empty. Nothing here may special-case it.
        GrainKeys.Subscription(Guid.Empty)
            .ShouldBe("sub/00000000000000000000000000000000");
}

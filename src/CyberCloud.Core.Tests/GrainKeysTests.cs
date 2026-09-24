using CyberCloud.Core.Resources;
using Shouldly;
using System.Globalization;

namespace CyberCloud.Core.Tests;

/// <summary>
///     <see cref="GrainKeys" /> — the only type allowed to format or parse a grain key (ADR-002,
///     docs/plan/02 § ADR-002; the shape table, docs/plan/06 § Grain keys).
/// </summary>
public class GrainKeysTests {
    static readonly Guid Subscription = Guid.Parse("7f2d4e88-1a3b-4c5d-8e9f-0a1b2c3d4e5f");
    static readonly Guid Tenant = Guid.Parse("2b4a1c66-2e70-4a9d-9d0a-1f7ec1f1a4b3");
    static readonly Guid Resource = Guid.Parse("0a1b2c3d-4e5f-4071-8293-a4b5c6d7e8f9");

    static readonly ResourceId Sample = new(
        Tenant,
        Subscription,
        "prod",
        new("CyberCloud.DBforPostgreSQL", "servers"),
        "orders-db",
        Resource
    );

    // ── The six ReBAC shapes — docs/plan/07 § Storage, § ListObjects and § The Leopard index ───

    /// <summary>
    ///     The six <c>rel/</c> shapes, each with the kind it must decode to.
    /// </summary>
    /// <remarks>
    ///     Kept as a member rather than inline so that adding a shape means adding a row here, which
    ///     is the same discipline <c>Corpus.EveryGrainKeyShapeFor</c> applies to the other ten.
    /// </remarks>
    public static TheoryData<string, GrainKeyKind> RelationShapes =>
        new() {
            { GrainKeys.ObjectRelations("resourceGroup", "prod"), GrainKeyKind.ObjectRelations },
            { GrainKeys.SubjectRelations("user", "alice"), GrainKeyKind.SubjectRelations },
            { GrainKeys.CheckCache("resourceGroup", "prod"), GrainKeyKind.CheckCache },
            { GrainKeys.TupleStore(Tenant), GrainKeyKind.TupleStore },
            { GrainKeys.ListObjects("user", "alice"), GrainKeyKind.ListObjects },
            { GrainKeys.MembershipIndex("group", "eng"), GrainKeyKind.MembershipIndex }
        };

    [Fact]
    public void TheMembershipIndexShapeIsKeyedByTheSubjectObjectAndNotByAUserset() {
        // docs/plan/07 § Storage's row says rel/idx/{usersetType}/{usersetId}; the grain that landed
        // holds both directions for one subject OBJECT, so group:eng#member is rel/idx/group/eng with
        // `member` looked up inside — GrainKeys.MembershipIndex's remarks. The same tail as the
        // reverse index and the walk, and a third activation rather than either of theirs.
        GrainKeys.MembershipIndex("group", "eng").ShouldBe("rel/idx/group/eng");
        GrainKeys.MembershipIndex("group", "eng").ShouldNotBe(GrainKeys.SubjectRelations("group", "eng"));
        GrainKeys.MembershipIndex("group", "eng").ShouldNotBe(GrainKeys.ListObjects("group", "eng"));

        var parsed = GrainKeys.Parse("rel/idx/group/eng").GetValueOrThrow();

        parsed.Kind.ShouldBe(GrainKeyKind.MembershipIndex);
        parsed.ObjectType.ShouldBe("group");
        parsed.ObjectId.ShouldBe("eng");
        parsed.ToString().ShouldBe("rel/idx/group/eng");
    }

    [Fact]
    public void TheListObjectsShapeSharesTheReverseIndexTailAndIsStillADifferentKey() {
        // docs/plan/07 § ListObjects starts "from ISubjectRelationsGrain for the subject", so the
        // two keys carry the same subject — and must never be the same activation, or a listing
        // would hold the durable reverse index for its whole walk (GrainKeys.ListObjects' remarks).
        GrainKeys.ListObjects("user", "alice").ShouldBe("rel/list/user/alice");
        GrainKeys.ListObjects("user", "alice").ShouldNotBe(GrainKeys.SubjectRelations("user", "alice"));

        var parsed = GrainKeys.Parse(GrainKeys.ListObjects("user", "alice")).GetValueOrThrow();

        parsed.Kind.ShouldBe(GrainKeyKind.ListObjects);
        parsed.ObjectType.ShouldBe("user");
        parsed.ObjectId.ShouldBe("alice");
        parsed.ToString().ShouldBe("rel/list/user/alice");
    }

    // ── The eight shapes docs/plan/06 § Grain keys specifies ────────────────────────────────────────

    [Fact]
    public void EveryShapeIsExactlyWhatTheTableSays() {
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
    public void TheResourceKeyIsTheGuidAloneSoARenameAndAMoveAreMetadata() {
        // docs/plan/06 § Grain keys and the correction at docs/plan/02 § ADR-002. This is the whole reason
        // ResourceKey was deleted rather than ported: a key carrying the name would make a rename a
        // grain migration, and one carrying the resource group would make a move one too.
        var renamed = Sample with { Name = "billing-db" };
        var moved = Sample with { ResourceGroup = "staging" };
        var retyped = Sample with { Type = new("CyberCloud.Compute", "virtualMachines") };

        GrainKeys.Resource(renamed.Id).ShouldBe(GrainKeys.Resource(Sample.Id));
        GrainKeys.Resource(moved.Id).ShouldBe(GrainKeys.Resource(Sample.Id));
        GrainKeys.Resource(retyped.Id).ShouldBe(GrainKeys.Resource(Sample.Id));

        // …and the index key, which is the thing that DOES move, moves.
        GrainKeys.PathIndex(renamed).ShouldNotBe(GrainKeys.PathIndex(Sample));
        GrainKeys.PathIndex(moved).ShouldNotBe(GrainKeys.PathIndex(Sample));
    }

    [Fact]
    public void GuidsUseTheThirtyTwoDigitLowerCaseNForm() {
        string[] guidBearing = [
            GrainKeys.Subscription(Subscription),
            GrainKeys.Resource(Resource),
            GrainKeys.User(Resource),
            GrainKeys.Operation(Resource),
            GrainKeys.ClusterConnection(Resource)
        ];

        foreach (var key in guidBearing) {
            var guid = key.Split('/')[^1];

            guid.Length.ShouldBe(32, $"'{key}' does not end in the 32-digit N form");
            guid.ShouldNotContain("-");
            guid.ShouldBe(guid.ToLowerInvariant(), $"'{key}' spells a GUID in upper case");
        }
    }

    [Fact]
    public void TheDigestIsSixteenLowerCaseHexCharacters() {
        string[] indexKeys = [
            GrainKeys.PathIndex(Sample),
            GrainKeys.EmailIndex(Tenant, "alice@example.com")
        ];

        foreach (var key in indexKeys) {
            var digest = key.Split('/')[^1];

            digest.Length.ShouldBe(GrainKeys.DigestLength);

            foreach (var c in digest) {
                (c is >= '0' and <= '9' or >= 'a' and <= 'f').ShouldBeTrue(
                    $"'{digest}' contains '{c}', which is not lower-case hexadecimal"
                );
            }
        }
    }

    // ── Round trip, over a generated corpus rather than three hand-picked cases ────────────────

    [Fact]
    public void EveryShapeRoundTripsForEveryGeneratedId() {
        var count = 0;
        foreach (var id in Corpus.ResourceIds(3_000, 4242)) {
            foreach (var key in Corpus.EveryGrainKeyShapeFor(id)) {
                count++;

                GrainKeys.TryParse(key, out var parsed).ShouldBeTrue($"'{key}' should parse");
                parsed.ToString().ShouldBe(key);
            }
        }

        count.ShouldBe(3_000 * 8);
    }

    [Fact]
    public void EveryShapeParsesBackToTheKindThatBuiltIt() {
        foreach (var id in Corpus.ResourceIds(500, 11)) {
            var expected = new[] {
                GrainKeyKind.Subscription, GrainKeyKind.ResourceGroup, GrainKeyKind.Resource, GrainKeyKind.PathIndex,
                GrainKeyKind.User, GrainKeyKind.EmailIndex, GrainKeyKind.Operation, GrainKeyKind.ClusterConnection
            };

            var actual = Corpus.EveryGrainKeyShapeFor(id)
                .Select(static k => GrainKeys.Parse(k).GetValueOrThrow().Kind)
                .ToArray();

            actual.ShouldBe(expected);
        }
    }

    [Fact]
    public void ThePayloadSurvivesTheRoundTripAndNotJustTheString() {
        foreach (var id in Corpus.ResourceIds(500, 12)) {
            GrainKeys.Parse(GrainKeys.Subscription(id.SubscriptionId))
                .GetValueOrThrow()
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
    public void NoInputToOneFactoryCanProduceAStringAnotherFactoryCouldAlsoProduce() {
        // ⚠ THE ONE THAT MATTERS. If two shapes could ever produce one string, two unrelated
        // entities would share an activation and its state — a subscription reading a user's grain,
        // or an email claim satisfied by a path claim. The proof is not "these prefixes look
        // different": it is that over a large, adversarially-shaped corpus the map from key string
        // to (kind, payload) is injective in both directions.
        var seen = new Dictionary<string, (GrainKeyKind Kind, string Origin)>(StringComparer.Ordinal);

        // Ids sharing GUIDs across roles on purpose: the same GUID as subscription, resource, user,
        // operation and cluster at once is the input most likely to make two shapes coincide.
        foreach (var id in Corpus.ResourceIds(2_000, 5150)) {
            var shared = id with { SubscriptionId = id.Id };

            foreach (var candidate in Corpus.EveryGrainKeyShapeFor(id)
                         .Concat(Corpus.EveryGrainKeyShapeFor(shared))) {
                var kind = GrainKeys.Parse(candidate).GetValueOrThrow().Kind;
                var origin = $"{kind} from {id.Path}";

                if (seen.TryGetValue(candidate, out var previous)) {
                    previous.Kind.ShouldBe(
                        kind,
                        $"'{candidate}' was produced as {previous.Kind} by {previous.Origin} and "
                        + $"as {kind} by {origin} — two shapes collided"
                    );
                } else {
                    seen[candidate] = (kind, origin);
                }
            }
        }

        // Sanity: the corpus really did exercise all eight shapes.
        seen.Values.Select(static x => x.Kind).Distinct().Count().ShouldBe(8);
    }

    [Fact]
    public void ASubscriptionKeyIsAStrictPrefixOfItsResourceGroupKeysAndStillCannotBeConfusedWithOne() {
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
    public void AResourceGroupNamedRgIsUnambiguous() {
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
    [InlineData("parked")]
    public void AResourceGroupNamedAfterAnotherShapeIsStillAResourceGroup(string name) {
        var key = GrainKeys.ResourceGroup(Subscription, name);
        var parsed = GrainKeys.Parse(key).GetValueOrThrow();

        parsed.Kind.ShouldBe(GrainKeyKind.ResourceGroup);
        parsed.Name.ShouldBe(name);
        parsed.ToString().ShouldBe(key);
    }

    [Fact]
    public void TheFactoryRefusesAnInjectedResourceGroupName() {
        // Defence one: you cannot build the string in the first place.
        foreach (var (value, why) in Corpus.InjectionCharacters) {
            Should.Throw<ArgumentException>(
                () => GrainKeys.ResourceGroup(Subscription, "pr" + value + "od"),
                $"a resource group containing {Corpus.Printable(value)} ({why}) must not be "
                + "constructible"
            );
        }
    }

    [Fact]
    public void TheParserIsSafeEvenWhenNameValidationIsBypassed() {
        // Defence two, and the one that matters if a future caller composes a key by hand or a
        // physical key arrives from storage written by an older build. Nothing here may parse into
        // a DIFFERENT shape, and nothing may throw.
        foreach (var (value, why) in Corpus.InjectionCharacters) {
            var forged = "sub/7f2d4e881a3b4c5d8e9f0a1b2c3d4e5f/rg/pr" + value + "od";

            Should.NotThrow(() => GrainKeys.TryParse(forged, out _));

            if (GrainKeys.TryParse(forged, out var parsed)) {
                // The only tolerable outcome other than rejection is an exact round trip into the
                // same shape — never a re-cut into another one.
                parsed.Kind.ShouldBe(
                    GrainKeyKind.ResourceGroup,
                    $"'{Corpus.Printable(forged)}' ({why}) re-cut into a different shape"
                );
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
    public void AForgedShapeIsRejectedRatherThanReinterpreted(string forged, string why) {
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
    public void EveryGeneratedKeyIsSafeForTenantQualification() {
        // ADR-002's corrected encoding table (docs/plan/02 § ADR-002): Orleans.Multitenant copies the
        // key within the tenant VERBATIM and prefixes '~' only when it starts with '|' or '~'. So a
        // '|' is not corrupted — but the physical key in Redis, in a log line and in a trace stops
        // reading as the key we constructed. Every key here is trivially clean, so one that is not
        // is a bug.
        var tenant = Tenant.ToString("N", CultureInfo.InvariantCulture);

        foreach (var id in Corpus.ResourceIds(2_000, 99)) {
            foreach (var key in Corpus.EveryGrainKeyShapeFor(id)) {
                GrainKeys.IsTenantQualificationSafe(key)
                    .ShouldBeTrue($"'{key}' must contain no '|' and must not start with '|' or '~'");

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
    public void TheClusterKeyIsANullTenantKeyAndCannotBeMistakenForATenantedOne() {
        // docs/plan/06 § Grain keys — IClusterConnectionGrain is the one null-tenant grain, and
        // ADR-002's table (docs/plan/02 § ADR-002) records that the null-tenant branch is a DIFFERENT
        // encoding: no tenant prefix, no '~' rule, and the whole key has its '|' doubled.
        foreach (var id in Corpus.ResourceIds(200, 3).Select(static x => x.Id)) {
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
    public void ATenantedClusterKeyWouldBeADifferentStringWhichIsWhyTheExceptionIsWorthNoting() {
        // The failure mode the null-tenant note prevents: if somebody tenant-qualified this grain,
        // the same cluster would have one activation per tenant, each holding its own client and
        // its own watches — which is precisely the "two connections to one cluster" that
        // docs/plan/06 § Grain keys exists to stop. Recorded as an assertion so the distinction is
        // visible rather than folkloric.
        var key = GrainKeys.ClusterConnection(Resource);

        OrleansMultitenantKeyModel.Qualify(null, key)
            .ShouldNotBe(OrleansMultitenantKeyModel.Qualify(Tenant.ToString("N", CultureInfo.InvariantCulture), key));
    }

    // ── The path index hashes CanonicalPath, never Path ───────────────────────────────────────

    [Fact]
    public void TwoCaseDifferingPathsProduceTheSameIndexKey() {
        // ⚠ docs/plan/06 § Identifiers and docs/plan/02 § ADR-002. The provider namespace and type are
        // case-preserving on the wire, so one resource has more than one Path spelling. Hashing
        // Path would let each spelling claim the name and defeat the two-phase create at
        // docs/plan/06 § Two-phase create — the one place a duplicate claim is a correctness bug rather than
        // a cosmetic one.
        var mixed = Sample with { Type = new("CyberCloud.Cache", "Redis") };
        var lower = Sample with { Type = new("cybercloud.cache", "redis") };

        // They really are different strings — otherwise this test proves nothing.
        string.Equals(mixed.Path, lower.Path, StringComparison.Ordinal)
            .ShouldBeFalse("the two Path spellings must differ, or this test asserts nothing");
        mixed.CanonicalPath.ShouldBe(lower.CanonicalPath);

        GrainKeys.PathIndex(mixed).ShouldBe(GrainKeys.PathIndex(lower));
    }

    [Fact]
    public void EveryCaseSpellingOfEveryGeneratedPathAgreesOnOneIndexKey() {
        foreach (var id in Corpus.ResourceIds(1_000, 17)) {
            var upper = id with {
                Type = new(
                    id.Type.Namespace.ToUpperInvariant(),
                    id.Type.Type.ToUpperInvariant()
                )
            };

            GrainKeys.PathIndex(upper)
                .ShouldBe(
                    GrainKeys.PathIndex(id),
                    $"'{upper.Path}' and '{id.Path}' are one resource"
                );
        }
    }

    [Fact]
    public void TheIndexKeyIgnoresTheResourceGuidBecauseTheGuidIsTheAnswerNotTheQuestion() {
        // The index maps path -> GUID (docs/plan/06 § Identifiers). An id parsed from a path carries
        // Guid.Empty until the index resolves it, and it must hash to the same entry as the
        // resolved one or the claim could never be looked up.
        ResourceId.TryParsePath(Sample.Path, out var unresolved).ShouldBeTrue();
        unresolved.Id.ShouldBe(Guid.Empty);

        GrainKeys.PathIndex(unresolved).ShouldBe(GrainKeys.PathIndex(Sample));
        GrainKeys.PathIndex(Sample.WithId(Guid.NewGuid())).ShouldBe(GrainKeys.PathIndex(Sample));
    }

    [Fact]
    public void DistinctPathsProduceDistinctIndexKeys() {
        var byKey = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var id in Corpus.ResourceIds(3_000, 23)) {
            var key = GrainKeys.PathIndex(id);

            if (byKey.TryGetValue(key, out var existing)) {
                existing.ShouldBe(
                    id.CanonicalPath,
                    $"'{existing}' and '{id.CanonicalPath}' collided on index key '{key}'"
                );
            } else {
                byKey[key] = id.CanonicalPath;
            }
        }
    }

    [Fact]
    public void ACanonicalPathCannotContainTheDigestSeparator() {
        // The digest input is "{prefix}\n{value}". The separator is only unambiguous while the
        // value cannot contain one; ResourceNaming and ResourceTypeName are what guarantee that.
        foreach (var id in Corpus.ResourceIds(500, 29)) {
            id.CanonicalPath.ShouldNotContain("\n");
        }
    }

    // ── The client index: hash(tenantId + clientId), per tenant, case-sensitive ───────────────

    [Fact]
    public void ClientUniquenessIsPerTenant() {
        // docs/plan/11 § Protocol — a client_id is unique within a tenant, so `portal` in two
        // tenants is two distinct index entries, exactly as the email index is per tenant.
        var a = GrainKeys.ClientIndex(Tenant, "portal");
        var b = GrainKeys.ClientIndex(Subscription, "portal");

        a.ShouldNotBe(b);
        a.ShouldStartWith("idx/client/");
        b.ShouldStartWith("idx/client/");
    }

    [Fact]
    public void AClientIdIsCaseSensitiveUnlikeAnEmail() {
        // ⚠ An OAuth client_id is an opaque identifier compared byte for byte. Folding its case the
        // way the email index folds an address would merge two ids the protocol keeps apart.
        GrainKeys.ClientIndex(Tenant, "Portal").ShouldNotBe(GrainKeys.ClientIndex(Tenant, "portal"));
    }

    [Fact]
    public void TheClientIndexShapeRoundTripsToItsDigest() {
        var key = GrainKeys.ClientIndex(Tenant, "cyc");

        var parsed = GrainKeys.Parse(key).GetValueOrThrow();

        parsed.Kind.ShouldBe(GrainKeyKind.ClientIndex);
        parsed.Digest.Length.ShouldBe(GrainKeys.DigestLength);
        parsed.Id.ShouldBe(Guid.Empty);
        parsed.ToString().ShouldBe(key);
    }

    [Theory]
    [InlineData("")]
    [InlineData("with\nnewline")]
    [InlineData("with space")]
    [InlineData("  padded  ")]
    [InlineData("tab\tin")]
    public void AClientIdAKeyCannotCarryIsRefused(string clientId) {
        // ⚠ The newline is the digest separator; the rest are invisible in a log and turn one
        // identifier into two. EnsureValidClientId refuses them, and the builder throws.
        GrainKeys.EnsureValidClientId(clientId).IsFailure.ShouldBeTrue();
        Should.Throw<ArgumentException>(() => GrainKeys.ClientIndex(Tenant, clientId));
    }

    // ── The two #94 shapes: the code store and the consent grant — docs/plan/11 § Protocol ────

    [Fact]
    public void TheAuthorizationCodeShapeIsCodeSlashId() {
        // ⚠ Keyed by the code's jti and never by the code — GrainKeys.AuthorizationCode's remarks.
        GrainKeys.AuthorizationCode(Resource).ShouldBe("code/0a1b2c3d4e5f40718293a4b5c6d7e8f9");

        var parsed = GrainKeys.Parse(GrainKeys.AuthorizationCode(Resource)).GetValueOrThrow();

        parsed.Kind.ShouldBe(GrainKeyKind.AuthorizationCode);
        parsed.Id.ShouldBe(Resource);
        parsed.ToString().ShouldBe(GrainKeys.AuthorizationCode(Resource));

        // The same GUID under every other two-segment prefix is a different key, and the D form is
        // not accepted — the canonicity rule every other GUID shape is held to.
        GrainKeys.AuthorizationCode(Resource).ShouldNotBe(GrainKeys.Session(Resource));
        GrainKeys.Parse("code/" + Resource.ToString("D")).IsFailure.ShouldBeTrue();
    }

    [Fact]
    public void AConsentGrantIsOneActivationPerPersonAndClientInOneTenant() {
        var alice = Guid.Parse("a11ce000-0000-4000-8000-000000000001");
        var bob = Guid.Parse("b0b00000-0000-4000-8000-000000000002");

        var key = GrainKeys.ConsentGrant(Tenant, alice, "crm");

        key.ShouldStartWith("consent/");
        key.Length.ShouldBe("consent/".Length + GrainKeys.DigestLength);

        // A different person, a different client, or the same pair in another tenant is another
        // grant; the client id is compared byte for byte, as ClientIndex compares it.
        key.ShouldNotBe(GrainKeys.ConsentGrant(Tenant, bob, "crm"));
        key.ShouldNotBe(GrainKeys.ConsentGrant(Tenant, alice, "erp"));
        key.ShouldNotBe(GrainKeys.ConsentGrant(Subscription, alice, "crm"));
        key.ShouldNotBe(GrainKeys.ConsentGrant(Tenant, alice, "CRM"));
        key.ShouldBe(GrainKeys.ConsentGrant(Tenant, alice, "crm"));
    }

    [Fact]
    public void TheConsentGrantShapeRoundTripsToItsDigestAndRefusesAnythingElse() {
        var key = GrainKeys.ConsentGrant(Tenant, Resource, "crm");

        var parsed = GrainKeys.Parse(key).GetValueOrThrow();

        parsed.Kind.ShouldBe(GrainKeyKind.ConsentGrant);
        parsed.Digest.Length.ShouldBe(GrainKeys.DigestLength);
        parsed.Id.ShouldBe(Guid.Empty);
        parsed.ToString().ShouldBe(key);

        // ⚠ The one two-segment shape whose payload is a digest: a GUID after `consent/` is not a
        // key, and neither is upper-case hex or a digest of the wrong length.
        GrainKeys.Parse("consent/" + Resource.ToString("N")).IsFailure.ShouldBeTrue();
        GrainKeys.Parse("consent/" + parsed.Digest.ToUpperInvariant()).IsFailure.ShouldBeTrue();
        GrainKeys.Parse("consent/" + parsed.Digest[..8]).IsFailure.ShouldBeTrue();
        GrainKeys.Parse("consent/" + parsed.Digest + "/extra").IsFailure.ShouldBeTrue();

        // And a client id a key cannot carry throws here as it does at ClientIndex.
        Should.Throw<ArgumentException>(static () => GrainKeys.ConsentGrant(Tenant, Resource, "with\nnewline"));
        Should.Throw<ArgumentException>(static () => GrainKeys.ConsentGrant(Tenant, Resource, ""));
    }

    [Fact]
    public void TheTwoNewShapesCollideWithNoneOfTheOthers() {
        // The property the whole file is about, extended to the two #94 shapes: over the corpus, no
        // string one of them mints is minted by any other factory, and both survive qualification.
        foreach (var id in Corpus.ResourceIds(300, 9400)) {
            var code = GrainKeys.AuthorizationCode(id.Id);
            var consent = GrainKeys.ConsentGrant(id.TenantId, id.Id, id.Name);

            GrainKeys.IsTenantQualificationSafe(code).ShouldBeTrue();
            GrainKeys.IsTenantQualificationSafe(consent).ShouldBeTrue();

            foreach (var other in Corpus.EveryGrainKeyShapeFor(id)) {
                other.ShouldNotBe(code);
                other.ShouldNotBe(consent);
            }

            GrainKeys.Parse(code).GetValueOrThrow().Kind.ShouldBe(GrainKeyKind.AuthorizationCode);
            GrainKeys.Parse(consent).GetValueOrThrow().Kind.ShouldBe(GrainKeyKind.ConsentGrant);
        }
    }

    // ── #43: the device authorization and the invitation ───────────────────────────────────────

    [Fact]
    public void ADeviceAuthorizationIsKeyedByItsUserCodesDigestAndNeverSpellsTheCode() {
        var key = GrainKeys.DeviceAuthorization("BCDFGHJK");

        key.ShouldStartWith("device/");
        key.Length.ShouldBe("device/".Length + GrainKeys.DigestLength);
        // ⚠ The code is a credential for its ten minutes; a key that spelled it would print it in
        // every log line that prints a grain id — GrainKeys.DeviceAuthorization's remarks.
        key.ShouldNotContain("BCDFGHJK");
        key.ShouldNotBe(GrainKeys.DeviceAuthorization("BCDFGHJL"));

        var parsed = GrainKeys.Parse(key).GetValueOrThrow();

        parsed.Kind.ShouldBe(GrainKeyKind.DeviceAuthorization);
        parsed.ToString().ShouldBe(key);

        // Normalized input only: a separator, lower case or a control character is the caller's
        // bug, and a digest of the un-normalized form would be a second grain for one code.
        Should.Throw<ArgumentException>(static () => GrainKeys.DeviceAuthorization("BCDF-GHJK"));
        Should.Throw<ArgumentException>(static () => GrainKeys.DeviceAuthorization("bcdfghjk"));
        Should.Throw<ArgumentException>(static () => GrainKeys.DeviceAuthorization(""));
        GrainKeys.Parse("device/" + Resource.ToString("N")).IsFailure.ShouldBeTrue();
        GrainKeys.Parse("device/" + parsed.Digest.ToUpperInvariant()).IsFailure.ShouldBeTrue();
    }

    [Fact]
    public void TheInvitationShapeIsInviteSlashIdAndRoundTrips() {
        GrainKeys.Invitation(Resource).ShouldBe("invite/0a1b2c3d4e5f40718293a4b5c6d7e8f9");

        var parsed = GrainKeys.Parse(GrainKeys.Invitation(Resource)).GetValueOrThrow();

        parsed.Kind.ShouldBe(GrainKeyKind.Invitation);
        parsed.Id.ShouldBe(Resource);
        GrainKeys.Parse("invite/" + Resource.ToString("D")).IsFailure.ShouldBeTrue();
    }

    [Fact]
    public void TheDirectoryIndexIsOneKeyPerCollectionFromAClosedSet() {
        GrainKeys.DirectoryIndex(GrainKeys.DirectoryUsers).ShouldBe("idx/dir/users");
        GrainKeys.DirectoryIndex(GrainKeys.DirectoryInvitations).ShouldBe("idx/dir/invitations");
        GrainKeys.DirectoryIndex(GrainKeys.DirectoryApplications).ShouldBe("idx/dir/applications");

        foreach (var collection in GrainKeys.DirectoryCollections) {
            var key = GrainKeys.DirectoryIndex(collection);
            var parsed = GrainKeys.Parse(key).GetValueOrThrow();

            parsed.Kind.ShouldBe(GrainKeyKind.DirectoryIndex);
            parsed.Name.ShouldBe(collection);
            parsed.Digest.ShouldBeEmpty("the one idx/ shape whose payload is a name, not a digest");
            parsed.ToString().ShouldBe(key);
            GrainKeys.IsTenantQualificationSafe(key).ShouldBeTrue();
        }

        // Closed: a fourth collection, a near-miss spelling and a digest in the name's place are
        // refused rather than read as a list nobody writes to.
        Should.Throw<ArgumentException>(static () => GrainKeys.DirectoryIndex("groups"));
        GrainKeys.Parse("idx/dir/groups").IsFailure.ShouldBeTrue();
        GrainKeys.Parse("idx/dir/Users").IsFailure.ShouldBeTrue();
        GrainKeys.Parse("idx/dir/0123456789abcdef").IsFailure.ShouldBeTrue();
        GrainKeys.Parse("idx/dir/users/extra").IsFailure.ShouldBeTrue();
    }

    [Fact]
    public void TheDirectoryIndexCollidesWithNoOtherShape() {
        var directory = GrainKeys.DirectoryCollections.Select(GrainKeys.DirectoryIndex).ToArray();

        foreach (var id in Corpus.ResourceIds(300, 4100)) {
            foreach (var other in Corpus.EveryGrainKeyShapeFor(id)) {
                directory.ShouldNotContain(other);
            }

            directory.ShouldNotContain(GrainKeys.Invitation(id.Id));
            directory.ShouldNotContain(GrainKeys.ClientIndex(id.TenantId, id.Name));
        }
    }

    [Fact]
    public void TheTwoIssue43ShapesCollideWithNoneOfTheOthers() {
        foreach (var id in Corpus.ResourceIds(300, 4300)) {
            var device = GrainKeys.DeviceAuthorization(id.Id.ToString("N")[..8].ToUpperInvariant());
            var invite = GrainKeys.Invitation(id.Id);

            GrainKeys.IsTenantQualificationSafe(device).ShouldBeTrue();
            GrainKeys.IsTenantQualificationSafe(invite).ShouldBeTrue();

            foreach (var other in Corpus.EveryGrainKeyShapeFor(id)) {
                other.ShouldNotBe(device);
                other.ShouldNotBe(invite);
            }
        }
    }

    // ── The watch index: hash(subscriptionId + canonical type), per subscription ──────────────

    [Fact]
    public void AWatchIsPerSubscriptionAndPerType() {
        // docs/plan/08 § What the resource manager deliberately does not do — a provider hears about
        // "resources of type X in my subscription", so the same type in two subscriptions is two
        // entries, and two types in one subscription are two entries.
        var shares = new ResourceTypeName("CyberCloud.Storage", "accounts/fileShares");
        var accounts = new ResourceTypeName("CyberCloud.Storage", "accounts");

        var a = GrainKeys.WatchIndex(Subscription, shares);
        var b = GrainKeys.WatchIndex(Tenant, shares);
        var c = GrainKeys.WatchIndex(Subscription, accounts);

        a.ShouldNotBe(b);
        a.ShouldNotBe(c);
        a.ShouldStartWith("idx/watch/");
    }

    [Fact]
    public void AWatchFoldsTheTypeToItsCanonicalSpelling() {
        // ⚠ The provider namespace is case-preserving, so two spellings of one type must be one watch
        // list — otherwise a change reaches whichever half spelled it the way the writer did.
        var spelled = new ResourceTypeName("CyberCloud.Storage", "accounts/fileShares");
        var lowered = new ResourceTypeName("cybercloud.storage", "accounts/fileshares");

        GrainKeys.WatchIndex(Subscription, spelled).ShouldBe(GrainKeys.WatchIndex(Subscription, lowered));
    }

    [Fact]
    public void TheWatchIndexShapeRoundTripsToItsDigest() {
        var key = GrainKeys.WatchIndex(Subscription, new("CyberCloud.Sample", "widgets"));

        var parsed = GrainKeys.Parse(key).GetValueOrThrow();

        parsed.Kind.ShouldBe(GrainKeyKind.WatchIndex);
        parsed.Digest.Length.ShouldBe(GrainKeys.DigestLength);
        parsed.Id.ShouldBe(Guid.Empty);
        parsed.ToString().ShouldBe(key);
    }

    [Fact]
    public void AWatchOnNoTypeIsRefused() {
        Should.Throw<ArgumentException>(static () => GrainKeys.WatchIndex(Subscription, default));
    }

    // ── The email index: hash(tenantId + normalized email), per tenant ────────────────────────

    [Fact]
    public void EmailUniquenessIsPerTenant() {
        // docs/plan/11 § Sign-up and tenant creation — "email uniqueness is per tenant; global
        // email uniqueness would be a global index — the thing we do not have and do not want."
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
    [InlineData("\u00A0alice@example.com\u00A0")]
    public void CaseAndSurroundingWhitespaceDoNotChangeTheKey(string email) =>
        GrainKeys.EmailIndex(Tenant, email)
            .ShouldBe(GrainKeys.EmailIndex(Tenant, "alice@example.com"));

    [Fact]
    public void TheNormalizedFormIsTheOneToStore() {
        // Storing a differently-normalized address on the user than the one that went into the
        // digest is how an account becomes unfindable by its own email, so the rule is public.
        GrainKeys.NormalizeEmail("  Alice@Example.COM ")
            .GetValueOrThrow()
            .ShouldBe("alice@example.com");
    }

    [Fact]
    public void AsciiCaseFoldingIsTheOnlyThingThatMergesTwoSpellings() {
        // ⚠ THE UNICODE TRAP, recorded against the BCL rather than against our code.
        // "K" is KELVIN SIGN. ToLowerInvariant folds it onto 'k', so a normalizer built on it
        // would make aK@x and ak@x one key — one account silently claiming another's identity at
        // sign-up, which the two-phase claim cannot tell from a genuine duplicate.
        "K".ToLowerInvariant()
            .ShouldBe(
                "k",
                "if this ever goes false the BCL changed; the ASCII-only rule is still correct"
            );

        // …and the ASCII-only rule does not.
        GrainKeys.EmailIndex(Tenant, "aK@example.com")
            .ShouldNotBe(GrainKeys.EmailIndex(Tenant, "ak@example.com"));

        // The trap in the other direction: U+0130 LATIN CAPITAL LETTER I WITH DOT ABOVE does NOT
        // lower-case to "i" — it produces two code points — so a rule that assumed it did would
        // canonicalise inconsistently. Here it is left alone and stays a different address.
        "İ".ToLowerInvariant().ShouldNotBe("i");

        GrainKeys.EmailIndex(Tenant, "İrem@example.com")
            .ShouldNotBe(GrainKeys.EmailIndex(Tenant, "irem@example.com"));

        // Turkish dotless ı is a third distinct letter, not a spelling of 'i'.
        GrainKeys.EmailIndex(Tenant, "ırem@example.com")
            .ShouldNotBe(GrainKeys.EmailIndex(Tenant, "irem@example.com"));
    }

    [Fact]
    public void TwoDifferentEmailsNeverNormalizeToTheSameKey() {
        var byKey = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var email in Corpus.DistinctEmails(5_000, 31)) {
            var normalized = GrainKeys.NormalizeEmail(email).GetValueOrThrow();
            var key = GrainKeys.EmailIndex(Tenant, email);

            if (byKey.TryGetValue(key, out var existing)) {
                existing.ShouldBe(
                    normalized,
                    $"'{existing}' and '{normalized}' collided on email index key '{key}'"
                );
            } else {
                byKey[key] = normalized;
            }
        }

        // The look-alike code points from the injection corpus, each spliced into an address that
        // is otherwise identical. None may share a key with the plain form.
        var keys = new HashSet<string>(StringComparer.Ordinal) { GrainKeys.EmailIndex(Tenant, "alice@example.com") };

        foreach (var (value, why) in Corpus.InjectionCharacters) {
            var email = "al" + value + "ice@example.com";
            var normalized = GrainKeys.NormalizeEmail(email);

            if (normalized.IsFailure) {
                continue;
            }

            keys.Add(GrainKeys.EmailIndex(Tenant, email))
                .ShouldBeTrue($"'{Corpus.Printable(email)}' ({why}) shares a key with another address");
        }
    }

    [Fact]
    public void TheSameEmailInTheSameTenantIsAlwaysTheSameKey() {
        // Determinism across calls: a digest that varied would make every claim unfindable.
        foreach (var email in Corpus.DistinctEmails(500, 37)) {
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
    public void AnAddressThatIsNotOneIsRejected(string? email, string why) {
        GrainKeys.NormalizeEmail(email).IsFailure.ShouldBeTrue($"'{email}' — {why}");

        if (email is not null) {
            Should.Throw<ArgumentException>(() => GrainKeys.EmailIndex(Tenant, email));
        }
    }

    [Fact]
    public void AnOverlongAddressIsRejectedRatherThanHashed() {
        var local = new string('a', GrainKeys.MaxEmailLength);

        GrainKeys.NormalizeEmail(local + "@example.com").IsFailure.ShouldBeTrue();
        GrainKeys.NormalizeEmail(new string('a', GrainKeys.MaxEmailLength - 12) + "@example.com")
            .IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void ANormalizedEmailCannotContainTheDigestSeparator() {
        // Same argument as for the path: the digest input is "{prefix}\n{tenant:N}\n{email}", and
        // the tenant's N form is fixed at 32 characters, so the cut points are unambiguous only
        // while neither component can contain a '\n'.
        foreach (var email in Corpus.DistinctEmails(200, 41)) {
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
    [InlineData(
        "7f2d4e881a3b4c5d8e9f0a1b2c3d4e5f/prod/CyberCloud.DBforPostgreSQL/servers/0a1b2c3d4e5f40718293a4b5c6d7e8f9"
    )]
    public void MalformedInputReturnsFalseAndNeverThrows(string? text) {
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
            out _
        )
            .ShouldBeFalse();

    [Fact]
    public void DefaultGrainKeyIsNotAKey() {
        default(GrainKey).Kind.ShouldBe(GrainKeyKind.None);
        default(GrainKey).ToString().ShouldBeEmpty();
        default(GrainKey).Name.ShouldBeEmpty();
        default(GrainKey).Digest.ShouldBeEmpty();
    }

    // ── Bounds and culture ────────────────────────────────────────────────────────────────────

    [Fact]
    public void EveryKeyIsBoundedInLengthSoRedisKeysAreNotUnbounded() {
        // A grain key becomes a Redis key (docs/plan/05); an unbounded one is a memory problem.
        //
        // ⚠ THE LONGEST SHAPE IN THE TYPE IS NO LONGER sub/{32}/rg/{63}, AND THE NUMBER IS RECOUNTED
        // RATHER THAN CARRIED. GrainKeys.ParkedResourceRegistry addresses the same resource group
        // through a longer prefix, so the longest key the type can build moved from 103 to 106
        // characters when it landed. Counted by hand on 2026-09-05, segment by segment, with
        // ResourceNaming's 63-character ceiling on a resource group name as the only variable part:
        //
        //   sub/{32}/rg/{63}     = 4 + 32 + 4 + 63 = 103
        //   parked/{32}/rg/{63}  = 7 + 32 + 4 + 63 = 106
        //   sweep/{32}/rg/{63}   = 6 + 32 + 4 + 63 = 105
        //
        // ⚠ THE THIRD LINE IS ISSUE #12'S AND IT DID NOT MOVE THE MAXIMUM, which is the fact worth
        // asserting rather than assuming: `sweep/` is one character shorter than `parked/`, so the
        // bound stays at 106 and this test would have passed without the line. It is here because
        // the previous sentence said a third shape "has to come here and say so", and a shape that
        // came here and was silently absent would leave the next reader recounting from the enum.
        // Re-counted by hand on 2026-09-05 over all three prefixes.
        //
        // All three are asserted by name, which is what makes a fourth shape with a longer prefix
        // have to come here and say so.
        var group = GrainKeys.ResourceGroup(Subscription, new('a', 63));
        var parked = GrainKeys.ParkedResourceRegistry(Subscription, new('a', 63));
        var sweeper = GrainKeys.ExpirySweeper(Subscription, new('a', 63));

        group.Length.ShouldBe(103);
        parked.Length.ShouldBe(106);
        sweeper.Length.ShouldBe(105);

        // ⚠ 103 AND NOT 106 BELOW, AND THE TWO NUMBERS ARE NOT IN CONFLICT — the loop's scope is
        // narrower than the type's. Corpus.EveryGrainKeyShapeFor yields docs/plan/06 § Grain keys'
        // EIGHT shapes and not the type's twenty-one (see its remarks: it is that table, deliberately,
        // not an inventory of GrainKeys), and 103 is the true maximum over those eight. Raising it
        // to 106 to match the sentence above would weaken the only bound this loop enforces in
        // exchange for nothing, since the parked shape is not among the eight it walks and is
        // asserted exactly, one line up. What the loop catches that the two lines above cannot is
        // one of those eight growing a component — a longer digest, a prefix renamed — which is a
        // change nobody would think to come back here for.
        foreach (var key in Corpus.EveryGrainKeyShapeFor(Sample)) {
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
    public void TheTenantShapeIsTenantSlashTheTenantIdInNForm() {
        GrainKeys.Tenant(Tenant).ShouldBe("tenant/2b4a1c662e704a9d9d0a1f7ec1f1a4b3");

        var parsed = GrainKeys.Parse(GrainKeys.Tenant(Tenant)).GetValueOrThrow();
        parsed.Kind.ShouldBe(GrainKeyKind.Tenant);
        parsed.Id.ShouldBe(Tenant);
        parsed.ToString().ShouldBe(GrainKeys.Tenant(Tenant));
    }

    [Fact]
    public void ATenantKeyCannotBeConfusedWithAnyOtherShape() {
        // The same GUID under every shape, including the new one, must produce distinct strings
        // that each parse back to their own kind.
        var kinds = new[] {
            (GrainKeys.Tenant(Resource), GrainKeyKind.Tenant),
            (GrainKeys.Subscription(Resource), GrainKeyKind.Subscription),
            (GrainKeys.Resource(Resource), GrainKeyKind.Resource), (GrainKeys.User(Resource), GrainKeyKind.User),
            (GrainKeys.Operation(Resource), GrainKeyKind.Operation),
            (GrainKeys.ClusterConnection(Resource), GrainKeyKind.ClusterConnection)
        };

        kinds.Select(static x => x.Item1).Distinct(StringComparer.Ordinal).Count().ShouldBe(kinds.Length);

        foreach (var (key, kind) in kinds) {
            GrainKeys.Parse(key).GetValueOrThrow().Kind.ShouldBe(kind);
        }
    }

    [Fact]
    public void ThePlatformSingletonsAreAClosedSetOfTwo() {
        GrainKeys.ShardMap().ShouldBe("platform/shard-map");
        GrainKeys.TenantDirectory().ShouldBe("platform/tenant-directory");

        GrainKeys.PlatformSingletons.ShouldBe(["shard-map", "tenant-directory"]);

        foreach (var name in GrainKeys.PlatformSingletons) {
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
    public void ThePlatformKeysAreNullTenantSafeInBothDirections() {
        // Same argument as the cluster-connection key: these are null-tenant, so they must pass
        // through Orleans.Multitenant's null-tenant branch as themselves, and must never read back
        // as belonging to a tenant.
        foreach (var key in new[] { GrainKeys.ShardMap(), GrainKeys.TenantDirectory() }) {
            key.ShouldNotContain("|");

            OrleansMultitenantKeyModel.Qualify(null, key).ShouldBe(key);
            OrleansMultitenantKeyModel.ExtractTenant(key).ShouldBeNull();
            OrleansMultitenantKeyModel.ExtractKey(key).ShouldBe(key);

            GrainKeys.IsTenantQualificationSafe(key).ShouldBeTrue();
        }
    }

    [Fact]
    public void ATenantKeyIsTenantQualificationSafeSoThePhysicalKeySaysTheTenantTwice() {
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
        // docs/plan/06 § Platform administration — the platform tenant is Guid.Empty. Nothing here may special-case it.
        GrainKeys.Subscription(Guid.Empty)
            .ShouldBe("sub/00000000000000000000000000000000");

    [Theory]
    [MemberData(nameof(RelationShapes))]
    public void EveryRelationShapeRoundTripsToTheKindThatBuiltIt(string key, GrainKeyKind expected) {
        var parsed = GrainKeys.Parse(key);

        parsed.IsSuccess.ShouldBeTrue(parsed.Error?.Message);
        parsed.GetValueOrThrow().Kind.ShouldBe(expected);
        parsed.GetValueOrThrow().ToString().ShouldBe(key);
        GrainKeys.IsTenantQualificationSafe(key).ShouldBeTrue();
    }

    [Fact]
    public void ARelationKeyCarriesItsObjectTypeAndIdSeparately() {
        // The payload must survive, not only the string: a key that round-trips but decodes to the
        // wrong object would route a tuple read to another entity's grain.
        var parsed = GrainKeys.Parse(GrainKeys.ObjectRelations("resourceGroup", "prod"))
            .GetValueOrThrow();

        parsed.ObjectType.ShouldBe("resourceGroup");
        parsed.ObjectId.ShouldBe("prod");
        parsed.Id.ShouldBe(Guid.Empty);
        parsed.Name.ShouldBeEmpty();
        parsed.Digest.ShouldBeEmpty();
    }

    [Fact]
    public void ATupleStoreKeyCarriesItsTenantId() {
        GrainKeys.Parse(GrainKeys.TupleStore(Tenant)).GetValueOrThrow().Id.ShouldBe(Tenant);
    }

    [Fact]
    public void AGuidIsAnObjectIdBecauseTheNFormIsAValidName() {
        // docs/plan/07 § The model says "ids are GUIDs"; the N form satisfies ResourceNaming, so
        // the ordinary case needs no special rule.
        var key = GrainKeys.ObjectRelations("resource", Tenant.ToString("N", CultureInfo.InvariantCulture));

        GrainKeys.Parse(key)
            .GetValueOrThrow()
            .ObjectId
                .ShouldBe(Tenant.ToString("N", CultureInfo.InvariantCulture));
    }

    [Theory]
    [InlineData("ResourceGroup", "prod", "the type starts upper-case")]
    [InlineData("resource-group", "prod", "the type contains a hyphen")]
    [InlineData("resource:group", "prod", "the type contains the type/id separator")]
    [InlineData("resource/group", "prod", "the type contains the key separator")]
    [InlineData("resource#group", "prod", "the type contains the tuple separator")]
    [InlineData("", "prod", "the type is empty")]
    [InlineData("resourceGroup", "PROD", "the id is upper-case")]
    [InlineData("resourceGroup", "-prod", "the id starts with a hyphen")]
    [InlineData("resourceGroup", "pr od", "the id contains a space")]
    [InlineData("resourceGroup", "pr/od", "the id contains the key separator")]
    [InlineData("resourceGroup", "", "the id is empty")]
    public void AnIllegalRelationKeyComponentIsRefusedAtConstruction(
        string type,
        string id,
        string why
    ) =>
        Should.Throw<ArgumentException>(
            () => GrainKeys.ObjectRelations(type, id),
            $"'{type}':'{id}' — {why}"
        );

    [Theory]
    [InlineData("rel/obj/resourceGroup", "three segments is not an object-relations key")]
    [InlineData("rel/obj/resourceGroup/prod/extra", "trailing junk")]
    [InlineData("rel/idx/group", "three segments is not a membership-index key")]
    [InlineData("rel/store/not-a-guid", "the tenant id must be the N form")]
    [InlineData("rel/store", "the store key needs a tenant")]
    [InlineData("rel/store/7f2d4e88-1a3b-4c5d-8e9f-0a1b2c3d4e5f", "the D form is not the N form")]
    [InlineData("rel/check/resourceGroup", "three segments is not a check key")]
    [InlineData("rel/list/user", "three segments is not a list-objects key")]
    [InlineData("rel/nope/resourceGroup/prod", "an unknown authorization shape")]
    public void AMalformedRelationKeyIsRefused(string key, string why) =>
        GrainKeys.Parse(key).IsFailure.ShouldBeTrue($"'{key}' — {why}");

    [Fact]
    public void NoRelationShapeCanProduceAStringAnotherShapeCouldAlsoProduce() {
        // The same property the other ten shapes are held to, extended across the boundary: a key
        // that two factories could both mint is two unrelated entities sharing one activation —
        // here, one object's tuples answering for another's.
        Dictionary<string, GrainKeyKind> seen = new(StringComparer.Ordinal);

        foreach (var type in new[] { "resourceGroup", "user", "group", "sub", "res", "idx", "rel" }) {
            foreach (var id in new[] { "prod", "rg", "path", "email", "store", "obj", "check", "list" }) {
                foreach (var key in new[] {
                             GrainKeys.ObjectRelations(type, id), GrainKeys.SubjectRelations(type, id),
                             GrainKeys.CheckCache(type, id), GrainKeys.ListObjects(type, id),
                             GrainKeys.MembershipIndex(type, id)
                         }) {
                    var kind = GrainKeys.Parse(key).GetValueOrThrow().Kind;

                    if (seen.TryGetValue(key, out var already)) {
                        already.ShouldBe(kind, $"'{key}' is produced by two different shapes");
                    }

                    seen[key] = kind;
                }
            }
        }

        // …and none of them collides with any of the original ten either.
        foreach (var id in Corpus.ResourceIds(200, 707)) {
            foreach (var other in Corpus.EveryGrainKeyShapeFor(id)) {
                seen.ShouldNotContainKey(other);
            }
        }
    }

    // ── The parked-resource registry — docs/plan/08 § Soft delete, issue #71 ──────────────────
    //
    // ⚠ THE SHAPE THAT SHARES A TAIL WITH ANOTHER ONE, which no other pair in this type does. Every
    // shape but these two is told apart by its first segment AND a segment count nothing else has;
    // `parked/{sub}/rg/{name}` and `sub/{sub}/rg/{name}` differ by their first segment alone, and
    // they carry the SAME payload — the same subscription, the same group name — so a parser that
    // cut on anything but that segment would return a well-formed key of the wrong kind rather than
    // a rejection. That is the failure worth a section of its own: it would route a listing of
    // parked resources at the group's own membership, which is the merge docs/plan/08 § Soft delete
    // refuses in as many words.

    [Fact]
    public void TheParkedRegistryShapeIsParkedSlashSubscriptionSlashRgSlashName() {
        GrainKeys.ParkedResourceRegistry(Subscription, "prod")
            .ShouldBe("parked/7f2d4e881a3b4c5d8e9f0a1b2c3d4e5f/rg/prod");

        var parsed = GrainKeys.Parse(GrainKeys.ParkedResourceRegistry(Subscription, "prod")).GetValueOrThrow();

        parsed.Kind.ShouldBe(GrainKeyKind.ParkedResourceRegistry);
        parsed.Id.ShouldBe(Subscription);
        parsed.Name.ShouldBe("prod");
        parsed.ToString().ShouldBe(GrainKeys.ParkedResourceRegistry(Subscription, "prod"));
    }

    [Fact]
    public void TheParkedRegistryAndItsResourceGroupAreTwoKeysWithOnePayload() {
        // The property the fork in ParseFourSegments has to have, over a corpus rather than one
        // pair: for every (subscription, group) the two shapes produce two DIFFERENT strings that
        // decode to two different kinds and to the SAME subscription and name. A parser that folded
        // them would satisfy the payload half and fail the kind half, which is why both are asserted.
        foreach (var id in Corpus.ResourceIds(500, 71)) {
            var group = GrainKeys.ResourceGroup(id.SubscriptionId, id.ResourceGroup);
            var parked = GrainKeys.ParkedResourceRegistry(id.SubscriptionId, id.ResourceGroup);

            parked.ShouldNotBe(group);

            var decodedGroup = GrainKeys.Parse(group).GetValueOrThrow();
            var decodedParked = GrainKeys.Parse(parked).GetValueOrThrow();

            decodedGroup.Kind.ShouldBe(GrainKeyKind.ResourceGroup);
            decodedParked.Kind.ShouldBe(GrainKeyKind.ParkedResourceRegistry);

            decodedParked.Id.ShouldBe(decodedGroup.Id);
            decodedParked.Name.ShouldBe(decodedGroup.Name);

            GrainKeys.IsTenantQualificationSafe(parked).ShouldBeTrue();
        }
    }

    [Fact]
    public void AResourceGroupCalledParkedIsStillNotAParkedRegistryKey() {
        // `parked` is a legal resource group name under ResourceNaming, so the near-miss below is
        // constructible by a tenant rather than only by a forger.
        var group = GrainKeys.ResourceGroup(Subscription, "parked");
        var registry = GrainKeys.ParkedResourceRegistry(Subscription, "prod");

        group.ShouldNotBe(registry);
        GrainKeys.Parse(group).GetValueOrThrow().Kind.ShouldBe(GrainKeyKind.ResourceGroup);

        // …and the registry of the group that is itself called `parked`, which is the one string a
        // reader is most likely to misread. It is a registry key, and its name is `parked`.
        var registryOfParked = GrainKeys.ParkedResourceRegistry(Subscription, "parked");
        var decoded = GrainKeys.Parse(registryOfParked).GetValueOrThrow();

        registryOfParked.ShouldBe("parked/7f2d4e881a3b4c5d8e9f0a1b2c3d4e5f/rg/parked");
        decoded.Kind.ShouldBe(GrainKeyKind.ParkedResourceRegistry);
        decoded.Name.ShouldBe("parked");
    }

    [Theory]
    [InlineData("parked/7f2d4e881a3b4c5d8e9f0a1b2c3d4e5f", "the prefix and a subscription is not the shape")]
    [InlineData("parked/7f2d4e881a3b4c5d8e9f0a1b2c3d4e5f/rg", "the marker with no name")]
    [InlineData("parked/7f2d4e881a3b4c5d8e9f0a1b2c3d4e5f/xx/prod", "the wrong marker")]
    [InlineData("parked/7f2d4e881a3b4c5d8e9f0a1b2c3d4e5f/rg/prod/extra", "trailing junk")]
    [InlineData("parked/7f2d4e88-1a3b-4c5d-8e9f-0a1b2c3d4e5f/rg/prod", "the D form is not the N form")]
    [InlineData("parked/7f2d4e881a3b4c5d8e9f0a1b2c3d4e5f/rg/PROD", "an upper-case group name")]
    [InlineData("Parked/7f2d4e881a3b4c5d8e9f0a1b2c3d4e5f/rg/prod", "the prefix is matched case-sensitively")]
    public void AForgedParkedRegistryKeyIsRejectedRatherThanReinterpreted(string forged, string why) {
        GrainKeys.TryParse(forged, out _).ShouldBeFalse($"'{forged}' — {why}");
        GrainKeys.Parse(forged).Error!.Code.ShouldBe(ErrorCode.InvalidGrainKey);
    }

    [Fact]
    public void TheParkedRegistryFactoryRefusesAnInjectedResourceGroupName() =>
        // The same defence, and the same corpus, as ResourceGroup's: the one caller-controlled
        // component of both four-segment shapes is the same name, so a hole in either factory is a
        // hole in the pair.
        Should.Throw<ArgumentException>(
            static () => GrainKeys.ParkedResourceRegistry(Subscription, "pr/od"),
            "a resource group name containing '/' must not be constructible into a registry key"
        );

    // ── The expiry sweeper — docs/plan/07 § Azure RBAC, issue #12 ─────────────────────────────
    //
    // ⚠ THE TAIL IS NOW SHARED BY THREE SHAPES RATHER THAN TWO, which is what makes this section a
    // copy of the one above rather than one case appended to it. `sub/{sub}/rg/{name}`,
    // `parked/{sub}/rg/{name}` and `sweep/{sub}/rg/{name}` differ by their first segment alone and
    // carry the SAME payload, so every near-miss the registry has, the sweeper has too — and one
    // more besides: the two of them are the pair a reader is most likely to confuse, because they
    // address the same resource group's soft-delete machinery and only one of them holds the state.
    // A fork that folded them would point a sweep's reminder at the registry grain, which is the
    // activation GrainKeys.ExpirySweeper exists to keep the sweep out of.

    [Fact]
    public void TheExpirySweeperShapeIsSweepSlashSubscriptionSlashRgSlashName() {
        GrainKeys.ExpirySweeper(Subscription, "prod")
            .ShouldBe("sweep/7f2d4e881a3b4c5d8e9f0a1b2c3d4e5f/rg/prod");

        var parsed = GrainKeys.Parse(GrainKeys.ExpirySweeper(Subscription, "prod")).GetValueOrThrow();

        parsed.Kind.ShouldBe(GrainKeyKind.ExpirySweeper);
        parsed.Id.ShouldBe(Subscription);
        parsed.Name.ShouldBe("prod");
        parsed.ToString().ShouldBe(GrainKeys.ExpirySweeper(Subscription, "prod"));
    }

    [Fact]
    public void TheThreeResourceGroupShapesAreThreeKeysWithOnePayload() {
        // The property the fork in ParseFourSegments has to have, over a corpus rather than one
        // triple: for every (subscription, group) the three shapes produce three DIFFERENT strings
        // that decode to three different kinds and to the SAME subscription and name. A parser that
        // folded any pair would satisfy the payload half and fail the kind half, which is why both
        // are asserted.
        foreach (var id in Corpus.ResourceIds(500, 12)) {
            var group = GrainKeys.ResourceGroup(id.SubscriptionId, id.ResourceGroup);
            var parked = GrainKeys.ParkedResourceRegistry(id.SubscriptionId, id.ResourceGroup);
            var sweeper = GrainKeys.ExpirySweeper(id.SubscriptionId, id.ResourceGroup);

            new[] { group, parked, sweeper }.Distinct(StringComparer.Ordinal).Count().ShouldBe(3);

            var decoded = new[] { group, parked, sweeper }
                .Select(static x => GrainKeys.Parse(x).GetValueOrThrow())
                .ToArray();

            decoded.Select(static x => x.Kind)
                .ShouldBe(
                    [GrainKeyKind.ResourceGroup, GrainKeyKind.ParkedResourceRegistry, GrainKeyKind.ExpirySweeper]
                );

            decoded.Select(static x => x.Id).Distinct().ShouldBe([id.SubscriptionId]);
            decoded.Select(static x => x.Name).Distinct(StringComparer.Ordinal).ShouldBe([id.ResourceGroup]);

            GrainKeys.IsTenantQualificationSafe(sweeper).ShouldBeTrue();
        }
    }

    [Fact]
    public void AResourceGroupCalledSweepIsStillNotAnExpirySweeperKey() {
        // `sweep` is a legal resource group name under ResourceNaming, so the near-miss below is
        // constructible by a tenant rather than only by a forger.
        var group = GrainKeys.ResourceGroup(Subscription, "sweep");

        group.ShouldNotBe(GrainKeys.ExpirySweeper(Subscription, "prod"));
        GrainKeys.Parse(group).GetValueOrThrow().Kind.ShouldBe(GrainKeyKind.ResourceGroup);

        // …and the sweeper of the group that is itself called `sweep`, which is the one string a
        // reader is most likely to misread. It is a sweeper key, and its name is `sweep`.
        var sweeperOfSweep = GrainKeys.ExpirySweeper(Subscription, "sweep");
        var decoded = GrainKeys.Parse(sweeperOfSweep).GetValueOrThrow();

        sweeperOfSweep.ShouldBe("sweep/7f2d4e881a3b4c5d8e9f0a1b2c3d4e5f/rg/sweep");
        decoded.Kind.ShouldBe(GrainKeyKind.ExpirySweeper);
        decoded.Name.ShouldBe("sweep");
    }

    [Theory]
    [InlineData("sweep/7f2d4e881a3b4c5d8e9f0a1b2c3d4e5f", "the prefix and a subscription is not the shape")]
    [InlineData("sweep/7f2d4e881a3b4c5d8e9f0a1b2c3d4e5f/rg", "the marker with no name")]
    [InlineData("sweep/7f2d4e881a3b4c5d8e9f0a1b2c3d4e5f/xx/prod", "the wrong marker")]
    [InlineData("sweep/7f2d4e881a3b4c5d8e9f0a1b2c3d4e5f/rg/prod/extra", "trailing junk")]
    [InlineData("sweep/7f2d4e88-1a3b-4c5d-8e9f-0a1b2c3d4e5f/rg/prod", "the D form is not the N form")]
    [InlineData("sweep/7f2d4e881a3b4c5d8e9f0a1b2c3d4e5f/rg/PROD", "an upper-case group name")]
    [InlineData("Sweep/7f2d4e881a3b4c5d8e9f0a1b2c3d4e5f/rg/prod", "the prefix is matched case-sensitively")]
    [InlineData("sweeper/7f2d4e881a3b4c5d8e9f0a1b2c3d4e5f/rg/prod", "a near-miss prefix is not the prefix")]
    public void AForgedExpirySweeperKeyIsRejectedRatherThanReinterpreted(string forged, string why) {
        GrainKeys.TryParse(forged, out _).ShouldBeFalse($"'{forged}' — {why}");
        GrainKeys.Parse(forged).Error!.Code.ShouldBe(ErrorCode.InvalidGrainKey);
    }

    [Fact]
    public void TheExpirySweeperFactoryRefusesAnInjectedResourceGroupName() =>
        // The same defence, and the same reasoning, as the other two four-segment factories': the
        // one caller-controlled component of all three shapes is the same name, so a hole in any
        // one factory is a hole in the set.
        Should.Throw<ArgumentException>(
            static () => GrainKeys.ExpirySweeper(Subscription, "pr/od"),
            "a resource group name containing '/' must not be constructible into a sweeper key"
        );

    // ── The twenty-sixth shape — the management group, docs/plan/06 § The hierarchy (issue #39) ──

    /// <summary>
    ///     <c>mg/{name}</c>: two segments like a subscription key, with a name where the GUID would
    ///     be, and the parser decides by the prefix before it reads the payload.
    /// </summary>
    [Fact]
    public void TheManagementGroupShapeIsKeyedByNameAndRoundTrips() {
        var key = GrainKeys.ManagementGroup("platform");

        key.ShouldBe("mg/platform");

        var decoded = GrainKeys.Parse(key).GetValueOrThrow();
        decoded.Kind.ShouldBe(GrainKeyKind.ManagementGroup);
        decoded.Name.ShouldBe("platform");
        decoded.Id.ShouldBe(Guid.Empty);
        decoded.ToString().ShouldBe(key);

        GrainKeys.IsTenantQualificationSafe(key).ShouldBeTrue();
    }

    /// <summary>
    ///     ⚠ A group may be named with 32 lower-case hex digits — a legal DNS-1123 label — and the
    ///     key is still a group, not a subscription with the wrong prefix; and a name where a
    ///     subscription's GUID belongs is not a key at all. Neither shape can be re-cut into the other.
    /// </summary>
    [Fact]
    public void AHexNamedGroupIsAGroupAndANamedSubscriptionIsNotAKey() {
        var hexName = Subscription.ToString("N", CultureInfo.InvariantCulture);

        var group = GrainKeys.Parse(GrainKeys.ManagementGroup(hexName)).GetValueOrThrow();
        group.Kind.ShouldBe(GrainKeyKind.ManagementGroup);
        group.Name.ShouldBe(hexName);
        group.Id.ShouldBe(Guid.Empty, "a hex-named group decoded as if its name were a GUID");

        GrainKeys.TryParse("sub/platform", out _).ShouldBeFalse();
    }

    [Theory]
    [InlineData("mg", "the prefix alone")]
    [InlineData("mg/", "the prefix with an empty name")]
    [InlineData("mg/platform/extra", "a third segment")]
    [InlineData("mg/PLATFORM", "an upper-case name")]
    [InlineData("mg/-platform", "a leading hyphen")]
    [InlineData("mg/plat_form", "an underscore")]
    [InlineData("MG/platform", "the prefix is matched case-sensitively")]
    [InlineData("mgs/platform", "a near-miss prefix is not the prefix")]
    [InlineData("mi/platform", "the managed-identity prefix with a name is not a GUID")]
    public void AForgedManagementGroupKeyIsRejectedRatherThanReinterpreted(string forged, string why) {
        GrainKeys.TryParse(forged, out _).ShouldBeFalse($"'{forged}' — {why}");
        GrainKeys.Parse(forged).Error!.Code.ShouldBe(ErrorCode.InvalidGrainKey);
    }

    [Fact]
    public void TheManagementGroupFactoryRefusesAnInjectedName() =>
        Should.Throw<ArgumentException>(
            static () => GrainKeys.ManagementGroup("plat/form"),
            "a management group name containing '/' must not be constructible into a key"
        );

    /// <summary>
    ///     ⚠ The count prose on <see cref="GrainKeys" /> is re-derived off the enum, and this is the
    ///     derivation: every member but <see cref="GrainKeyKind.None" /> is a key. The number here
    ///     has to move with the enum AND with the prose — it was twenty-six on #39's branch, and
    ///     twenty-nine on the day it merged beside #90's <see cref="GrainKeyKind.WatchIndex" /> and
    ///     #94's two, which is the drift the prose paragraph describes happening to itself; thirty-one
    ///     since #43's <see cref="GrainKeyKind.DeviceAuthorization" /> and
    ///     <see cref="GrainKeyKind.Invitation" />; thirty-two since #41's
    ///     <see cref="GrainKeyKind.DirectoryIndex" />.
    /// </summary>
    [Fact]
    public void TheClosedSetHasThirtyTwoShapes() =>
        Enum.GetValues<GrainKeyKind>().Count(static x => x != GrainKeyKind.None).ShouldBe(32);
}

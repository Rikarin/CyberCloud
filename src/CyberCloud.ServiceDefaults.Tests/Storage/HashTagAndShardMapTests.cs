using CyberCloud.ServiceDefaults.Storage;
using Npgsql;
using Shouldly;
using System.Globalization;

namespace CyberCloud.ServiceDefaults.Tests.Storage;

/// <summary>
///     The parts of the two tiers that are arithmetic rather than I/O: the Redis Cluster hash tag,
///     the placement function, and the connection string the durable provider is handed.
/// </summary>
/// <remarks>
///     No containers. Everything here is a pure function, and a pure function that needs Docker to be
///     checked is a pure function nobody checks.
/// </remarks>
public sealed class HashTagAndShardMapTests {
    [Fact]
    public void TheSlotFunctionMatchesRedisReferenceVectors() {
        // The three values every CRC16 implementation is checked against, plus the two the Redis
        // Cluster specification itself uses. If this fails, nothing else in this file means
        // anything, so it comes first.
        RedisHashSlot.Of("foo").ShouldBe(12182);
        RedisHashSlot.Of("somekey").ShouldBe(11058);
        RedisHashSlot.Of("123456789").ShouldBe(12739);
    }

    [Fact]
    public void TheKeyLayoutIsExactlyTheOneInThePlan() {
        var map = new StaticShardMapCache(TwoShards());
        var tenant = Guid.Parse("11111111-2222-3333-4444-555555555555");

        var tag = map.HotHashTagFor(tenant.ToString("D", CultureInfo.InvariantCulture));
        var key = TenantHotKeys.Format(tag, "CyberCloud.Tests.Hot", "res/abc");

        // docs/plan/05 § Hot: {cc:t:<tenantId>}:<grainType>:<keyWithinTenant>
        key.ShouldBe("{cc:t:11111111222233334444555555555555}:CyberCloud.Tests.Hot:res/abc");
    }

    [Fact]
    public void EveryKeyOfOneTenantLandsOnOneSlotAndDifferentTenantsDoNot() {
        var map = new StaticShardMapCache(TwoShards());
        var a = map.HotHashTagFor(StorageFixture.Tenant(1).ToString("D", CultureInfo.InvariantCulture));
        var b = map.HotHashTagFor(StorageFixture.Tenant(2).ToString("D", CultureInfo.InvariantCulture));

        var grainTypes = new[] { "Resource", "Session", "Terminal", "Quota", "RateLimit" };

        var slotsOfA = grainTypes
            .SelectMany(type => Enumerable.Range(0, 40).Select(i => TenantHotKeys.Format(a, type, "k/" + i)))
            .Select(RedisHashSlot.Of)
            .Distinct()
            .ToList();

        // 200 keys, one slot. This is the whole point of the hash tag: a tenant's state is
        // one-shard-local, so a multi-key read is one round trip and a tenant delete is one SCAN.
        slotsOfA.Count.ShouldBe(1);

        var slotOfB = RedisHashSlot.Of(TenantHotKeys.Format(b, "Resource", "k/0"));
        slotOfB.ShouldNotBe(slotsOfA[0]);
    }

    [Fact]
    public void TenantsSpreadOverTheSlotSpaceRatherThanClustering() {
        var map = new StaticShardMapCache(TwoShards());

        var slots = Enumerable.Range(0, 500)
            .Select(i => map.HotHashTagFor(StorageFixture.Tenant(i).ToString("D", CultureInfo.InvariantCulture)))
            .Select(tag => RedisHashSlot.Of(TenantHotKeys.Format(tag, "Resource", "k")))
            .Distinct()
            .Count();

        // Not a statistical test — a sanity floor. If the tag were constant, or the CRC were
        // returning zero, this would be 1. 500 tenants over 16 384 slots should collide a handful of
        // times at most.
        slots.ShouldBeGreaterThan(480);
    }

    [Fact]
    public void BracingTheWholeKeyLosesColocationWhichIsWhyItIsWrong() {
        // ⚠ Both of these "work" against a single-node Redis and are wrong on a cluster. This test
        // is what makes the difference visible without one.
        var tag = "cc:t:" + Guid.Empty.ToString("N", CultureInfo.InvariantCulture);
        var correct = Enumerable.Range(0, 50).Select(i => TenantHotKeys.Format(tag, "Resource", "k/" + i));
        var wholeKeyBraced = Enumerable.Range(0, 50).Select(i => "{" + tag + ":Resource:k/" + i + "}");
        var noBraces = Enumerable.Range(0, 50).Select(i => tag + ":Resource:k/" + i);

        correct.Select(RedisHashSlot.Of).Distinct().Count().ShouldBe(1);
        wholeKeyBraced.Select(RedisHashSlot.Of).Distinct().Count().ShouldBeGreaterThan(40);
        noBraces.Select(RedisHashSlot.Of).Distinct().Count().ShouldBeGreaterThan(40);
    }

    [Fact]
    public void EmptyBracesFallBackToHashingTheWholeKey() {
        // Redis' rule, restated so that a future "optimisation" that produces {} cannot pass.
        RedisHashSlot.HashTagOf("{}:a").ShouldBe("{}:a");
        RedisHashSlot.HashTagOf("{t}:a").ShouldBe("t");
        RedisHashSlot.HashTagOf("no-braces").ShouldBe("no-braces");
        RedisHashSlot.HashTagOf("{outer{inner}}").ShouldBe("outer{inner");
    }

    [Fact]
    public void TwoSpellingsOfOneTenantGuidGiveOneSlot() {
        var map = new StaticShardMapCache(TwoShards());
        var id = Guid.NewGuid();

        map.HotHashTagFor(id.ToString("D", CultureInfo.InvariantCulture))
            .ShouldBe(map.HotHashTagFor(id.ToString("N", CultureInfo.InvariantCulture)));

        map.HotHashTagFor(id.ToString("B", CultureInfo.InvariantCulture))
            .ShouldBe(map.HotHashTagFor(id.ToString("N", CultureInfo.InvariantCulture)));
    }

    [Fact]
    public void AHashTagOverrideMovesATenantWithoutChangingTheKeyFormat() {
        var options = TwoShards();
        var whale = StorageFixture.Tenant(7);
        options.Hot.HashTagOverrides[whale.ToString("D", CultureInfo.InvariantCulture)] = "cc:big:whale";

        var map = new StaticShardMapCache(options);
        var tag = map.HotHashTagFor(whale.ToString("D", CultureInfo.InvariantCulture));

        tag.ShouldBe("cc:big:whale");
        TenantHotKeys.Format(tag, "Session", "s/1").ShouldBe("{cc:big:whale}:Session:s/1");
    }

    [Fact]
    public void ATagThatAlreadyCarriesBracesIsRejectedRatherThanNested() {
        // Redis takes the FIRST '{' to the FIRST following '}', so "{{a}b}" tags on "{a" — a value
        // nobody wrote. Cheaper to refuse it.
        Should.Throw<ArgumentException>(() => new TenantHotKeys("t", "{already}"));
    }

    [Fact]
    public void TheNullTenantSentinelRoutesInsteadOfThrowing() {
        // ⚠ This is the docs/plan/05 § Storage provider wiring defect, as a test. Its body starts
        // with Guid.Parse(tenantId); Orleans.Multitenant passes "Null" for every platform grain.
        Should.Throw<FormatException>(() => Guid.Parse("Null"));

        var options = TwoShards();
        options.Durable.NullTenantShard = "durable-01";
        var map = new StaticShardMapCache(options);

        map.DurableShardFor("Null").ShouldBe("durable-01");
        map.HotHashTagFor("Null").ShouldBe("cc:t:Null");
    }

    [Fact]
    public void PlacementIsStableAcrossProcessesAndPinsWin() {
        var options = TwoShards();
        var tenant = StorageFixture.Tenant(3).ToString("D", CultureInfo.InvariantCulture);

        // Not string.GetHashCode(): that is randomised per process, so two silos would place one
        // tenant on two shards and split its rows across two databases with no error anywhere.
        new StaticShardMapCache(options).DurableShardFor(tenant)
            .ShouldBe(new StaticShardMapCache(TwoShards()).DurableShardFor(tenant));

        var unpinned = new StaticShardMapCache(options).DurableShardFor(tenant);
        var other = StorageFixture.AllShards.First(x => !string.Equals(x, unpinned, StringComparison.Ordinal));

        var pinned = TwoShards();
        pinned.Durable.Pins[tenant] = other;
        new StaticShardMapCache(pinned).DurableShardFor(tenant).ShouldBe(other);
    }

    [Fact]
    public void APinToAnUnknownShardIsFatalAtWiringTimeRatherThanSilent() {
        var options = TwoShards();
        options.Durable.Pins[Guid.NewGuid().ToString("D", CultureInfo.InvariantCulture)] = "durable-99";

        Should.Throw<InvalidOperationException>(() => new StaticShardMapCache(options))
            .Message.ShouldContain("durable-99");
    }

    [Fact]
    public void MaxPoolSizeIsAppliedAndOverridesWhateverTheOperatorWrote() {
        var options = TwoShards();
        options.Durable.Shards["durable-00"] = "Host=a;Database=cc;Username=u;Password=p;Maximum Pool Size=200";
        options.Durable.MaxPoolSize = 5;

        var connectionString = new ConfiguredShardConnections(options).Durable("durable-00");
        var parsed = new NpgsqlConnectionStringBuilder(connectionString);

        // docs/plan/05 § Storage provider wiring: 30 silos × 16 shards × 20 = 9 600. The failure
        // mode this guards is not "the setting is missing", it is "the setting is there twice and
        // the wrong one wins".
        parsed.MaxPoolSize.ShouldBe(5);
        connectionString.ShouldNotContain("200", Case.Sensitive);
    }

    [Fact]
    public void EveryTenantOnAShardGetsAByteIdenticalConnectionStringOrThePoolMathIsPerTenant() {
        // ⚠ The subtlest requirement in docs/plan/05 § Storage provider wiring, and one the document
        // does not state: Npgsql pools by connection string, and Orleans.Multitenant builds one
        // storage provider per TENANT. If the string differs per tenant — an application_name
        // carrying the tenant id would do it — then "MaxPoolSize of 5 per silo per shard" silently
        // becomes 5 per silo per shard PER TENANT, and the 9 600 the plan is worried about becomes
        // 9 600 × tenants.
        var options = TwoShards();
        var connections = new ConfiguredShardConnections(options);
        var map = new StaticShardMapCache(options);

        var byShard = Enumerable.Range(0, 200)
            .Select(i => map.DurableShardFor(StorageFixture.Tenant(i).ToString("D", CultureInfo.InvariantCulture)))
            .Select(shard => (Shard: shard, Connection: connections.Durable(shard)))
            .GroupBy(x => x.Shard, StringComparer.Ordinal)
            .ToList();

        byShard.Count.ShouldBe(2, "200 tenants should reach both shards, or this proves nothing.");

        foreach (var shard in byShard) {
            shard.Select(x => x.Connection).Distinct(StringComparer.Ordinal).Count().ShouldBe(1);
        }
    }

    [Fact]
    public void TransactionModePgBouncerTurnsOffTheTwoThingsThatBreakUnderIt() {
        var options = TwoShards();
        options.Durable.PgBouncerTransactionMode = true;

        var parsed = new NpgsqlConnectionStringBuilder(new ConfiguredShardConnections(options).Durable("durable-00"));

        // Auto-prepare is off by DEFAULT in Npgsql 10 (MaxAutoPrepare = 0) — the claim that
        // "Npgsql 10 auto-prepares" is not true of a default connection string. Pinned anyway so a
        // later edit cannot turn it on behind a transaction-mode pooler without failing this test.
        parsed.MaxAutoPrepare.ShouldBe(0);

        // DISCARD ALL on connection return is pointless behind a transaction-mode pooler, which
        // already hands out a clean server connection per transaction, and errors on some versions.
        parsed.NoResetOnClose.ShouldBeTrue();
    }

    [Fact]
    public void NpgsqlDoesNotAutoPrepareByDefaultWhichIsWhatMakesPgBouncerViable() {
        // Recorded as a test because the compatibility argument for transaction-mode pooling rests
        // on it, and it is a property of a dependency rather than of our code — so it should break
        // the build if a future Npgsql changes the default.
        new NpgsqlConnectionStringBuilder("Host=h;Database=d").MaxAutoPrepare.ShouldBe(0);
    }

    [Fact]
    public void AShardThatIsNotInTheTableFailsLoudly() {
        Should.Throw<KeyNotFoundException>(() => new ConfiguredShardConnections(TwoShards()).Durable("durable-42"))
            .Message.ShouldContain("durable-00");
    }

    [Fact]
    public void ASiloWithNoDurableShardsIsRefusedRatherThanStartedEmpty() {
        var options = new CyberCloudStorageOptions();
        options.Hot.ConnectionString = "localhost:6379";

        Should.Throw<InvalidOperationException>(() => new StaticShardMapCache(options));
    }

    static CyberCloudStorageOptions TwoShards() {
        var options = new CyberCloudStorageOptions();
        options.Hot.ConnectionString = "localhost:6379";
        options.Durable.Shards["durable-00"] = "Host=a;Database=cc;Username=u;Password=p";
        options.Durable.Shards["durable-01"] = "Host=b;Database=cc;Username=u;Password=p";
        return options;
    }
}

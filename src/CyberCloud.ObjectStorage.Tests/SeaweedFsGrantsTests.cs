using CyberCloud.Core.Time;
using CyberCloud.ResourceManager.Conformance;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;

namespace CyberCloud.ObjectStorage.Tests;

/// <summary>
///     Buckets and bucket-scoped keys against a real SeaweedFS with its IAM API — what a PostgreSQL
///     server's WAL archive is given.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The assertion that matters is the neighbour's bucket.</b> A key that opened its own
///         bucket would pass a test that only wrote there; a key that opened every bucket would too.
///         <see cref="AKeyWritesItsOwnBucketAndNotItsNeighbours" /> makes two buckets and proves the key
///         for one is refused by the other, then withdraws the key and proves the store stops honouring
///         it. SeaweedFS applies an IAM change through a filer subscription, so the waits are for the
///         store to start honouring a key or to stop — the directions that fail closed — and never for
///         a refusal. A wait for a refusal is a test that passes over the breach it was written for.
///     </para>
///     <para>
///         <c>chrislusf/seaweedfs:3.80</c>, the image <see cref="SeaweedFsRoundTripTests" /> pins, as
///         <c>weed server -s3 -iam</c> and no identities file — the IAM API writes the identities
///         to the filer, and a <c>-s3.config</c> file would take precedence over them (measured; see
///         <see cref="SeaweedFsObjectStoreGrants" />).
///     </para>
/// </remarks>
public sealed class SeaweedFsGrantsTests : IAsyncLifetime {
    static readonly TimeSpan Settle = TimeSpan.FromSeconds(30);

    IContainer container = null!;
    ObjectStorageOptions options = null!;
    SeaweedFsObjectStoreGrants grants = null!;

    public async ValueTask InitializeAsync() {
        var token = TestContext.Current.CancellationToken;

        container = new ContainerBuilder(SeaweedFsRoundTripTests.Image)
            .WithPortBinding(8333, true)
            .WithPortBinding(8111, true)
            // ⚠ No preallocation: the entrypoint's -master.volumePreallocate at 1 GiB a volume costs
            // gigabytes of Docker VM disk per bucket, and this suite makes three. The later flags win.
            .WithCommand(
                "server",
                "-s3",
                "-iam",
                "-dir=/data",
                "-ip.bind=0.0.0.0",
                "-master.volumePreallocate=false",
                "-master.volumeSizeLimitMB=64"
            )
            // ⚠ The IAM port, not the S3 one: weed iam waits for the filer and binds last, about thirty
            // seconds after the S3 gateway on this image.
            .WithWaitStrategy(
                Wait.ForUnixContainer()
                    .UntilHttpRequestIsSucceeded(static x => x.ForPort(8111).ForPath("/").ForStatusCodeMatching(static _ => true))
            )
            .Build();

        await container.StartAsync(token);

        var s3 = $"http://{container.Hostname}:{container.GetMappedPublicPort(8333)}";
        var iam = $"http://{container.Hostname}:{container.GetMappedPublicPort(8111)}";

        var admin = await SeaweedFsObjectStoreGrants.BootstrapAdministratorAsync(
            new HttpClient { Timeout = TimeSpan.FromSeconds(30) },
            new Uri(iam),
            "platform",
            new SystemClock(),
            token
        );

        options = new() {
            Endpoint = s3,
            IamEndpoint = iam,
            DataPlaneEndpoint = "http://objectstore.data-plane.invalid:8333",
            Bucket = "platform",
            AccessKeyId = admin.GetValueOrThrow().AccessKeyId,
            SecretAccessKey = admin.GetValueOrThrow().SecretAccessKey,
            AllowInsecureTransport = true
        };

        grants = new(new HttpClient { Timeout = TimeSpan.FromSeconds(30) }, options, new SystemClock());

        // ⚠ THE INSTALL STEP, AND WHAT THE FIRST FAST-LANE RUN UNDER LOAD FOUND MISSING. The bootstrap
        // is the store's first identity, and until the S3 gateway has loaded it the gateway
        // authenticates nothing — weed/s3api/auth_credentials.go at 3.80 turns authentication on the
        // first time it sees an identity and never off. Under the lane's load that took long enough
        // for a key issued straight after to write into its neighbour's bucket. The installer waits
        // for a stranger to be refused before it hands the store to anything, and so does this.
        await EventuallyAsync(
            async () => (await SeaweedFsObjectStoreGrants.RefusesStrangersAsync(
                    new HttpClient { Timeout = TimeSpan.FromSeconds(30) },
                    new Uri(s3),
                    options.Region,
                    new SystemClock(),
                    token
                )) is { IsSuccess: true } closed
                && closed.GetValueOrThrow(),
            "the store to refuse a key nobody issued, which is the administrator's identity reaching the S3 gateway"
        );

        await EventuallyAsync(async () => (await grants.EnsureBucketAsync("platform", token)).IsSuccess, "the administrator's key to take effect");
    }

    public async ValueTask DisposeAsync() {
        if (container is not null) {
            await container.DisposeAsync();
        }
    }

    [Fact]
    public async Task AKeyWritesItsOwnBucketAndNotItsNeighbours() {
        var token = TestContext.Current.CancellationToken;
        var own = ObjectStoreCredentials.BucketFor("pg", Guid.NewGuid());
        var neighbour = ObjectStoreCredentials.BucketFor("pg", Guid.NewGuid());

        (await grants.EnsureBucketAsync(own, token)).IsSuccess.ShouldBeTrue();
        (await grants.EnsureBucketAsync(neighbour, token)).IsSuccess.ShouldBeTrue();
        (await grants.EnsureBucketAsync(own, token)).IsSuccess.ShouldBeTrue("a second EnsureBucketAsync is the next pass converging");

        var issued = await grants.IssueKeyAsync(own, own, token);
        issued.IsSuccess.ShouldBeTrue(issued.Error?.Message);
        var key = issued.GetValueOrThrow();
        key.ToString().ShouldNotContain(key.SecretAccessKey);

        var mine = Store(own, key);
        var theirs = Store(neighbour, key);

        // ⚠ ONLY THE OWN-BUCKET WRITE IS WAITED FOR, because that is the fail-CLOSED direction: a key
        // the gateway has not loaded yet is refused. #30's first cut also waited for a never-issued key
        // to be refused here, which hid the fail-open this assertion exists to catch; the store is shut
        // before the test starts (InitializeAsync), and from there nothing may be waited out.
        await EventuallyAsync(
            async () => (await mine.PutAsync("wals/000000010000000000000001", "wal"u8.ToArray(), "application/octet-stream", token)).IsSuccess,
            "the issued key to write its own bucket"
        );

        var refused = await theirs.PutAsync("wals/stolen", "x"u8.ToArray(), "application/octet-stream", token);
        refused.IsFailure.ShouldBeTrue("a key scoped to one bucket wrote into another");
        refused.Error!.Message.ShouldContain("403");

        (await theirs.ListAsync("", token)).IsFailure.ShouldBeTrue("a key scoped to one bucket listed another");

        (await grants.RevokeKeyAsync(own, key.AccessKeyId, token)).IsSuccess.ShouldBeTrue();

        await EventuallyAsync(
            async () => (await mine.PutAsync("wals/after", "x"u8.ToArray(), "application/octet-stream", token)).IsFailure,
            "the store to stop honouring a withdrawn key"
        );
    }

    [Fact]
    public async Task NoAnswerDuringABurstOfIamChangesLetsAStrangerOrANeighbourIn() {
        var token = TestContext.Current.CancellationToken;
        var own = ObjectStoreCredentials.BucketFor("pg", Guid.NewGuid());
        var neighbour = ObjectStoreCredentials.BucketFor("pg", Guid.NewGuid());

        (await grants.EnsureBucketAsync(own, token)).IsSuccess.ShouldBeTrue();
        (await grants.EnsureBucketAsync(neighbour, token)).IsSuccess.ShouldBeTrue();

        var key = (await grants.IssueKeyAsync(own, own, token)).GetValueOrThrow();
        var mine = Store(own, key);
        var theirs = Store(neighbour, key);
        var stranger = Store(own, new("NEVERISSUEDKEY000000", "never-issued-secret-never-issued"));

        await EventuallyAsync(
            async () => (await mine.PutAsync("wals/first", "x"u8.ToArray(), "application/octet-stream", token)).IsSuccess,
            "the issued key to write its own bucket"
        );

        // ⚠ THE PROBE `an-iam-change-can-open-the-store-for-a-moment` ASKED FOR. The hypothesis was that
        // every IAM change reloads the gateway's identities through a moment with none, and that the
        // gateway reads "none" as authentication off. So: hammer the two writes that must be refused
        // while the identities change under them — keys issued to other principals and withdrawn — and
        // count every answer that let one through. One is a breach, and this fails on it.
        using var burst = CancellationTokenSource.CreateLinkedTokenSource(token);
        var attempts = 0;
        var breaches = new List<string>();

        var hammer = Task.Run(
            async () => {
                while (!burst.IsCancellationRequested) {
                    attempts++;

                    if ((await theirs.PutAsync($"wals/stolen-{attempts}", "x"u8.ToArray(), "application/octet-stream", burst.Token)).IsSuccess) {
                        breaches.Add($"attempt {attempts}: the key for '{own}' wrote into '{neighbour}'");
                    }

                    if ((await stranger.PutAsync($"wals/stranger-{attempts}", "x"u8.ToArray(), "application/octet-stream", burst.Token)).IsSuccess) {
                        breaches.Add($"attempt {attempts}: a key nobody issued wrote into '{own}'");
                    }
                }
            },
            token
        );

        for (var i = 0; i < 8; i++) {
            var principal = ObjectStoreCredentials.BucketFor("pg", Guid.NewGuid());
            var other = await grants.IssueKeyAsync(principal, principal, token);
            other.IsSuccess.ShouldBeTrue(other.Error?.Message);

            if (i % 2 == 0) {
                (await grants.RevokeKeyAsync(principal, other.GetValueOrThrow().AccessKeyId, token)).IsSuccess.ShouldBeTrue();
            }
        }

        // The last change's reload lands a moment after its call returns; keep asking through it.
        await Task.Delay(TimeSpan.FromSeconds(5), token);
        await burst.CancelAsync();

        try {
            await hammer;
        } catch (OperationCanceledException) {
            // The burst ended mid-request.
        }

        TestContext.Current.TestOutputHelper?.WriteLine($"{attempts} rounds of two refused writes across 8 issues and 4 withdrawals");
        attempts.ShouldBeGreaterThan(8, "the probe asked too few times to have asked during a reload");
        breaches.ShouldBeEmpty();
    }

    [Fact]
    public async Task TheVaultHoldsTheKeyAndASecondPassIssuesNone() {
        var token = TestContext.Current.CancellationToken;
        var vault = new InMemorySecretVault();
        var bucket = ObjectStoreCredentials.BucketFor("pg", Guid.NewGuid());
        const string path = "platform/CyberCloud.DBforPostgreSQL/servers/test/objectstore";

        var first = await ObjectStoreCredentials.EnsureAsync(grants, vault, vault, path, bucket, bucket, token);
        first.IsSuccess.ShouldBeTrue(first.Error?.Message);

        var second = await ObjectStoreCredentials.EnsureAsync(grants, vault, vault, path, bucket, bucket, token);
        second.GetValueOrThrow().ShouldBe(first.GetValueOrThrow(), "the second pass read the vault's key back rather than issuing another");

        (await vault.ResolveAsync(new() { Path = path, Field = ObjectStoreCredentials.SecretAccessKeyField }, token))
            .GetValueOrThrow()
            .ShouldBe(first.GetValueOrThrow().SecretAccessKey);

        var store = Store(bucket, first.GetValueOrThrow());
        await EventuallyAsync(
            async () => (await store.PutAsync("base/backup.info", "x"u8.ToArray(), "text/plain", token)).IsSuccess,
            "the vault-held key to write its bucket"
        );
    }

    S3ObjectStore Store(string bucket, ObjectStoreKey key) =>
        new(
            new HttpClient { Timeout = TimeSpan.FromSeconds(30) },
            new() {
                Endpoint = options.Endpoint,
                Bucket = bucket,
                AccessKeyId = key.AccessKeyId,
                SecretAccessKey = key.SecretAccessKey,
                AllowInsecureTransport = true
            },
            new SystemClock()
        );

    static async Task EventuallyAsync(Func<Task<bool>> condition, string what) {
        var deadline = DateTimeOffset.UtcNow + Settle;

        while (DateTimeOffset.UtcNow < deadline) {
            if (await condition()) {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500), TestContext.Current.CancellationToken);
        }

        throw new ShouldAssertException($"waited {Settle.TotalSeconds:F0} seconds for {what}, and it did not happen");
    }
}

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
///         it. SeaweedFS applies an IAM change through a filer subscription, so each of those waits for
///         the store to agree rather than asserting on the first answer.
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

        // ⚠ SETTLED MEANS TWO THINGS, AND THE SECOND IS WHAT THE FIRST RUN UNDER LOAD FOUND. The key
        // writes its own bucket, AND a key the store never issued is refused. In the full Fast lane —
        // seventy suites and their containers on one host — the neighbour write below once SUCCEEDED
        // right after the own-bucket write first did, and alone it never has: the S3 gateway reloads
        // its identities from the filer on each IAM change, and a reload caught between writes serves
        // a moment with no identities, which SeaweedFS treats as authentication off. Waiting for a
        // bogus key to be refused is waiting for that moment to pass; the hazard itself is recorded in
        // charts/managed/postgres/conformance.yaml § owed, `an-iam-change-can-open-the-store-for-a-moment`.
        var bogus = Store(own, new("NEVERISSUEDKEY000000", "never-issued-secret-never-issued"));

        await EventuallyAsync(
            async () =>
                (await mine.PutAsync("wals/000000010000000000000001", "wal"u8.ToArray(), "application/octet-stream", token)).IsSuccess
                && (await bogus.PutAsync("wals/bogus", "x"u8.ToArray(), "application/octet-stream", token)).IsFailure,
            "the issued key to write its own bucket while a key nobody issued is refused"
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

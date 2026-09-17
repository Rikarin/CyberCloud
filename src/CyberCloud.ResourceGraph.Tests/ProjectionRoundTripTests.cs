using CyberCloud.ResourceGraph.Tests.Infrastructure;
using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;
using System.Globalization;

namespace CyberCloud.ResourceGraph.Tests;

/// <summary>
///     Sink → JetStream → the silo's projector → ClickHouse, against the real servers, and back out
///     through a reader. docs/plan/08 § The resource-graph projection.
/// </summary>
[Collection(ProjectionSuite.Name)]
public sealed class ProjectionRoundTripTests(ProjectionFixture fixture) {
    static string N(Guid id) => id.ToString("N", CultureInfo.InvariantCulture);

    [Fact]
    public async Task ACreatedEventBecomesTheRowWithItsColumnsAndItsReaders() {
        var token = TestContext.Current.CancellationToken;
        var resourceId = Guid.NewGuid();
        var group = Guid.NewGuid();

        // The grants a real write leaves behind: the resource hangs off its group (step 8), Alice
        // owns the group, and the engineering group reads the resource directly. Bob has nothing.
        await fixture.GrantAsync(ProjectionFixture.Tenant, $"resource:{N(resourceId)}#parent@resourceGroup:{N(group)}");
        await fixture.GrantAsync(ProjectionFixture.Tenant, $"resourceGroup:{N(group)}#owner@user:alice");
        await fixture.GrantAsync(ProjectionFixture.Tenant, $"resource:{N(resourceId)}#reader@group:eng#member");

        var published = await fixture.Sink.PublishAsync(ProjectionFixture.Created(resourceId, "first"), token);
        published.IsSuccess.ShouldBeTrue(published.Error?.Message);

        var row = await fixture.WaitForVersionAsync(ProjectionFixture.Tenant, resourceId, 1);

        // docs/plan/08's columns, as the event carried them.
        row.TenantId.ShouldBe(ProjectionFixture.Tenant);
        row.SubscriptionId.ShouldBe(ProjectionFixture.Subscription);
        row.ResourceGroup.ShouldBe("prod");
        row.Provider.ShouldBe("CyberCloud.Testing");
        row.Type.ShouldBe("widgets");
        row.Name.ShouldBe("first");
        row.ApiVersion.ShouldBe("2026-08-01");
        row.ProvisioningState.ShouldBe("Creating");
        row.Location.ShouldBe("eu-central");
        row.ClusterId.ShouldBe(Guid.Empty);
        row.Tags.ShouldBe(new Dictionary<string, string> { ["env"] = "test" });
        row.CreatedAt.ShouldBe(new DateTimeOffset(2026, 9, 17, 10, 0, 0, TimeSpan.Zero));
        row.DesiredHash.ShouldBe("sha256:0");
        row.Version.ShouldBe(1);
        row.Change.ShouldBe("Created");
        row.IsDeleted.ShouldBe((byte)0);
        row.ProjectedAt.ShouldBeGreaterThan(row.CreatedAt);

        // ⚠ THE ACCESS COLUMN, FILLED BY THE ENGINE. Alice's ownership is INHERITED through the
        // parent edge and has no tuple on the resource; the group's grant is direct and stays a
        // userset. Bob, who was granted nothing, is not in it — and that absence is the assertion a
        // list filter depends on.
        row.Access.ShouldBe(["group:eng#member", "user:alice"], "sorted, so two projections of one state compare equal");
    }

    [Fact]
    public async Task AReplayAndAReorderedEventAreDroppedAndTheTableHoldsTheLatestVersion() {
        var token = TestContext.Current.CancellationToken;
        var resourceId = Guid.NewGuid();
        var v1 = ProjectionFixture.Created(resourceId, "replayed");
        var v2 = v1 with { Change = ResourceChangeKind.StateChanged, ProvisioningState = ProvisioningState.Succeeded, Version = 2 };

        (await fixture.Sink.PublishAsync(v1, token)).IsSuccess.ShouldBeTrue();
        (await fixture.Sink.PublishAsync(v2, token)).IsSuccess.ShouldBeTrue();
        var latest = await fixture.WaitForVersionAsync(ProjectionFixture.Tenant, resourceId, 2);
        latest.ProvisioningState.ShouldBe("Succeeded");

        // ⚠ THE REPLAY GOES AROUND THE SINK'S DE-DUPLICATION. The sink stamps `{id}.{version}` as
        // the message id, so re-publishing v1 through it would be swallowed by JetStream before
        // any projector saw it — which is the first half of idempotency and is asserted below. This
        // is the second half: the same bytes arriving with a FRESH message id, as a restore that
        // re-reads an older stream or a consumer whose ack was lost would deliver them.
        foreach (var replayed in new[] { v1, v2, v1 }) {
            var acknowledged = await fixture.JetStream.PublishAsync(
                replayed.Subject,
                ResourceChangedJson.Encode(replayed),
                opts: new NatsJSPubOpts { MsgId = Guid.NewGuid().ToString("N") },
                cancellationToken: token
            );
            acknowledged.EnsureSuccess();
            acknowledged.Duplicate.ShouldBeFalse("a fresh message id is a new message to JetStream");
        }

        // Wait for the consumer to have drained them: pending goes to zero on the durable.
        await WaitUntilDrainedAsync(token);

        var after = (await fixture.Reader.ReadAsync(ProjectionFixture.Tenant, resourceId, token)).GetValueOrThrow().Row.ShouldNotBeNull();
        after.Version.ShouldBe(2, "a lower version does not overwrite a higher one");
        after.ProvisioningState.ShouldBe("Succeeded");

        // ⚠ THE READ ABOVE IS THE ENGINE'S PROMISE AND THIS IS THE PROJECTOR'S. ReplacingMergeTree
        // collapses a replayed row at merge time and FINAL hides it before that, so the row read
        // back is version 2 whether or not anything was written — a physical row count was tried
        // as the assertion and a background merge made it pass with the check disabled. What the
        // stream cannot show is WHY nothing was written; the projector, driven directly, can.
        (await fixture.SiloProjector.ProjectAsync(v1, token)).GetValueOrThrow().ShouldBe(ProjectionOutcome.Dropped, "a replayed version is dropped before the insert");
        (await fixture.SiloProjector.ProjectAsync(v2, token)).GetValueOrThrow().ShouldBe(ProjectionOutcome.Dropped, "the current version is dropped too");
        (await fixture.SiloProjector.ProjectAsync(v2 with { Version = 3 }, token)).GetValueOrThrow().ShouldBe(ProjectionOutcome.Applied);
    }

    [Fact]
    public async Task ARetriedPublishIsOneMessageOnTheStream() {
        var token = TestContext.Current.CancellationToken;
        var resourceId = Guid.NewGuid();
        var change = ProjectionFixture.Created(resourceId, "retried");

        var stream = await fixture.JetStream.GetStreamAsync(fixture.Options.Stream, cancellationToken: token);
        var before = stream.Info.State.Messages;

        // The gateway publishing once, and once more because it did not see the first ack.
        (await fixture.Sink.PublishAsync(change, token)).IsSuccess.ShouldBeTrue();
        (await fixture.Sink.PublishAsync(change, token)).IsSuccess.ShouldBeTrue("a duplicate is a success — the event is on the stream");

        await stream.RefreshAsync(token);
        stream.Info.State.Messages.ShouldBe(before + 1, "JetStream's duplicate window folded the second publish into the first");

        await fixture.WaitForVersionAsync(ProjectionFixture.Tenant, resourceId, 1);
    }

    [Fact]
    public async Task ADeletedEventTombstonesTheRowAndEmptiesItsReaders() {
        var token = TestContext.Current.CancellationToken;
        var resourceId = Guid.NewGuid();
        var group = Guid.NewGuid();

        await fixture.GrantAsync(ProjectionFixture.Tenant, $"resource:{N(resourceId)}#parent@resourceGroup:{N(group)}");
        await fixture.GrantAsync(ProjectionFixture.Tenant, $"resourceGroup:{N(group)}#reader@user:carol");

        var created = ProjectionFixture.Created(resourceId, "doomed");
        (await fixture.Sink.PublishAsync(created, token)).IsSuccess.ShouldBeTrue();
        (await fixture.WaitForVersionAsync(ProjectionFixture.Tenant, resourceId, 1)).Access.ShouldBe(["user:carol"]);

        var deleted = created with { Change = ResourceChangeKind.Deleted, ProvisioningState = ProvisioningState.Deleting, Version = 5 };
        (await fixture.Sink.PublishAsync(deleted, token)).IsSuccess.ShouldBeTrue();

        var tombstone = await fixture.WaitForVersionAsync(ProjectionFixture.Tenant, resourceId, 5);

        tombstone.IsDeleted.ShouldBe((byte)1, "the row stays and says it is gone — a DELETE would be a mutation");
        tombstone.Change.ShouldBe("Deleted");
        tombstone.Access.ShouldBeEmpty("a resource that is gone is a resource nobody lists");
    }

    [Fact]
    public async Task ABodyWhoseTenantDisagreesWithItsSubjectIsTerminatedNotProjected() {
        var token = TestContext.Current.CancellationToken;
        var resourceId = Guid.NewGuid();
        var otherTenant = Guid.Parse("22222222-2222-4222-8222-222222222222");

        // A forged message: the subject says one tenant, the body another. The sink cannot build
        // this — the subject comes from the body — so it is published raw.
        var forged = ProjectionFixture.Created(resourceId, "forged") with { TenantId = otherTenant };
        var subjectOfTenant = ProjectionFixture.Created(resourceId, "forged").Subject;

        var acknowledged = await fixture.JetStream.PublishAsync(subjectOfTenant, ResourceChangedJson.Encode(forged), cancellationToken: token);
        acknowledged.EnsureSuccess();

        await WaitUntilDrainedAsync(token);

        (await fixture.Reader.ReadAsync(otherTenant, resourceId, token)).GetValueOrThrow().Found.ShouldBeFalse("the body's tenant got no row");
        (await fixture.Reader.ReadAsync(ProjectionFixture.Tenant, resourceId, token)).GetValueOrThrow().Found.ShouldBeFalse("and neither did the subject's");
    }

    [Fact]
    public async Task AnEventNamingNoTenantOrNoResourceIsRefusedByTheProjectorItself() {
        var token = TestContext.Current.CancellationToken;

        var refused = await fixture.SiloProjector.ProjectAsync(ProjectionFixture.Created(Guid.Empty, "nobody"), token);

        refused.IsFailure.ShouldBeTrue();
        refused.Error!.Code.ShouldBe(ErrorCode.InvalidRequestBody);
    }

    [Fact]
    public async Task ASinkPointedAtNothingRefusesWithinItsCeilingRatherThanHoldingTheWrite() {
        // ⚠ The connection retries its first connect forever and buffers a publish while
        // disconnected — right for the event, wrong for the PUT waiting on it. The sink's ceiling is
        // what turns a NATS outage into a warning on the write path instead of a hung request.
        var token = TestContext.Current.CancellationToken;
        var nowhere = new ResourceGraphOptions { NatsUrl = "nats://127.0.0.1:1", PublishTimeout = TimeSpan.FromSeconds(2) };
        await using var sink = new NatsResourceChangedSink(nowhere, Microsoft.Extensions.Logging.Abstractions.NullLogger<NatsResourceChangedSink>.Instance);

        var started = DateTimeOffset.UtcNow;
        var published = await sink.PublishAsync(ProjectionFixture.Created(Guid.NewGuid(), "unreachable"), token);

        published.IsFailure.ShouldBeTrue("a Result, not a throw — the write path logs it and stands");
        published.Error!.Message.ShouldContain("127.0.0.1:1");
        (DateTimeOffset.UtcNow - started).ShouldBeLessThan(TimeSpan.FromSeconds(15), "bounded by the ceiling, not by the client's retry");
    }

    async Task WaitUntilDrainedAsync(CancellationToken token) {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);

        while (DateTimeOffset.UtcNow < deadline) {
            var consumer = await fixture.JetStream.GetConsumerAsync(fixture.Options.Stream, fixture.Options.Consumer, token);

            if (consumer.Info.NumPending == 0 && consumer.Info.NumAckPending == 0) {
                // One more beat, so the ack the consumer sent after its last write has landed.
                await Task.Delay(300, token);
                return;
            }

            await Task.Delay(200, token);
        }

        throw new TimeoutException("The projector did not drain the consumer within 30s.");
    }
}

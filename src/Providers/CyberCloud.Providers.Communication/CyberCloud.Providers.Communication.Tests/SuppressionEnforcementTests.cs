using System.Text.Json;

namespace CyberCloud.Providers.Communication.Tests;

/// <summary>
///     The property this family exists to keep:
///     <b>
///         a suppressed address is never handed to a
///         carrier
///     </b> — for the tenant's own sends, and for the platform's, which travel the same seam.
/// </summary>
/// <remarks>
///     <para>
///         docs/plan/17 § The parts that are actually the work:
///         <i>
///             "Bounces, complaints, opt-outs —
///             per tenant, honoured before dispatch. Ignoring a complaint is how a sending domain gets
///             blocked."
///         </i> And issue #33's warning that the platform's own transactional mail fails
///         with it. Every test here drives a real send — <see cref="IMessageSender" />, the seam
///         <c>CyberCloud.Identity.Seams.CommunicationOtpDelivery</c> holds for every OTP — against
///         a real <c>MessageGrain</c>, with an in-memory carrier that counts its calls. The
///         assertion is always the same shape: refused, and the carrier's count did not move.
///     </para>
///     <para>
///         ⚠
///         <b>
///             The platform's transactional path is this path, and that is why one suite pins
///             both.
///         </b> An OTP is <c>IOtpDeliverySeam</c> → <c>CommunicationOtpDelivery</c> →
///         <see cref="IMessageSender" /> → <c>MessageGrain.DispatchAsync</c>, whose suppression check
///         runs before <c>IChannelProviderRegistry.Resolve</c> ever names a carrier. The platform's
///         own service is a <c>services</c> resource like any tenant's — <c>SiloIdentityOptions</c>
///         names it — so its list is the same grain <c>services/suppressions</c> writes to. There is
///         no second send path to check. ⚠ This suite drives the tenant's half — <see cref="IMessageSender" />
///         directly and the <c>send</c> action over it — and does not construct
///         <c>CommunicationOtpDelivery</c>; the platform's half is pinned from the other end of the
///         seam by
///         <c>CyberCloud.Identity.Tests.OtpDeliveryTests.ACodeForASuppressedDestinationIsRefusedBeforeTheCarrier</c>,
///         which sends a one-time code to a suppressed address through the real adapter.
///     </para>
///     <para>
///         ⚠ <b>Sabotage-tested.</b> With the suppression check in <c>MessageGrain.DispatchAsync</c>
///         moved below the dispatch, <see cref="ATenantsOwnSendToASuppressedAddressIsRefusedBeforeAnyCarrier" />
///         went red on the carrier count; with the delete path releasing regardless of reason,
///         <see cref="DeletingTheResourceLeavesAComplaintInPlaceAndTheSendStillRefuses" /> went red
///         on the second send. Both were restored before this suite was committed. The owner rule
///         the review of #33 added was tested the same way: with the reconciler's conflict check
///         disabled, <see cref="ASecondResourceForTheSameAddressIsRefusedAndTheFirstsDeleteReleasesOnlyItsOwn" />
///         went red on the refusal; with the delete's owner check disabled, on the send after the
///         second resource's delete. Both restored.
///     </para>
/// </remarks>
[Collection(CommunicationSilo.Name)]
public sealed class SuppressionEnforcementTests(CommunicationTestCluster cluster) {
    [Fact]
    public async Task ATenantsOwnSendToASuppressedAddressIsRefusedBeforeAnyCarrier() {
        Carriers.Reset();

        var service = await cluster.ConvergedServiceAsync("own-sends");
        await cluster.ConvergedEmailChannelAsync("own-sends");

        var block = CommunicationTestCluster.Child(CommunicationSuppressions.Type, "own-sends", "blocked");
        var converged = await cluster.ReconcileAsync(
            block,
            CommunicationSuppressions.Body(destination: "Blocked@Example.com")
        );
        converged.IsConverged.ShouldBeTrue(converged.ToString());

        // The address in a different spelling than the resource used — normalized before it is
        // stored and before it is checked, so a case difference is not a way round the list.
        var refused = await cluster.Sender.SendAsync(
            CommunicationTestCluster.Tenant,
            CommunicationTestCluster.Send(service, "blocked@example.com", "own-1"),
            CommunicationTestCluster.Ct
        );

        refused.IsFailure.ShouldBeTrue("a send to a suppressed address must be refused, not sent");
        refused.Error!.Code.ShouldBe(ErrorCode.PolicyViolation);
        refused.Error.Message.ShouldContain("suppression list");

        // ⚠ THE ASSERTION THAT MATTERS. A refusal that came back after the carrier was called would
        // pass the two lines above and be exactly the failure docs/plan/17 describes.
        Carriers.Email.Calls.ShouldBe(0, "the carrier was called for a suppressed address");
        Carriers.Email.Sent.ShouldBeEmpty();

        // And an address that is not on the list goes through the same channel, so the refusal
        // above was the list and not a broken channel.
        var sent = await cluster.Sender.SendAsync(
            CommunicationTestCluster.Tenant,
            CommunicationTestCluster.Send(service, "welcome@example.com", "own-2"),
            CommunicationTestCluster.Ct
        );

        sent.IsSuccess.ShouldBeTrue(sent.Error?.Message);
        sent.GetValueOrThrow().Status.ShouldBe(MessageStatus.Dispatched);
        Carriers.Email.Calls.ShouldBe(1);
    }

    [Fact]
    public async Task TheSendActionRefusesTheSameWayAsADirectSend() {
        Carriers.Reset();

        var service = await cluster.ConvergedServiceAsync("send-action");
        await cluster.ConvergedEmailChannelAsync("send-action");

        var block = CommunicationTestCluster.Child(CommunicationSuppressions.Type, "send-action", "opted-out");
        (await cluster.ReconcileAsync(block, CommunicationSuppressions.Body(destination: "user@example.com")))
            .IsConverged.ShouldBeTrue();

        // The tenant-facing surface: POST …/send, over the same IMessageSender the test above drove
        // directly — and the same one identity's OTP adapter holds, which is that adapter's suite
        // to pin rather than this one's.
        var handler = new ServiceSendHandler(cluster.Sender);

        using var body = JsonDocument.Parse(
            """{"channel":"email","to":"user@example.com","idempotencyKey":"otp-sign-in-1","body":"Your code is 482913."}"""
        );

        var refused = await handler.InvokeAsync(
            ActionContextFor(service, body.RootElement),
            CommunicationTestCluster.Ct
        );

        refused.IsFailure.ShouldBeTrue("the send action returned a body for a suppressed address");
        refused.Error!.Code.ShouldBe(ErrorCode.PolicyViolation);
        Carriers.Email.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task AComplaintIsNeverDowngradedByAResourceThatNamesTheSameAddress() {
        var service = await cluster.ConvergedServiceAsync("complaints");
        var serviceId = CommunicationServices.ServiceIdOf(service);

        // The carrier said the recipient complained. Arrives through the receipt path in production;
        // written directly here because the property under test is the reconciler's, not the router's.
        (await cluster.Plane.SuppressAsync(
                CommunicationTestCluster.Tenant,
                serviceId,
                ChannelKind.Email,
                "angry@example.com",
                SuppressionReason.Complaint,
                "marked as spam",
                Guid.Empty,
                CommunicationTestCluster.Ct
            )).IsSuccess.ShouldBeTrue();

        var resource = CommunicationTestCluster.Child(CommunicationSuppressions.Type, "complaints", "angry");
        var pass = await cluster.ReconcileAsync(
            resource,
            CommunicationSuppressions.Body(destination: "angry@example.com", note: "tenant's note")
        );

        pass.IsConverged.ShouldBeTrue("the address is suppressed, which is all the body asked for");

        var held = await cluster.Plane.CheckSuppressionAsync(
            CommunicationTestCluster.Tenant,
            serviceId,
            ChannelKind.Email,
            "angry@example.com",
            CommunicationTestCluster.Ct
        );
        held.GetValueOrThrow().Entry!
            .Reason.ShouldBe(
                SuppressionReason.Complaint,
                "the reconcile pass turned the recipient's complaint into the tenant's block"
            );
        held.GetValueOrThrow().Entry!
            .Note.ShouldBe("marked as spam", "the reconcile pass overwrote the carrier's words with the tenant's");
    }

    [Fact]
    public async Task DeletingTheResourceLeavesAComplaintInPlaceAndTheSendStillRefuses() {
        Carriers.Reset();

        var service = await cluster.ConvergedServiceAsync("complaint-survives");
        await cluster.ConvergedEmailChannelAsync("complaint-survives");
        var serviceId = CommunicationServices.ServiceIdOf(service);

        // A manual block first, placed by the tenant's own resource…
        var resource = CommunicationTestCluster.Child(CommunicationSuppressions.Type, "complaint-survives", "someone");
        var body = CommunicationSuppressions.Body(destination: "someone@example.com");
        (await cluster.ReconcileAsync(resource, body)).IsConverged.ShouldBeTrue();

        // …then the recipient complains, which the grain records over the manual block.
        (await cluster.Plane.SuppressAsync(
                CommunicationTestCluster.Tenant,
                serviceId,
                ChannelKind.Email,
                "someone@example.com",
                SuppressionReason.Complaint,
                "marked as spam",
                Guid.Empty,
                CommunicationTestCluster.Ct
            )).IsSuccess.ShouldBeTrue();

        // The tenant deletes their resource. The delete SUCCEEDS — a resource stuck in Deleting
        // protects nothing the grain is not already protecting — and the complaint stands.
        var deleted = await cluster.DeleteAsync(resource, body);
        deleted.IsConverged.ShouldBeTrue(deleted.ToString());

        var refused = await cluster.Sender.SendAsync(
            CommunicationTestCluster.Tenant,
            CommunicationTestCluster.Send(service, "someone@example.com", "after-delete"),
            CommunicationTestCluster.Ct
        );

        refused.IsFailure.ShouldBeTrue("deleting the tenant's resource released the recipient's complaint");
        refused.Error!.Code.ShouldBe(ErrorCode.PolicyViolation);
        Carriers.Email.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task DeletingTheResourceReleasesAManualBlockAndTheSendGoesThrough() {
        Carriers.Reset();

        var service = await cluster.ConvergedServiceAsync("release");
        await cluster.ConvergedEmailChannelAsync("release");

        var resource = CommunicationTestCluster.Child(CommunicationSuppressions.Type, "release", "typo");
        var body = CommunicationSuppressions.Body(destination: "typo@example.com");
        (await cluster.ReconcileAsync(resource, body)).IsConverged.ShouldBeTrue();

        (await cluster.Sender.SendAsync(
                CommunicationTestCluster.Tenant,
                CommunicationTestCluster.Send(service, "typo@example.com", "while-blocked"),
                CommunicationTestCluster.Ct
            ))
            .IsFailure.ShouldBeTrue();

        (await cluster.DeleteAsync(resource, body)).IsConverged.ShouldBeTrue();

        // ⚠ A refusal is re-attemptable and a NEW key is used anyway: the point is the list, not the
        // idempotency record.
        var sent = await cluster.Sender.SendAsync(
            CommunicationTestCluster.Tenant,
            CommunicationTestCluster.Send(service, "typo@example.com", "after-release"),
            CommunicationTestCluster.Ct
        );

        sent.IsSuccess.ShouldBeTrue(sent.Error?.Message);
        Carriers.Email.Calls.ShouldBe(1);
    }

    [Fact]
    public async Task ASecondResourceForTheSameAddressIsRefusedAndTheFirstsDeleteReleasesOnlyItsOwn() {
        Carriers.Reset();

        var service = await cluster.ConvergedServiceAsync("one-block");
        await cluster.ConvergedEmailChannelAsync("one-block");
        var serviceId = CommunicationServices.ServiceIdOf(service);

        // Two resources, one address. The first converges and owns the block…
        var first = CommunicationTestCluster.Child(CommunicationSuppressions.Type, "one-block", "first");
        var body = CommunicationSuppressions.Body(destination: "shared@example.com", note: "first's note");
        (await cluster.ReconcileAsync(first, body)).IsConverged.ShouldBeTrue();

        var held = await cluster.Plane.CheckSuppressionAsync(
            CommunicationTestCluster.Tenant,
            serviceId,
            ChannelKind.Email,
            "shared@example.com",
            CommunicationTestCluster.Ct
        );
        held.GetValueOrThrow().Entry!
            .OwnerResourceId.ShouldBe(first.Id, "the entry does not name the resource that wrote it");

        // …and the second is refused by name, whether its note is the first's or its own. Before
        // the owner existed the identical note read as converged onto the first's entry.
        var second = CommunicationTestCluster.Child(CommunicationSuppressions.Type, "one-block", "second");

        foreach (var note in new[] { "first's note", "second's note" }) {
            var refused = await cluster.ReconcileAsync(
                second,
                CommunicationSuppressions.Body(destination: "Shared@Example.com", note: note)
            );

            refused.Kind.ShouldBe(
                ReconcileOutcomeKind.Failed,
                $"a second resource with note '{note}' was not refused: {refused}"
            );
            refused.Error!.Code.ShouldBe(ErrorCode.Conflict);
            refused.Error.Message.ShouldContain(
                first.Id.D(),
                customMessage: "the refusal does not name the resource that holds the address"
            );
        }

        // The second's delete touches nothing: the block is not its to release.
        (await cluster.DeleteAsync(
                second,
                CommunicationSuppressions.Body(destination: "shared@example.com", note: "second's note")
            ))
            .IsConverged.ShouldBeTrue();

        (await cluster.Sender.SendAsync(
                CommunicationTestCluster.Tenant,
                CommunicationTestCluster.Send(service, "shared@example.com", "while-owned"),
                CommunicationTestCluster.Ct
            ))
            .IsFailure.ShouldBeTrue("the delete of a resource that never owned the block released it");
        Carriers.Email.Calls.ShouldBe(0);

        // The first's delete releases its own, and the address is sendable — with no resource left
        // saying otherwise.
        (await cluster.DeleteAsync(first, body)).IsConverged.ShouldBeTrue();

        var sent = await cluster.Sender.SendAsync(
            CommunicationTestCluster.Tenant,
            CommunicationTestCluster.Send(service, "shared@example.com", "after-owner-gone"),
            CommunicationTestCluster.Ct
        );
        sent.IsSuccess.ShouldBeTrue(sent.Error?.Message);
        Carriers.Email.Calls.ShouldBe(1);
    }

    [Fact]
    public async Task AnUnownedManualBlockIsAdoptedByTheFirstResourceThatNamesIt() {
        var service = await cluster.ConvergedServiceAsync("adoption");
        var serviceId = CommunicationServices.ServiceIdOf(service);

        // An entry from before SuppressionEntry.OwnerResourceId existed reads back with Guid.Empty;
        // this is that entry, written the way the field's absence would have left it.
        (await cluster.Plane.SuppressAsync(
                CommunicationTestCluster.Tenant,
                serviceId,
                ChannelKind.Email,
                "legacy@example.com",
                SuppressionReason.ManualBlock,
                "written before the owner existed",
                Guid.Empty,
                CommunicationTestCluster.Ct
            )).IsSuccess.ShouldBeTrue();

        var resource = CommunicationTestCluster.Child(CommunicationSuppressions.Type, "adoption", "adopter");
        (await cluster.ReconcileAsync(
                resource,
                CommunicationSuppressions.Body(destination: "legacy@example.com", note: "adopted")
            )).IsConverged.ShouldBeTrue();

        var held = await cluster.Plane.CheckSuppressionAsync(
            CommunicationTestCluster.Tenant,
            serviceId,
            ChannelKind.Email,
            "legacy@example.com",
            CommunicationTestCluster.Ct
        );
        held.GetValueOrThrow().Entry!.OwnerResourceId.ShouldBe(resource.Id, "the pass did not adopt the unowned entry");
        held.GetValueOrThrow().Entry!.Note.ShouldBe("adopted");
    }

    [Fact]
    public async Task AHardBounceReceiptFeedsTheListAndTheNextSendIsRefused() {
        Carriers.Reset();

        var service = await cluster.ConvergedServiceAsync("bounces");
        await cluster.ConvergedEmailChannelAsync("bounces");
        var serviceId = CommunicationServices.ServiceIdOf(service);

        var first = await cluster.Sender.SendAsync(
            CommunicationTestCluster.Tenant,
            CommunicationTestCluster.Send(service, "gone@example.com", "bounce-1"),
            CommunicationTestCluster.Ct
        );
        first.IsSuccess.ShouldBeTrue(first.Error?.Message);
        Carriers.Email.Calls.ShouldBe(1);

        // The carrier's receipt: permanently undeliverable. docs/plan/17 § The parts that are
        // actually the work — a bounce "must not be lost" by the suppression list. ⚠ Handed to the
        // router in-process: no HTTP ingress exists for a carrier to reach IWebhookRouter through,
        // which charts/bundle/bundle.yaml § owed carries as communication-receipts-have-no-ingress.
        // What this pins is everything from the router down.
        var handled = await cluster.Router.HandleReceiptAsync(
            CommunicationTestCluster.Tenant,
            serviceId,
            new() {
                ProviderMessageId = first.GetValueOrThrow().ProviderMessageId,
                Status = MessageStatus.Failed,
                ProviderStatus = "550 5.1.1",
                Detail = "user unknown",
                OccurredAt = TestClock.Start.AddMinutes(1),
                Suppresses = SuppressionReason.HardBounce
            },
            CommunicationTestCluster.Ct
        );

        handled.GetValueOrThrow().ShouldBeTrue("the receipt found no message to attach to");

        // Queryable per send: the status action carries the receipt.
        using var status = JsonDocument.Parse("""{"idempotencyKey":"bounce-1"}""");
        var read = await new ServiceStatusHandler(cluster.Sender).InvokeAsync(
            ActionContextFor(service, status.RootElement),
            CommunicationTestCluster.Ct
        );

        read.IsSuccess.ShouldBeTrue(read.Error?.Message);
        using var message = JsonDocument.Parse(read.GetValueOrThrow());
        message.RootElement.GetProperty("status").GetString().ShouldBe("failed");
        message.RootElement.GetProperty("receiptCount").GetInt32().ShouldBe(1);
        message.RootElement.GetProperty("receipts")[0].GetString()!.ShouldContain("550 5.1.1: user unknown");
        CommunicationServices.MessageResponse.Validate(message.RootElement)
            .IsSuccess.ShouldBeTrue("the status body does not match the published response shape");

        // And the bounce fed the list: the next send never reaches the carrier.
        var refused = await cluster.Sender.SendAsync(
            CommunicationTestCluster.Tenant,
            CommunicationTestCluster.Send(service, "gone@example.com", "bounce-2"),
            CommunicationTestCluster.Ct
        );

        refused.IsFailure.ShouldBeTrue("a hard bounce did not suppress the address");
        refused.Error!.Code.ShouldBe(ErrorCode.PolicyViolation);
        Carriers.Email.Calls.ShouldBe(1);

        // The tenant can see why, through both service actions.
        using var check = JsonDocument.Parse("""{"channel":"email","destination":"gone@example.com"}""");
        var checked_ = await new ServiceCheckSuppressionHandler(cluster.Plane).InvokeAsync(
            ActionContextFor(service, check.RootElement),
            CommunicationTestCluster.Ct
        );
        using var checkBody = JsonDocument.Parse(checked_.GetValueOrThrow());
        checkBody.RootElement.GetProperty("suppressed").GetBoolean().ShouldBeTrue();
        checkBody.RootElement.GetProperty("reason").GetString().ShouldBe("hardBounce");

        using var list = JsonDocument.Parse("""{"channel":""}""");
        var listed = await new ServiceListSuppressionsHandler(cluster.Plane).InvokeAsync(
            ActionContextFor(service, list.RootElement),
            CommunicationTestCluster.Ct
        );
        using var listBody = JsonDocument.Parse(listed.GetValueOrThrow());
        listBody.RootElement.GetProperty("count").GetInt32().ShouldBe(1);
        listBody.RootElement.GetProperty("entries")[0]
            .GetString()!
            .ShouldStartWith("email gone@example.com hardBounce ");
        CommunicationServices.ListSuppressionsResponse.Validate(listBody.RootElement).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task AnotherTenantsListDoesNotReachThisTenantsSends() {
        Carriers.Reset();

        // The same service name and the same address in another tenant, blocked there…
        var theirs = CommunicationTestCluster.Service("shared-name", CommunicationTestCluster.OtherTenant);
        (await cluster.ReconcileAsync(theirs, CommunicationServices.Body())).IsConverged.ShouldBeTrue();

        var theirBlock = CommunicationTestCluster.Child(
            CommunicationSuppressions.Type,
            "shared-name",
            "blocked",
            CommunicationTestCluster.OtherTenant
        );
        (await cluster.ReconcileAsync(
                theirBlock,
                CommunicationSuppressions.Body(destination: "shared@example.com")
            )).IsConverged.ShouldBeTrue();

        // …is not a block here. Two tenants' lists are two grains, keyed by tenant before path.
        var ours = await cluster.ConvergedServiceAsync("shared-name");
        await cluster.ConvergedEmailChannelAsync("shared-name");

        var sent = await cluster.Sender.SendAsync(
            CommunicationTestCluster.Tenant,
            CommunicationTestCluster.Send(ours, "shared@example.com", "cross-1"),
            CommunicationTestCluster.Ct
        );

        sent.IsSuccess.ShouldBeTrue(sent.Error?.Message);
        CommunicationServices.ServiceIdOf(theirs).ShouldNotBe(CommunicationServices.ServiceIdOf(ours));
    }

    static ActionContext ActionContextFor(ResourceId service, JsonElement body) =>
        new(
            service,
            CommunicationServices.V2026,
            string.Empty,
            body,
            default,
            string.Empty,
            null,
            new CyberCloud.ResourceManager.Conformance.InMemorySecretVault()
        );
}

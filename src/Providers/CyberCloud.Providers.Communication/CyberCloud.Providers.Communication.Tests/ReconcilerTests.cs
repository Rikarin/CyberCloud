namespace CyberCloud.Providers.Communication.Tests;

/// <summary>
///     What the four reconcilers decide that the shared suite cannot see: ownership of a channel
///     kind, a BYO channel without handles, a child ahead of its parent, a template's version
///     history, and the service's default locale reaching the send path.
/// </summary>
[Collection(CommunicationSilo.Name)]
public sealed class ReconcilerTests(CommunicationTestCluster cluster) {
    [Fact]
    public async Task ASecondResourceForTheSameKindIsRefusedByNameAndWritesNothing() {
        await cluster.ConvergedServiceAsync("one-owner");
        var first = await cluster.ConvergedEmailChannelAsync("one-owner", 100);

        var second = CommunicationTestCluster.Child(CommunicationChannels.Type, "one-owner", "email-again");
        var refused = await cluster.ReconcileAsync(
            second,
            CommunicationChannels.Body("email", provider: "in-memory", maxMessagesPerDay: 5)
        );

        refused.Kind.ShouldBe(ReconcileOutcomeKind.Failed);
        refused.Error!.Code.ShouldBe(ErrorCode.Conflict);
        refused.Error.Message.ShouldContain(first.Id.D());
        refused.Retryable.ShouldBeFalse("a conflict of ownership does not change between passes");

        // And the first resource's configuration is untouched.
        var held = await cluster.Plane.GetChannelAsync(
            CommunicationTestCluster.Tenant,
            CommunicationServices.ServiceIdOf(first),
            ChannelKind.Email,
            CommunicationTestCluster.Ct
        );
        held.GetValueOrThrow().Limits.MaxMessagesPerWindow.ShouldBe(100);
        held.GetValueOrThrow().OwnerResourceId.ShouldBe(first.Id);

        // Deleting the loser leaves the winner's configuration in place, and says so.
        var deleted = await cluster.DeleteAsync(second, CommunicationChannels.Body("email", provider: "in-memory"));
        deleted.IsConverged.ShouldBeTrue();
        (await cluster.Plane.GetChannelAsync(
                CommunicationTestCluster.Tenant,
                CommunicationServices.ServiceIdOf(first),
                ChannelKind.Email,
                CommunicationTestCluster.Ct
            ))
            .IsSuccess.ShouldBeTrue(
                "deleting a channel resource that did not own the kind removed the owner's configuration"
            );
    }

    [Fact]
    public async Task ATenantAccountWithoutHandlesIsRefusedRatherThanBilledAtThePlatformRate() {
        await cluster.ConvergedServiceAsync("byo");

        var channel = CommunicationTestCluster.Child(CommunicationChannels.Type, "byo", "sms");
        var refused = await cluster.ReconcileAsync(
            channel,
            CommunicationChannels.Body("sms", provider: "in-memory", account: "tenant")
        );

        refused.Kind.ShouldBe(ReconcileOutcomeKind.Failed);
        refused.Error!.Code.ShouldBe(ErrorCode.InvalidRequestBody);
        refused.Error.Message.ShouldContain("AccountRef");

        // With both handles it converges, and the handles — never values — are what the grain holds.
        var converged = await cluster.ReconcileAsync(
            channel,
            CommunicationChannels.Body(
                "sms",
                provider: "in-memory",
                account: "tenant",
                accountRef: "tenants/x/twilio#sid",
                authRef: "tenants/x/twilio#token@3"
            )
        );

        converged.IsConverged.ShouldBeTrue(converged.ToString());

        var held = (await cluster.Plane.GetChannelAsync(
                CommunicationTestCluster.Tenant,
                CommunicationServices.ServiceIdOf(channel),
                ChannelKind.Sms,
                CommunicationTestCluster.Ct
            )).GetValueOrThrow();
        held.Credentials.Mode.ShouldBe(CredentialMode.TenantAccount);
        held.Credentials.AccountRef.ShouldBe(new CarrierSecretRef { Path = "tenants/x/twilio", Field = "sid" });
        held.Credentials.AuthRef.ShouldBe(
            new CarrierSecretRef { Path = "tenants/x/twilio", Field = "token", Version = "3" }
        );
    }

    [Fact]
    public async Task AChildAheadOfItsServiceWaitsRatherThanFailing() {
        // The service resource exists — the manager guarantees that — but its first pass has not run.
        var channel = CommunicationTestCluster.Child(CommunicationChannels.Type, "not-yet", "email");
        var waiting = await cluster.ReconcileAsync(channel, CommunicationChannels.Body("email", provider: "in-memory"));

        waiting.Kind.ShouldBe(ReconcileOutcomeKind.InProgress, waiting.ToString());
        waiting.Reason.ShouldContain("not provisioned yet");

        var template = CommunicationTestCluster.Child(CommunicationTemplates.Type, "not-yet", "welcome");
        var alsoWaiting = await cluster.ReconcileAsync(template, CommunicationTemplates.Body());
        alsoWaiting.Kind.ShouldBe(ReconcileOutcomeKind.InProgress, alsoWaiting.ToString());

        await cluster.ConvergedServiceAsync("not-yet");

        (await cluster.ReconcileAsync(
                channel,
                CommunicationChannels.Body("email", provider: "in-memory")
            )).IsConverged.ShouldBeTrue();
        (await cluster.ReconcileAsync(template, CommunicationTemplates.Body())).IsConverged.ShouldBeTrue();
    }

    [Fact]
    public async Task ATemplateAppendsAVersionOnlyWhenTheBodyChangesAndKeepsItsHistoryAcrossRecreation() {
        var service = await cluster.ConvergedServiceAsync("versions");
        var serviceId = CommunicationServices.ServiceIdOf(service);

        var template = CommunicationTestCluster.Child(CommunicationTemplates.Type, "versions", "otp");
        var v1 = CommunicationTemplates.Body(body: "Your code is {code}.");

        (await cluster.ReconcileAsync(template, v1)).IsConverged.ShouldBeTrue();
        (await cluster.ReconcileAsync(template, v1)).IsConverged.ShouldBeTrue("a second pass over the same body");
        (await cluster.ReconcileAsync(template, v1)).IsConverged.ShouldBeTrue("and a third");

        var templateId = CommunicationTemplates.TemplateIdOf(template);
        (await cluster.Plane.ListTemplateVersionsAsync(
                CommunicationTestCluster.Tenant,
                templateId,
                CommunicationTestCluster.Ct
            ))
            .GetValueOrThrow()
            .Length.ShouldBe(1, "three identical passes appended more than one version");

        var v2 = CommunicationTemplates.Body(body: "Your one-time code is {code}.");
        (await cluster.ReconcileAsync(template, v2)).IsConverged.ShouldBeTrue();

        var versions = (await cluster.Plane.ListTemplateVersionsAsync(
                CommunicationTestCluster.Tenant,
                templateId,
                CommunicationTestCluster.Ct
            )).GetValueOrThrow();
        versions.Length.ShouldBe(2);
        versions[^1].Bodies[0].Body.ShouldBe("Your one-time code is {code}.");

        // Delete, then recreate under the same name with a NEW resource GUID: the name is
        // forgotten and remembered again, and the history continues at 3 rather than at 1.
        (await cluster.DeleteAsync(template, v2)).IsConverged.ShouldBeTrue();
        (await cluster.Plane.ResolveTemplateAsync(
                CommunicationTestCluster.Tenant,
                serviceId,
                "otp",
                CommunicationTestCluster.Ct
            )).IsFailure.ShouldBeTrue();

        var recreated = CommunicationTestCluster.Child(CommunicationTemplates.Type, "versions", "otp");
        recreated.Id.ShouldNotBe(template.Id);

        var v3 = CommunicationTemplates.Body(body: "Code: {code}");
        (await cluster.ReconcileAsync(recreated, v3)).IsConverged.ShouldBeTrue();

        (await cluster.Plane.ListTemplateVersionsAsync(
                CommunicationTestCluster.Tenant,
                CommunicationTemplates.TemplateIdOf(recreated),
                CommunicationTestCluster.Ct
            ))
            .GetValueOrThrow()
            .Length.ShouldBe(3, "the recreated template did not continue its history");
    }

    [Fact]
    public async Task ATemplateRecreatedForAnotherChannelIsRefusedRatherThanSilentlyKeptOnTheOld() {
        await cluster.ConvergedServiceAsync("channel-locked");

        var template = CommunicationTestCluster.Child(CommunicationTemplates.Type, "channel-locked", "greeting");
        (await cluster.ReconcileAsync(template, CommunicationTemplates.Body("email"))).IsConverged.ShouldBeTrue();
        (await cluster.DeleteAsync(template, CommunicationTemplates.Body("email"))).IsConverged.ShouldBeTrue();

        var recreated = CommunicationTestCluster.Child(CommunicationTemplates.Type, "channel-locked", "greeting");
        var refused = await cluster.ReconcileAsync(recreated, CommunicationTemplates.Body("sms"));

        refused.Kind.ShouldBe(ReconcileOutcomeKind.Failed, refused.ToString());
        refused.Error!.Code.ShouldBe(ErrorCode.Conflict);
        refused.Error.Message.ShouldContain("another name");
    }

    [Fact]
    public async Task TheServicesDefaultLocaleReachesASendThatNamesNone() {
        Carriers.Reset();

        var service = CommunicationTestCluster.Service("localised");
        (await cluster.ReconcileAsync(service, CommunicationServices.Body("cs"))).IsConverged.ShouldBeTrue();
        await cluster.ConvergedEmailChannelAsync("localised");

        // Two templates, two locales, two names — the api-version's one-locale-per-resource shape.
        var czech = CommunicationTestCluster.Child(CommunicationTemplates.Type, "localised", "otp-cs");
        (await cluster.ReconcileAsync(
                czech,
                CommunicationTemplates.Body(body: "Váš kód je {code}.", locale: "cs")
            )).IsConverged.ShouldBeTrue();

        var request = CommunicationTestCluster.Send(service, "someone@example.com", "loc-1") with {
            TemplateName = "otp-cs", Arguments = [new() { Name = "code", Value = "1" }], Body = ""
        };
        var sent = await cluster.Sender.SendAsync(
            CommunicationTestCluster.Tenant,
            request,
            CommunicationTestCluster.Ct
        );

        sent.IsSuccess.ShouldBeTrue(sent.Error?.Message);
        Carriers.Email.Sent.Single().Locale.ShouldBe("cs");
        Carriers.Email.Sent.Single().Body.ShouldBe("Váš kód je 1.");
    }

    [Fact]
    public async Task RetiringAServiceStopsSendsAndKeepsItsSuppressionList() {
        Carriers.Reset();

        var service = await cluster.ConvergedServiceAsync("retire");
        await cluster.ConvergedEmailChannelAsync("retire");
        var serviceId = CommunicationServices.ServiceIdOf(service);

        var block = CommunicationTestCluster.Child(CommunicationSuppressions.Type, "retire", "b");
        (await cluster.ReconcileAsync(
                block,
                CommunicationSuppressions.Body(destination: "kept@example.com")
            )).IsConverged.ShouldBeTrue();

        (await cluster.DeleteAsync(service, CommunicationServices.Body())).IsConverged.ShouldBeTrue();
        (await cluster.ObserveAsync(service, CommunicationServices.Body())).Exists.ShouldBeFalse();

        var refused = await cluster.Sender.SendAsync(
            CommunicationTestCluster.Tenant,
            CommunicationTestCluster.Send(service, "anyone@example.com", "retired-1"),
            CommunicationTestCluster.Ct
        );
        refused.IsFailure.ShouldBeTrue("a retired service still sends");
        Carriers.Email.Calls.ShouldBe(0);

        // ⚠ The list outlives the service — and a service recreated under the same name lands on
        // the same list, because the grain is keyed by the address.
        var check = await cluster.Plane.CheckSuppressionAsync(
            CommunicationTestCluster.Tenant,
            serviceId,
            ChannelKind.Email,
            "kept@example.com",
            CommunicationTestCluster.Ct
        );
        check.GetValueOrThrow().IsSuppressed.ShouldBeTrue("retiring the service cleared its suppression list");

        var again = CommunicationTestCluster.Service("retire");
        CommunicationServices.ServiceIdOf(again).ShouldBe(serviceId);
    }

    [Fact]
    public void TheServiceIdIsDerivedFromTheAddressAndTheChildDerivesItsParents() {
        var service = CommunicationTestCluster.Service("derive");
        var channel = CommunicationTestCluster.Child(CommunicationChannels.Type, "derive", "email");
        var template = CommunicationTestCluster.Child(CommunicationTemplates.Type, "derive", "t");

        CommunicationServices.ServiceIdOf(channel).ShouldBe(CommunicationServices.ServiceIdOf(service));
        CommunicationServices.ServiceIdOf(template).ShouldBe(CommunicationServices.ServiceIdOf(service));
        CommunicationServices.ServiceIdOf(service)
            .ShouldNotBe(service.Id, "the grain id is the address's, not the resource manager's");

        // Two spellings of one address are one grain; two tenants are two.
        var spelled = service with { Type = new("cybercloud.communication", "Services") };
        CommunicationServices.ServiceIdOf(spelled).ShouldBe(CommunicationServices.ServiceIdOf(service));
        CommunicationServices.ServiceIdOf(
            CommunicationTestCluster.Service("derive", CommunicationTestCluster.OtherTenant)
        )
            .ShouldNotBe(CommunicationServices.ServiceIdOf(service));

        // And a template's own id is not its service's.
        CommunicationTemplates.TemplateIdOf(template).ShouldNotBe(CommunicationServices.ServiceIdOf(service));
    }
}

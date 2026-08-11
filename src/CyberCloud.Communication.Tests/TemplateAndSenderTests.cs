namespace CyberCloud.Communication.Tests;

/// <summary>
///     Templates and sender identities, end to end — docs/plan/17 § The parts that are actually the
///     work, and § The channel abstraction's broker-not-carrier rule.
/// </summary>
[Collection(CommunicationClusterFixture.Name)]
public sealed class TemplateAndSenderTests(CommunicationCluster cluster) {
    async Task<Guid> TemplateAsync(Guid service, string name, ChannelKind channel = ChannelKind.Sms) {
        var templateId = Guid.NewGuid();

        (await cluster.Template(templateId).CreateAsync(service, name, channel)).IsSuccess.ShouldBeTrue();
        (await cluster.Template(templateId).AddVersionAsync(TestData.OtpParameters, TestData.OtpBodies))
            .IsSuccess.ShouldBeTrue();

        (await cluster.Service(service).RegisterTemplateAsync(name, templateId)).IsSuccess.ShouldBeTrue();

        return templateId;
    }

    // ── FAILURE CLASS: a template with a missing required parameter fails BEFORE dispatch ──────

    [Fact]
    public async Task ATemplateWithAMissingRequiredParameterFailsBeforeTheCarrier() {
        CommunicationCluster.ResetDoubles();
        var service = await cluster.NewServiceAsync();
        _ = await TemplateAsync(service, "otp");

        var refused = await cluster.SendAsync(
            CommunicationCluster.Tenant,
            new() {
                ServiceId = service,
                Channel = ChannelKind.Sms,
                Destination = "+420777123456",
                TemplateName = "otp",
                Locale = "en-US",
                Arguments = [],
                IdempotencyKey = "otp-1"
            }
        );

        refused.IsFailure.ShouldBeTrue();
        refused.Error!.Code.ShouldBe(ErrorCode.InvalidRequestBody);
        refused.Error.Message.ShouldContain("'code'");

        TestProviders.Sms.Calls.ShouldBe(
            0,
            "the carrier would have sent \"Your code is {code}\" to a customer — a wasted message, a "
            + "support ticket, and a complaint"
        );
    }

    [Fact]
    public async Task ATemplateWithItsParametersRendersAndDispatches() {
        CommunicationCluster.ResetDoubles();
        var service = await cluster.NewServiceAsync();
        _ = await TemplateAsync(service, "otp");

        var sent = await cluster.SendAsync(
            CommunicationCluster.Tenant,
            new() {
                ServiceId = service,
                Channel = ChannelKind.Sms,
                Destination = "+420777123456",
                TemplateName = "otp",
                Locale = "en-US",
                Arguments = [new() { Name = "code", Value = "424242" }],
                IdempotencyKey = "otp-2"
            }
        );

        sent.IsSuccess.ShouldBeTrue();
        TestProviders.Sms.Sent.Single().Body.ShouldBe("Your code is 424242.");
    }

    [Fact]
    public async Task ASendNamingATemplateThatDoesNotExistIsRefusedBeforeDispatch() {
        CommunicationCluster.ResetDoubles();
        var service = await cluster.NewServiceAsync();

        var refused = await cluster.SendAsync(
            CommunicationCluster.Tenant,
            new() {
                ServiceId = service,
                Channel = ChannelKind.Sms,
                Destination = "+420777123456",
                TemplateName = "does-not-exist",
                IdempotencyKey = "otp-3"
            }
        );

        refused.Error!.Code.ShouldBe(ErrorCode.ResourceNotFound);
        TestProviders.Sms.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task AFreeTextSendWithNoBodyAndNoTemplateIsRefused() {
        CommunicationCluster.ResetDoubles();
        var service = await cluster.NewServiceAsync();

        var refused = await cluster.SendAsync(
            CommunicationCluster.Tenant,
            new() {
                ServiceId = service,
                Channel = ChannelKind.Sms,
                Destination = "+420777123456",
                IdempotencyKey = "otp-4"
            }
        );

        refused.Error!.Code.ShouldBe(ErrorCode.InvalidRequestBody);
        TestProviders.Sms.Calls.ShouldBe(0);
    }

    // ── WhatsApp: pre-approved templates are the carrier's rule, and ours reflects it ───────────

    [Fact]
    public async Task AWhatsAppSendWithNoTemplateIsRefused() {
        CommunicationCluster.ResetDoubles();
        var service = await cluster.NewServiceAsync(ChannelKind.WhatsApp);

        var refused = await cluster.SendAsync(
            CommunicationCluster.Tenant,
            new() {
                ServiceId = service,
                Channel = ChannelKind.WhatsApp,
                Destination = "+420777123456",
                Body = "hello",
                IdempotencyKey = "wa-1"
            }
        );

        refused.Error!.Code.ShouldBe(ErrorCode.PolicyViolation);
        refused.Error.Message.ShouldContain("pre-approved template");
        TestProviders.WhatsApp.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task AWhatsAppTemplateWithNoCarrierApprovalIsRefusedRatherThanTriedAtMeta() {
        CommunicationCluster.ResetDoubles();
        var service = await cluster.NewServiceAsync(ChannelKind.WhatsApp);
        _ = await TemplateAsync(service, "otp", ChannelKind.WhatsApp);

        var refused = await cluster.SendAsync(
            CommunicationCluster.Tenant,
            new() {
                ServiceId = service,
                Channel = ChannelKind.WhatsApp,
                Destination = "+420777123456",
                TemplateName = "otp",
                Arguments = [new() { Name = "code", Value = "1" }],
                IdempotencyKey = "wa-2"
            }
        );

        refused.Error!.Code.ShouldBe(ErrorCode.PolicyViolation);
        refused.Error.Message.ShouldContain(
            "tenant's obligation",
            Case.Sensitive,
            "we are a broker, not a carrier — the refusal has to say whose job it is to fix"
        );

        TestProviders.WhatsApp.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task AnApprovedWhatsAppTemplateSendsByReference() {
        CommunicationCluster.ResetDoubles();
        var service = await cluster.NewServiceAsync(ChannelKind.WhatsApp);
        var templateId = await TemplateAsync(service, "otp", ChannelKind.WhatsApp);

        (await cluster.Template(templateId)
            .RecordApprovalAsync(1, SenderRegistrationStatus.Approved, "otp_v1_en"))
            .IsSuccess.ShouldBeTrue();

        var sent = await cluster.SendAsync(
            CommunicationCluster.Tenant,
            new() {
                ServiceId = service,
                Channel = ChannelKind.WhatsApp,
                Destination = "+420777123456",
                TemplateName = "otp",
                Arguments = [new() { Name = "code", Value = "424242" }],
                IdempotencyKey = "wa-3"
            }
        );

        sent.IsSuccess.ShouldBeTrue();
        TestProviders.WhatsApp.Sent.Single()
            .ProviderTemplateName
            .ShouldBe("otp_v1_en", "Meta accepts a template name and arguments, never a body");
    }

    [Fact]
    public async Task ADraftVersionCannotTakeAnApprovedWhatsAppTemplateDown() {
        CommunicationCluster.ResetDoubles();
        var service = await cluster.NewServiceAsync(ChannelKind.WhatsApp);
        var templateId = await TemplateAsync(service, "otp", ChannelKind.WhatsApp);

        _ = await cluster.Template(templateId).RecordApprovalAsync(1, SenderRegistrationStatus.Approved, "otp_v1_en");
        _ = await cluster.Template(templateId).AddVersionAsync(TestData.OtpParameters, TestData.OtpBodies);

        // Version 2 is a draft awaiting Meta, which can take days. Version 1 keeps serving.
        (await cluster.Template(templateId).GetVersionAsync(0)).GetValueOrThrow().Version.ShouldBe(1);
    }

    [Fact]
    public async Task AnApprovalWithNoCarrierTemplateNameIsRefused() {
        CommunicationCluster.ResetDoubles();
        var service = await cluster.NewServiceAsync(ChannelKind.WhatsApp);
        var templateId = await TemplateAsync(service, "otp", ChannelKind.WhatsApp);

        (await cluster.Template(templateId).RecordApprovalAsync(1, SenderRegistrationStatus.Approved, "  "))
            .Error!
            .Code
            .ShouldBe(ErrorCode.InvalidRequestBody);
    }

    [Fact]
    public async Task OnlyACarrierDecisionCanBeRecordedAsOne() {
        CommunicationCluster.ResetDoubles();
        var service = await cluster.NewServiceAsync(ChannelKind.WhatsApp);
        var templateId = await TemplateAsync(service, "otp", ChannelKind.WhatsApp);

        (await cluster.Template(templateId).RecordApprovalAsync(1, SenderRegistrationStatus.Pending, "x"))
            .Error!
            .Code
            .ShouldBe(ErrorCode.InvalidRequestBody);
    }

    // ── Sender identity: registration is the tenant's obligation, with our tooling ──────────────

    [Fact]
    public async Task ASendThroughAnUnapprovedSenderIsRefusedAndSaysWhoseJobItIs() {
        CommunicationCluster.ResetDoubles();
        var senderId = Guid.NewGuid();
        var service = await cluster.NewServiceAsync(senderId: senderId);

        _ = await cluster.SenderIdentity(senderId).RegisterAsync(ChannelKind.Sms, "CYBERCLOUD", ["CZ"]);
        _ = await cluster.SenderIdentity(senderId).MarkSubmittedAsync("campaign-4711");

        var refused = await cluster.SendAsync(
            CommunicationCluster.Tenant,
            CommunicationCluster.Request(service, "otp-1")
        );

        refused.Error!.Code.ShouldBe(ErrorCode.PolicyViolation);
        refused.Error.Message.ShouldContain("Pending");
        refused.Error.Message.ShouldContain("tenant's obligation");
        TestProviders.Sms.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task AnApprovedSenderSends() {
        CommunicationCluster.ResetDoubles();
        var senderId = Guid.NewGuid();
        var service = await cluster.NewServiceAsync(senderId: senderId);

        _ = await cluster.SenderIdentity(senderId).RegisterAsync(ChannelKind.Sms, "CYBERCLOUD", ["CZ"]);
        _ = await cluster.SenderIdentity(senderId).MarkSubmittedAsync("campaign-4711");
        _ = await cluster.SenderIdentity(senderId)
            .RecordDecisionAsync(SenderRegistrationStatus.Approved, ["CZ", "sk"], "approved");

        (await cluster.SendAsync(CommunicationCluster.Tenant, CommunicationCluster.Request(service, "otp-1")))
            .IsSuccess.ShouldBeTrue();

        (await cluster.SenderIdentity(senderId).GetAsync())
            .GetValueOrThrow()
            .Countries
            .ShouldBe(["CZ", "SK"], ignoreOrder: true);
    }

    [Fact]
    public async Task AnApprovedSenderClearedForNoCountryStillCannotSend() {
        CommunicationCluster.ResetDoubles();
        var senderId = Guid.NewGuid();
        var service = await cluster.NewServiceAsync(senderId: senderId);

        _ = await cluster.SenderIdentity(senderId).RegisterAsync(ChannelKind.Sms, "CYBERCLOUD", []);
        _ = await cluster.SenderIdentity(senderId).RecordDecisionAsync(SenderRegistrationStatus.Approved, [], "ok");

        var refused = await cluster.SendAsync(
            CommunicationCluster.Tenant,
            CommunicationCluster.Request(service, "otp-1")
        );

        refused.Error!
            .Message
            .ShouldContain(
                "never means everywhere",
                Case.Sensitive,
                "the other reading is the one a reasonable person reaches for, and it is the one that "
                + "puts an SMS into a jurisdiction the sender id is illegal in"
            );

        TestProviders.Sms.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task ARevokedSenderStopsSendingImmediately() {
        CommunicationCluster.ResetDoubles();
        var senderId = Guid.NewGuid();
        var service = await cluster.NewServiceAsync(senderId: senderId);
        var identity = cluster.SenderIdentity(senderId);

        _ = await identity.RegisterAsync(ChannelKind.Sms, "CYBERCLOUD", ["CZ"]);
        _ = await identity.RecordDecisionAsync(SenderRegistrationStatus.Approved, ["CZ"], "ok");

        (await cluster.SendAsync(CommunicationCluster.Tenant, CommunicationCluster.Request(service, "a")))
            .IsSuccess.ShouldBeTrue();

        _ = await identity.RecordDecisionAsync(SenderRegistrationStatus.Revoked, [], "campaign withdrawn");

        (await cluster.SendAsync(CommunicationCluster.Tenant, CommunicationCluster.Request(service, "b")))
            .Error!.Code.ShouldBe(ErrorCode.PolicyViolation);

        TestProviders.Sms.Calls.ShouldBe(1);
    }

    [Fact]
    public async Task ARejectionCarriesTheCarrierReasonVerbatim() {
        CommunicationCluster.ResetDoubles();
        var senderId = Guid.NewGuid();

        _ = await cluster.SenderIdentity(senderId).RegisterAsync(ChannelKind.Sms, "CYBERCLOUD", ["US"]);

        (await cluster.SenderIdentity(senderId).RecordDecisionAsync(SenderRegistrationStatus.Rejected, [], "  "))
            .Error!
            .Code
            .ShouldBe(
                ErrorCode.InvalidRequestBody,
                "the carrier's words are the only thing the tenant can act on, and paraphrasing loses "
                + "the code their support desk asks for"
            );
    }
}

/// <summary>
///     Channel configuration, BYO credentials and the refusing seams — docs/plan/17 § The channel
///     abstraction.
/// </summary>
[Collection(CommunicationClusterFixture.Name)]
public sealed class ChannelConfigurationTests(CommunicationCluster cluster) {
    [Fact]
    public async Task ByoCredentialsAreHandlesAndAByoChannelWithNoHandlesIsRefused() {
        CommunicationCluster.ResetDoubles();
        var serviceId = Guid.NewGuid();
        var service = cluster.Service(serviceId);

        _ = await service.CreateAsync(CommunicationCluster.Tenant, "primary");

        var refused = await service.ConfigureChannelAsync(
            new() {
                Channel = ChannelKind.Sms,
                Provider = "in-memory",
                Credentials = new() { Mode = CredentialMode.TenantAccount },
                Limits = new() { MaxMessagesPerWindow = 10, MaxSpendPerWindow = 10m },
                Enabled = true
            }
        );

        refused.Error!
            .Code
            .ShouldBe(
                ErrorCode.InvalidRequestBody,
                "a BYO channel with nothing to authenticate as would silently fall back to the "
                + "platform's account and bill at the marked-up rate the tenant chose BYO to avoid"
            );

        var accepted = await service.ConfigureChannelAsync(
            new() {
                Channel = ChannelKind.Sms,
                Provider = "in-memory",
                Credentials = new() {
                    Mode = CredentialMode.TenantAccount,
                    AccountRef = new() { Path = "tenants/x/communication/twilio", Field = "accountSid" },
                    AuthRef = new() { Path = "tenants/x/communication/twilio", Field = "authToken" }
                },
                Limits = new() { MaxMessagesPerWindow = 10, MaxSpendPerWindow = 10m },
                EstimatedUnitCost = 0.01m,
                Enabled = true
            }
        );

        accepted.IsSuccess.ShouldBeTrue("BYO is offered from day one — docs/plan/17 § The channel abstraction");

        var stored = (await service.GetChannelAsync(ChannelKind.Sms)).GetValueOrThrow();
        stored.Credentials.AuthRef.Path.ShouldBe("tenants/x/communication/twilio");
        stored.Credentials.AuthRef.Field.ShouldBe("authToken", "a field NAME is an address, not a value");
    }

    [Fact]
    public async Task TheCredentialHandlesReachTheProviderAndTheProviderResolvesThem() {
        CommunicationCluster.ResetDoubles();
        var serviceId = Guid.NewGuid();
        var service = cluster.Service(serviceId);

        _ = await service.CreateAsync(CommunicationCluster.Tenant, "primary");
        _ = await service.ConfigureChannelAsync(
            new() {
                Channel = ChannelKind.Sms,
                Provider = "in-memory",
                Credentials = new() {
                    Mode = CredentialMode.TenantAccount,
                    AccountRef = new() { Path = "p", Field = "accountSid" },
                    AuthRef = new() { Path = "p", Field = "authToken", Version = "3" }
                },
                Limits = new() { MaxMessagesPerWindow = 10, MaxSpendPerWindow = 10m },
                EstimatedUnitCost = 0.01m,
                Enabled = true
            }
        );

        _ = await cluster.SendAsync(
            CommunicationCluster.Tenant,
            CommunicationCluster.Request(serviceId, "otp-1")
        );

        var handed = TestProviders.Sms.Sent.Single();

        handed.Credentials.Mode.ShouldBe(CredentialMode.TenantAccount);
        handed.Credentials.AuthRef.Version.ShouldBe(
            "3",
            "pinning a version is what makes a dispatch reproducible across a rotation"
        );
    }

    [Fact]
    public async Task ADisabledChannelRefusesAndIsDistinctFromAnUnconfiguredOne() {
        CommunicationCluster.ResetDoubles();
        var service = await cluster.NewServiceAsync();

        (await cluster.Service(service).SetChannelEnabledAsync(ChannelKind.Sms, false)).IsSuccess.ShouldBeTrue();

        var disabled = await cluster.SendAsync(
            CommunicationCluster.Tenant,
            CommunicationCluster.Request(service, "otp-1")
        );

        disabled.Error!.Code.ShouldBe(ErrorCode.PolicyViolation);
        TestProviders.Sms.Calls.ShouldBe(0);

        var unconfigured = await cluster.SendAsync(
            CommunicationCluster.Tenant,
            CommunicationCluster.Request(service, "otp-2", "alice@example.com", ChannelKind.Email)
        );

        unconfigured.Error!
            .Code
            .ShouldBe(
                ErrorCode.ResourceNotFound,
                "\"this tenant does not use email\" and \"somebody turned SMS off\" are different "
                + "sentences, and a support case starts by telling them apart"
            );
    }

    // ── The refusing seams ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AChannelPointedAtNoCarrierRefusesRatherThanReportingSuccess() {
        CommunicationCluster.ResetDoubles();
        var serviceId = Guid.NewGuid();
        var service = cluster.Service(serviceId);

        _ = await service.CreateAsync(CommunicationCluster.Tenant, "primary");
        _ = await service.ConfigureChannelAsync(
            new() {
                Channel = ChannelKind.Sms,
                Provider = "unavailable",
                Credentials = new() { Mode = CredentialMode.PlatformAccount },
                Limits = new() { MaxMessagesPerWindow = 10, MaxSpendPerWindow = 10m },
                EstimatedUnitCost = 0.01m,
                Enabled = true
            }
        );

        var sent = await cluster.SendAsync(
            CommunicationCluster.Tenant,
            CommunicationCluster.Request(serviceId, "otp-1")
        );

        // ⚠ Reported as Failed rather than Dispatched. An OTP factor that reports delivery and sends
        // nothing locks every user who enrols in it out of their account — IOtpDeliverySeam says so
        // from the other end of this same seam.
        sent.GetValueOrThrow().Status.ShouldBe(MessageStatus.Failed);
        sent.GetValueOrThrow().Detail.ShouldContain("No Sms carrier is registered");
        sent.GetValueOrThrow().Detail.ShouldContain("10DLC", Case.Sensitive, "and it says what a real one owes");
    }

    [Fact]
    public async Task ARefusedDispatchCostsNoBudget() {
        CommunicationCluster.ResetDoubles();
        var serviceId = Guid.NewGuid();
        var service = cluster.Service(serviceId);

        _ = await service.CreateAsync(CommunicationCluster.Tenant, "primary");
        _ = await service.ConfigureChannelAsync(
            new() {
                Channel = ChannelKind.Sms,
                Provider = "unavailable",
                Credentials = new() { Mode = CredentialMode.PlatformAccount },
                Limits = new() { MaxMessagesPerWindow = 10, MaxSpendPerWindow = 10m },
                EstimatedUnitCost = 1m,
                Enabled = true
            }
        );

        _ = await cluster.SendAsync(
            CommunicationCluster.Tenant,
            CommunicationCluster.Request(serviceId, "otp-1")
        );

        (await cluster.Limits(serviceId).ReadAsync(ChannelKind.Sms)).GetValueOrThrow().Committed.ShouldBe(0m);
    }

    [Fact]
    public void EveryChannelHasARefusingSeamRegisteredForIt() {
        var registry = new ChannelProviderRegistry([
            new UnavailableSmsProvider(Microsoft.Extensions.Logging.Abstractions.NullLogger<UnavailableSmsProvider>.Instance),
            new UnavailableWhatsAppProvider(Microsoft.Extensions.Logging.Abstractions.NullLogger<UnavailableWhatsAppProvider>.Instance),
            new UnavailableEmailProvider(Microsoft.Extensions.Logging.Abstractions.NullLogger<UnavailableEmailProvider>.Instance),
            new UnavailablePushProvider(Microsoft.Extensions.Logging.Abstractions.NullLogger<UnavailablePushProvider>.Instance),
            new UnavailableVoiceProvider(Microsoft.Extensions.Logging.Abstractions.NullLogger<UnavailableVoiceProvider>.Instance)
        ]);

        foreach (var channel in Enum.GetValues<ChannelKind>().Where(x => x != ChannelKind.Unknown)) {
            registry.Resolve(channel, "unavailable")
                .IsSuccess
                .ShouldBeTrue(
                    $"a {channel} send with no carrier must fail with a sentence saying so and what a "
                    + "real implementation owes, not with a wiring error"
                );
        }
    }

    [Fact]
    public void AnUnnamedProviderWithSeveralCandidatesIsRefusedRatherThanPickedByRegistrationOrder() {
        var registry = new ChannelProviderRegistry([
            new InMemoryChannelProvider(ChannelKind.Sms),
            new UnavailableSmsProvider(Microsoft.Extensions.Logging.Abstractions.NullLogger<UnavailableSmsProvider>.Instance)
        ]);

        registry.Resolve(ChannelKind.Sms, string.Empty)
            .Error!
            .Code
            .ShouldBe(
                ErrorCode.InvalidRequestBody,
                "which carrier a tenant sends through is not a thing to decide by the order somebody "
                + "wrote lines in a wiring method"
            );
    }
}

using CyberCloud.Authorization.Contracts;
using CyberCloud.Core.Resources;
using CyberCloud.Identity.Contracts;
using CyberCloud.Identity.Host.Api;
using CyberCloud.Identity.Host.SignUp;
using CyberCloud.Identity.Host.Tests.Infrastructure;
using CyberCloud.Identity.SignIn;
using System.Globalization;

namespace CyberCloud.Identity.Host.Tests;

/// <summary>
///     The decisions behind <c>/api/signup/*</c>, asserted on what the handlers return and on what
///     the orchestrator asks of the grains and the scope manager.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Two rules from the sign-in surface carry over and one does not.</b> <c>begin</c>
///         answers the same body for every address and touches no grain for a malformed one — the
///         enumeration rule, unchanged. What does not carry over is uniform failure on
///         <c>complete</c>: by then the caller has proven an address, and the five sentences it can
///         answer are about the caller's own input. <see cref="ATakenSlugIsNamed" /> is the row that
///         would look like a leak on the sign-in surface and is the correct answer here.
///     </para>
///     <para>
///         ⚠ <b>The orchestrator's order and authority are the subject of the last two rows.</b>
///         <see cref="AFailedStepLeavesTheTicketRedrivableAndResumesAtTheNextStep" /> is what makes
///         the flow safe to retry: a second <c>complete</c> after a failure must not create a second
///         tenant. <see cref="TheOrchestratorCreatesTheScopesAsTheNewUserAndTheTenantAsTheOperator" />
///         is docs/plan/06 § Platform administration applied — the tenant as the seeded operator,
///         owned by the user; everything inside it as the user.
///     </para>
/// </remarks>
public sealed class SignUpApiTests {
    /// <summary>The runner's token, so a hung test is cancellable — xUnit1051.</summary>
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    static SignInContext Context => new() { ClientId = "cyc-portal", DeviceLabel = "tests", ClientAddress = "127.0.0.1" };

    static SignUpCompleteRequest Password(string organisation = "Contoso", string returnUrl = "/") =>
        new("Rene", organisation, new(SignUpCredential.PasswordKind, null, "correct-horse-battery-staple"), returnUrl);

    // ── begin ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task BeginAnswersTheSameBodyForAnyAddress() {
        var harness = new SignUpApiHarness();

        // ⚠ Registered-looking, absent, malformed, empty, absurd — the answer may not vary with any
        // of them. A page that could tell "sent" from "not sent" would enumerate on the platform's
        // behalf, and this endpoint is unauthenticated and reachable at volume.
        var answers = new List<SignUpBeginResponse>();
        foreach (var address in new[] {
                     "someone@example.com", "nobody-has-this-address@example.com", "not-an-address", "",
                     new string('x', 400) + "@example.com"
                 }) {
            var result = await harness.Api.BeginAsync(new(address, "/after"), null, Ct);
            answers.Add(result.Body.ShouldBeOfType<SignUpBeginResponse>());
        }

        foreach (var answer in answers) {
            answer.ShouldBe(answers[0]);
        }

        answers[0].Sent.ShouldBeTrue();
        answers[0].ReturnUrl.ShouldBe("/after");

        (await harness.Api.BeginAsync(null, null, Ct)).Body.ShouldBeOfType<SignUpBeginResponse>().ShouldBe(
            answers[0] with { ReturnUrl = ReturnUrl.Default }
        );
    }

    [Fact]
    public async Task AMalformedAddressTouchesNoGrain() {
        var harness = new SignUpApiHarness();

        var result = await harness.Api.BeginAsync(new("not-an-address", "/"), null, Ct);

        // ⚠ docs/plan/11 § Credentials: an unauthenticated endpoint whose path costs a grain
        // activation is an amplifier. A well-formed address reaches a grain keyed by a random id the
        // host minted — the caller cannot choose the activation — and a malformed one reaches none.
        harness.Grains.References.ShouldBe(0, "a malformed address must not reach a grain");
        result.Ticket.ShouldBeNull("nothing was begun, so there is nothing to name");
        result.Body.ShouldBeOfType<SignUpBeginResponse>().Sent.ShouldBeTrue("and the answer is still the one answer");

        var wellFormed = await harness.Api.BeginAsync(new("someone@example.com", "/"), null, Ct);
        harness.Grains.References.ShouldBe(1);
        wellFormed.Ticket.ShouldNotBeNull();
    }

    [Fact]
    public async Task ABeginWithATicketReissuesOnTheSameSignUp() {
        var harness = new SignUpApiHarness();

        var first = await harness.Api.BeginAsync(new("someone@example.com", "/"), null, Ct);
        var ticket = first.Ticket.ShouldNotBeNull();

        var again = await harness.Api.BeginAsync(new("someone@example.com", "/"), ticket, Ct);

        again.Ticket.ShouldNotBeNull().SignupId.ShouldBe(ticket.SignupId, "a resend is the same sign-up");
        harness.Grains.SignUps.Count.ShouldBe(1);
        harness.Grains.SignUps[ticket.SignupId].Issues.ShouldBe(2);
    }

    // ── verify, and the ticket ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task EveryCallAfterBeginAnswers401WithoutATicket() {
        var harness = new SignUpApiHarness();

        (await harness.Api.VerifyAsync(new("482913"), null)).Unauthorized.ShouldBeTrue();
        (await harness.Api.BeginPasskeyAsync(new("Rene"), null)).Unauthorized.ShouldBeTrue();
        (await harness.Api.CompleteAsync(Password(), null, null, Context, Ct)).Unauthorized.ShouldBeTrue();

        harness.Grains.References.ShouldBe(0, "no ticket, no grain");
    }

    [Fact]
    public async Task AWrongCodeIsFalseAndTheRightOneIsTrueOnce() {
        var harness = new SignUpApiHarness();
        var ticket = (await harness.Api.BeginAsync(new("someone@example.com", "/"), null, Ct)).Ticket!;

        (await harness.Api.VerifyAsync(new("000000"), ticket)).Body.ShouldBeOfType<SignUpVerifyResponse>().Verified.ShouldBeFalse();
        (await harness.Api.VerifyAsync(new("482913"), ticket)).Body.ShouldBeOfType<SignUpVerifyResponse>().Verified.ShouldBeTrue();
        (await harness.Api.VerifyAsync(new("482913"), ticket)).Body.ShouldBeOfType<SignUpVerifyResponse>().Verified.ShouldBeFalse();
    }

    // ── complete ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CompleteBeforeVerifyIsRefusedByName() {
        var harness = new SignUpApiHarness();
        var ticket = (await harness.Api.BeginAsync(new("someone@example.com", "/"), null, Ct)).Ticket!;

        var result = await harness.Api.CompleteAsync(Password(), ticket, null, Context, Ct);

        var body = result.Body.ShouldBeOfType<SignUpCompleteResponse>();
        body.Succeeded.ShouldBeFalse();
        body.Message.ShouldBe(SignUpApi.NotVerifiedMessage);
        harness.Scopes.TenantCreates.ShouldBeEmpty("nothing is created for an unproven address");
        result.Principal.ShouldBeNull();
    }

    [Fact]
    public async Task CompleteRewritesTheReturnUrlWithTheTenant() {
        var harness = new SignUpApiHarness();
        var ticket = await harness.VerifiedSignUpAsync();

        // ⚠ The original /authorize request carried the REMEMBERED tenant of an earlier sign-in
        // (the portal's cyc-tenant cookie). The resumed request has to name the tenant the new
        // cookie is for, so the hint is replaced rather than appended.
        var returnUrl = "/authorize?response_type=code&client_id=cyc-portal&tenant=old-tenant&state=xyz";

        var result = await harness.Api.CompleteAsync(Password(returnUrl: returnUrl), ticket, null, Context, Ct);

        var body = result.Body.ShouldBeOfType<SignUpCompleteResponse>();
        body.Succeeded.ShouldBeTrue(body.Message);

        var tenant = harness.Grains.SignUps[ticket.SignupId].TenantId.ToString("D", CultureInfo.InvariantCulture);
        body.TenantId.ShouldBe(tenant);
        body.ReturnUrl.ShouldBe($"/authorize?response_type=code&client_id=cyc-portal&tenant={tenant}&state=xyz");

        ReturnUrl.IsSafe(body.ReturnUrl).ShouldBeTrue("the rewritten URL is still a same-origin path");
        result.ClearTicket.ShouldBeTrue("the sign-up is done, so the ticket is spent");
    }

    [Theory]
    [InlineData("/", "/?tenant=t")]
    [InlineData("/authorize", "/authorize?tenant=t")]
    [InlineData("/authorize?a=1", "/authorize?a=1&tenant=t")]
    [InlineData("/authorize?tenant=x&a=1", "/authorize?tenant=t&a=1")]
    [InlineData("/authorize?a=1#frag", "/authorize?a=1&tenant=t#frag")]
    public void WithTenantSetsTheParameterOnce(string returnUrl, string expected) =>
        SignUpApi.WithTenant(returnUrl, "t").ShouldBe(expected);

    [Fact]
    public async Task CompleteIssuesASatisfiedCookieWithTheRightAmr() {
        // ── A password: the address was proven by the enrolment code, which IS the second factor. ──
        var withPassword = new SignUpApiHarness();
        var passwordTicket = await withPassword.VerifiedSignUpAsync();

        var password = await withPassword.Api.CompleteAsync(Password(), passwordTicket, null, Context, Ct);

        var principal = password.Principal.ShouldNotBeNull("a completed sign-up signs the person in");
        IdentitySessionPrincipal.IsFullyAuthenticated(principal).ShouldBeTrue("nothing is owed — the code was the second factor");
        principal.FindAll(AccessTokenClaims.AuthenticationMethods).Select(x => x.Value).ShouldBe(["pwd", "otp"]);
        principal.FindFirst(AccessTokenClaims.TenantId)!.Value
            .ShouldBe(withPassword.Grains.SignUps[passwordTicket.SignupId].TenantId.ToString("N", CultureInfo.InvariantCulture));
        principal.FindFirst(AccessTokenClaims.Subject)!.Value
            .ShouldBe(withPassword.Grains.SignUps[passwordTicket.SignupId].UserId.ToString("N", CultureInfo.InvariantCulture));

        // ── A passkey: two factors on its own, spelled as sign-in spells it. ──────────────────────
        var withPasskey = new SignUpApiHarness();
        var passkeyTicket = await withPasskey.VerifiedSignUpAsync();

        var begun = await withPasskey.Api.BeginPasskeyAsync(new("Rene"), passkeyTicket);
        begun.Body.ShouldBeOfType<PasskeyBeginResponse>().OptionsJson.ShouldNotBeEmpty();
        var challenge = begun.Challenge.ShouldNotBeNull();
        challenge.Kind.ShouldBe(PasskeyChallengeKind.Registration, "a registration challenge, so sign-in cannot answer it");
        withPasskey.Passkeys.Requested!.UserId.ShouldBe(withPasskey.Grains.SignUps[passkeyTicket.SignupId].UserId);

        var passkey = await withPasskey.Api.CompleteAsync(
            new("Rene", "Contoso", new(SignUpCredential.PasskeyKind, "{\"id\":\"cred-1\"}", null), "/"),
            passkeyTicket,
            challenge,
            Context,
            Ct
        );

        passkey.Body.ShouldBeOfType<SignUpCompleteResponse>().Succeeded.ShouldBeTrue();
        var hardware = passkey.Principal.ShouldNotBeNull();
        IdentitySessionPrincipal.IsFullyAuthenticated(hardware).ShouldBeTrue();
        hardware.FindAll(AccessTokenClaims.AuthenticationMethods).Select(x => x.Value)
            .ShouldBe([AuthenticationMethodNames.Of(AuthenticationMethod.Passkey)]);
        withPasskey.UserOf(passkeyTicket)!.Passkey.ShouldBe(withPasskey.Passkeys.Credential);
    }

    [Fact]
    public async Task APasskeyCompletionWithoutARegistrationChallengeCreatesNothing() {
        var harness = new SignUpApiHarness();
        var ticket = await harness.VerifiedSignUpAsync();

        // An assertion ticket — a sign-in's cookie — presented to a registration. PasskeyChallengeKind
        // says why the two must not answer each other.
        var assertion = new PasskeyChallengeTicket("{}", "rene@example.com", DateTimeOffset.MaxValue);

        var result = await harness.Api.CompleteAsync(
            new("Rene", "Contoso", new(SignUpCredential.PasskeyKind, "{}", null), "/"),
            ticket,
            assertion,
            Context,
            Ct
        );

        result.Body.ShouldBeOfType<SignUpCompleteResponse>().Succeeded.ShouldBeFalse();
        harness.Scopes.TenantCreates.ShouldBeEmpty("the credential is verified BEFORE anything is created");
    }

    [Fact]
    public async Task ATakenSlugIsNamed() {
        var harness = new SignUpApiHarness();
        harness.Grains.Directory.Held["contoso"] = Guid.NewGuid();
        var ticket = await harness.VerifiedSignUpAsync();

        var result = await harness.Api.CompleteAsync(Password("Contoso"), ticket, null, Context, Ct);

        var body = result.Body.ShouldBeOfType<SignUpCompleteResponse>();
        body.Succeeded.ShouldBeFalse();
        body.Message.ShouldBe(SignUpApi.SlugTakenMessage);

        // ⚠ Nothing was created, and the sign-up is still live: the person picks another name.
        harness.Scopes.TenantCreates.ShouldBeEmpty("the directory is asked before CreateTenantAsync, so nothing half-exists");
        result.ClearTicket.ShouldBeFalse();

        var retried = await harness.Api.CompleteAsync(Password("Contoso Ltd"), ticket, null, Context, Ct);
        retried.Body.ShouldBeOfType<SignUpCompleteResponse>().Succeeded.ShouldBeTrue();
        harness.Scopes.TenantCreates.ShouldHaveSingleItem().Request.Slug.ShouldBe("contoso-ltd");
    }

    [Fact]
    public async Task ASlugConflictFromTheManagerIsNamedToo() {
        // The race the directory pre-check cannot close: two sign-ups choosing one name in the same
        // second. The manager's Conflict maps to the same sentence.
        var harness = new SignUpApiHarness { Scopes = { TenantSlugConflicts = true } };
        var ticket = await harness.VerifiedSignUpAsync();

        var result = await harness.Api.CompleteAsync(Password(), ticket, null, Context, Ct);

        result.Body.ShouldBeOfType<SignUpCompleteResponse>().Message.ShouldBe(SignUpApi.SlugTakenMessage);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("!!!")]
    [InlineData("★ ✦ ★")]
    public async Task AnOrganisationNameWithNoSlugIsRefusedWithTheNamingRule(string organisation) {
        // ⚠ Slugify keeps ASCII letters and digits and nothing else, so a name made of anything else
        // slugs to the empty string, and the naming rule's own sentence is what the person reads.
        var harness = new SignUpApiHarness();
        var ticket = await harness.VerifiedSignUpAsync();

        var result = await harness.Api.CompleteAsync(Password(organisation), ticket, null, Context, Ct);

        var body = result.Body.ShouldBeOfType<SignUpCompleteResponse>();
        body.Succeeded.ShouldBeFalse();
        body.Message.ShouldBe(ResourceNaming.Validate(ResourceNaming.Slugify(organisation), "organisation name").Error!.Message);
        harness.Scopes.TenantCreates.ShouldBeEmpty();
    }

    [Fact]
    public async Task AnEmptyPasswordIsRefusedBeforeAnythingIsCreated() {
        var harness = new SignUpApiHarness();
        var ticket = await harness.VerifiedSignUpAsync();

        foreach (var password in new[] { "", null }) {
            var result = await harness.Api.CompleteAsync(
                new("Rene", "Contoso", new(SignUpCredential.PasswordKind, null, password), "/"),
                ticket,
                null,
                Context,
                Ct
            );

            var body = result.Body.ShouldBeOfType<SignUpCompleteResponse>();
            body.Succeeded.ShouldBeFalse();
            body.Message.ShouldBe(SignUpApi.PasswordRequiredMessage);
            result.ClearTicket.ShouldBeFalse();
        }

        // ⚠ Refused at step a, beside the passkey check, and not at the credential step: a refusal
        // after the tenant and the user exist would leave a tenant holding the slug with a
        // credential-less user, the retry would resume at the credential step so the organisation
        // name could no longer change, and an abandoned attempt would hold the slug for good.
        harness.Scopes.TenantCreates.ShouldBeEmpty("a tenant was created for a credential that cannot be set");
        harness.Grains.SignUps[ticket.SignupId].Steps.ShouldBeEmpty();

        // The ticket is still good: the same complete, with a password, creates everything.
        var completed = await harness.Api.CompleteAsync(Password(), ticket, null, Context, Ct);

        completed.Body.ShouldBeOfType<SignUpCompleteResponse>().Succeeded.ShouldBeTrue();
        harness.Scopes.TenantCreates.Count.ShouldBe(1);
    }

    [Fact]
    public async Task AFailedStepLeavesTheTicketRedrivableAndResumesAtTheNextStep() {
        var harness = new SignUpApiHarness();
        var ticket = await harness.VerifiedSignUpAsync();
        var signup = harness.Grains.SignUps[ticket.SignupId];

        // ⚠ The subscription step fails — a silo lost mid-flow, say. Everything before it ran.
        harness.Scopes.FailOnceAt = ScopeId.Subscription(signup.TenantId, signup.SubscriptionId).Path;

        var first = await harness.Api.CompleteAsync(Password(), ticket, null, Context, Ct);

        var failed = first.Body.ShouldBeOfType<SignUpCompleteResponse>();
        failed.Succeeded.ShouldBeFalse();
        failed.Message.ShouldBe(SignUpApi.StepFailedMessage);
        first.Principal.ShouldBeNull("nobody is signed into a half-made tenant");
        first.ClearTicket.ShouldBeFalse("the ticket stays valid: complete is re-drivable");

        signup.Steps.ShouldBe([SignUpStep.TenantCreated, SignUpStep.UserCreated, SignUpStep.CredentialSet]);

        // ── The retry. ─────────────────────────────────────────────────────────────────────────
        var second = await harness.Api.CompleteAsync(Password(), ticket, null, Context, Ct);

        second.Body.ShouldBeOfType<SignUpCompleteResponse>().Succeeded.ShouldBeTrue();
        second.Principal.ShouldNotBeNull();
        second.ClearTicket.ShouldBeTrue();

        signup.Steps.ShouldBe([
            SignUpStep.TenantCreated, SignUpStep.UserCreated, SignUpStep.CredentialSet,
            SignUpStep.SubscriptionCreated, SignUpStep.ResourceGroupCreated, SignUpStep.Completed
        ]);

        // ⚠ ONE tenant, ONE user, and the subscription tried twice — the retry skipped what was
        // recorded and started at the step that failed.
        harness.Scopes.TenantCreates.ShouldHaveSingleItem();
        harness.UserOf(ticket)!.Creates.ShouldBe(1);
        harness.Scopes.Creates.Count(x => x.Path == ScopeId.Subscription(signup.TenantId, signup.SubscriptionId).Path)
            .ShouldBe(2);
        harness.Scopes.Creates.Count(x => x.Path == ScopeId.Group(signup.TenantId, signup.SubscriptionId, "default").Path)
            .ShouldBe(1);
    }

    [Fact]
    public async Task ADeniedSubscriptionCreateIsRetriedOnceAfterTheOwnerEdgePropagates() {
        var harness = new SignUpApiHarness();
        var ticket = await harness.VerifiedSignUpAsync();
        var signup = harness.Grains.SignUps[ticket.SignupId];

        // The seam renders a check that has not seen the owner tuple yet as "does not exist".
        harness.Scopes.FailOnceAt = ScopeId.Subscription(signup.TenantId, signup.SubscriptionId).Path;
        harness.Scopes.FailWith = ErrorCode.ResourceNotFound;

        var result = await harness.Api.CompleteAsync(Password(), ticket, null, Context, Ct);

        result.Body.ShouldBeOfType<SignUpCompleteResponse>().Succeeded.ShouldBeTrue("one retry after OwnerPropagationDelay");
        harness.Scopes.Creates.Count(x => x.Path == ScopeId.Subscription(signup.TenantId, signup.SubscriptionId).Path)
            .ShouldBe(2);
    }

    [Fact]
    public async Task SignUpOffAnswersTheClosedMessage() {
        var harness = new SignUpApiHarness(selfServe: false);

        foreach (var (endpoint, result) in new[] {
                     ("begin", await harness.Api.BeginAsync(new("someone@example.com", "/after"), null, Ct)),
                     ("verify", await harness.Api.VerifyAsync(new("482913"), null)),
                     ("passkey/begin", await harness.Api.BeginPasskeyAsync(new("Rene"), null)),
                     ("complete", await harness.Api.CompleteAsync(Password(), null, null, Context, Ct))
                 }) {
            var body = result.Body.ShouldBeOfType<SignUpCompleteResponse>(endpoint);
            body.Succeeded.ShouldBeFalse(endpoint);
            body.Message.ShouldBe(SignUpApi.ClosedMessage, endpoint);
            result.Unauthorized.ShouldBeFalse($"{endpoint}: closed is a body, not a status");
            result.Ticket.ShouldBeNull(endpoint);
        }

        harness.Grains.References.ShouldBe(0, "a closed surface touches nothing");
    }

    [Fact]
    public async Task TheOrchestratorCreatesTheScopesAsTheNewUserAndTheTenantAsTheOperator() {
        var harness = new SignUpApiHarness();
        var ticket = await harness.VerifiedSignUpAsync();
        var signup = harness.Grains.SignUps[ticket.SignupId];

        var result = await harness.Api.CompleteAsync(Password("Contoso Ltd."), ticket, null, Context, Ct);
        result.Body.ShouldBeOfType<SignUpCompleteResponse>().Succeeded.ShouldBeTrue();

        var user = signup.UserId.ToString("N", CultureInfo.InvariantCulture);

        // ── The tenant: as the seeded operator, in the platform tenant, owned by the new user. ────
        var (request, caller) = harness.Scopes.TenantCreates.ShouldHaveSingleItem();
        caller.TenantId.ShouldBe(Guid.Empty);
        caller.SubjectType.ShouldBe(SubjectTypes.ServicePrincipal);
        caller.SubjectId.ShouldBe(IdentityBootstrap.SignUpOperator.ToString("N", CultureInfo.InvariantCulture));

        request.TenantId.ShouldBe(signup.TenantId);
        request.Slug.ShouldBe("contoso-ltd");
        request.DisplayName.ShouldBe("Contoso Ltd.");
        request.HomeRegion.ShouldBe(SignUpApiHarness.Region);
        request.OwnerSubjectType.ShouldBe(SubjectTypes.User);
        request.OwnerSubjectId.ShouldBe(user, "the operator is never the owner — IScopeManager.CreateTenantAsync");

        // ── The scopes: as the new user, in the new tenant, through the ordinary path. ──────────
        harness.Scopes.Creates.Count.ShouldBe(2);

        var subscription = harness.Scopes.Creates[0];
        subscription.Path.ShouldBe(ScopeId.Subscription(signup.TenantId, signup.SubscriptionId).Path);
        subscription.Body.ShouldContain($"\"displayName\":\"{SignUpOrchestrator.DefaultSubscriptionName}\"");

        var group = harness.Scopes.Creates[1];
        group.Path.ShouldBe(ScopeId.Group(signup.TenantId, signup.SubscriptionId, SignUpOrchestrator.DefaultResourceGroupName).Path);
        group.Body.ShouldContain($"\"location\":\"{SignUpApiHarness.Region}\"");

        foreach (var scope in harness.Scopes.Creates) {
            scope.Caller.TenantId.ShouldBe(signup.TenantId);
            scope.Caller.SubjectType.ShouldBe(SubjectTypes.User);
            scope.Caller.SubjectId.ShouldBe(user, $"{scope.Path} is created as the person, exactly as the portal would");
        }

        // ── And the user: created in the tenant, holding the confirmed email claim. ─────────────
        var created = harness.UserOf(ticket).ShouldNotBeNull();
        created.Email.ShouldBe("rene@example.com");
        created.DisplayName.ShouldBe("Rene");
        created.Password.ShouldBe("correct-horse-battery-staple");
        created.Sessions.ShouldHaveSingleItem();
        harness.Grains.EmailIndexes.Values.ShouldHaveSingleItem().Confirmed.ShouldBeTrue();
    }
}

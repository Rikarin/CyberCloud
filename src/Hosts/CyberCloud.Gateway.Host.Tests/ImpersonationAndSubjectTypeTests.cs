using CyberCloud.Gateway.Host.Tests.Infrastructure;

namespace CyberCloud.Gateway.Host.Tests;

/// <summary>
///     The two claims <c>ICallerContextResolver</c> asked the identity host for, asserted from the
///     gateway's side: <c>sub_typ</c> and <c>act_sub</c>.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The impersonation half is a security property and not a mapping test.</b> docs/plan/06
///         § Platform administration builds impersonation out of four controls — a second operator's
///         approval for a production tenant, a 60-minute box, an audit record, and <i>"the tenant sees
///         a notification"</i>. Every one of them is a property of the <b>grant</b>, and every one is
///         defeated by a caller who can name an operator themselves: the approval is skipped, the box
///         is unbounded, the audit names whoever was typed, and the notification either never fires or
///         accuses the wrong person.
///     </para>
///     <para>
///         ⚠ <b>docs/plan/06 § Platform administration says the value rides in an
///         <c>X-CyberCloud-Impersonated-By</c> header, and at this edge that sentence is a doc
///         defect.</b> The header is right on the internal hop — gateway to resource manager, where
///         <c>CallerContext.ImpersonatedBy</c> already is — and wrong on the one the public writes to.
///         These tests spell the header on the request and assert it changes nothing.
///     </para>
/// </remarks>
public sealed class ImpersonationAndSubjectTypeTests {
    /// <summary>The header docs/plan/06 § Platform administration names, spelled exactly.</summary>
    const string ImpersonatedByHeader = "X-CyberCloud-Impersonated-By";

    static readonly Guid Operator = Guid.Parse("9c1e7b40-3f2a-4d58-9a6c-8b2d5e0f1a34");

    [Fact]
    public async Task TheImpersonationHeaderCannotInjectAnOperator() {
        var gateway = new GatewayHarness();

        var response = await gateway.SendAsync(
            "GET",
            GatewayHarness.ResourcePath(GatewayHarness.TenantA),
            gateway.Token(GatewayHarness.TenantA),
            headers: (ImpersonatedByHeader, Operator.ToString("D"))
        );

        // ⚠ The request is NOT refused, and that is deliberate. Refusing would make the header a
        // denial-of-service lever any caller could pull against any request. It is ignored, which is
        // what "only the Authorization header is read" means in practice.
        response.Status.ShouldBe(StatusCodes.Status200OK);

        var caller = gateway.Manager.Callers.ShouldHaveSingleItem();

        caller.ImpersonatedBy.ShouldBeEmpty(
            "the operator behind a request comes from the token's `act_sub` claim or from nowhere — a "
            + "header would let any caller write any operator into the audit log."
        );
    }

    [Theory]
    [InlineData("x-cybercloud-impersonated-by")]
    [InlineData("X-CYBERCLOUD-IMPERSONATED-BY")]
    [InlineData("act_sub")]
    [InlineData("Impersonated-By")]
    public async Task NoSpellingOfTheHeaderIsRead(string header) {
        // Header names are case-insensitive, so a check that matched one casing would be defeated by
        // another. Nothing reads any of them, which is stronger than matching all of them.
        var gateway = new GatewayHarness();

        var response = await gateway.SendAsync(
            "GET",
            GatewayHarness.ResourcePath(GatewayHarness.TenantA),
            gateway.Token(GatewayHarness.TenantA),
            headers: (header, Operator.ToString("D"))
        );

        response.Status.ShouldBe(StatusCodes.Status200OK);
        gateway.Manager.Callers.ShouldHaveSingleItem().ImpersonatedBy.ShouldBeEmpty();
    }

    [Fact]
    public async Task AnImpersonationHeaderCannotOverrideAMintedOne() {
        // The nastier shape: the caller holds a genuinely impersonated token and tries to relabel who
        // is behind it. The minted value has to win, or the audit record is attacker-controlled in
        // exactly the case where it matters most.
        var gateway = new GatewayHarness();

        var response = await gateway.SendAsync(
            "GET",
            GatewayHarness.ResourcePath(GatewayHarness.TenantA),
            gateway.Token(GatewayHarness.TenantA, impersonatedBy: Operator.ToString("N")),
            headers: (ImpersonatedByHeader, Guid.Empty.ToString("D"))
        );

        response.Status.ShouldBe(StatusCodes.Status200OK);
        gateway.Manager.Callers.ShouldHaveSingleItem().ImpersonatedBy.ShouldBe(Operator.ToString("N"));
    }

    [Fact]
    public async Task AMintedImpersonationClaimReachesTheAuditableCaller() {
        var gateway = new GatewayHarness();

        var response = await gateway.SendAsync(
            "GET",
            GatewayHarness.ResourcePath(GatewayHarness.TenantA),
            gateway.Token(GatewayHarness.TenantA, impersonatedBy: Operator.ToString("N"))
        );

        response.Status.ShouldBe(StatusCodes.Status200OK);

        var caller = gateway.Manager.Callers.ShouldHaveSingleItem();

        // ⚠ The subject stays the tenant's user and the operator is the actor behind it. Collapsing
        // the two would make the audit trail say the tenant's own user did whatever support did,
        // which is the outcome "the tenant sees a notification" exists to prevent.
        caller.ImpersonatedBy.ShouldBe(Operator.ToString("N"));
        caller.SubjectId.ShouldBe("user-1");
        caller.TenantId.ShouldBe(GatewayHarness.TenantA);
    }

    // ── The subject type ─────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("user")]
    [InlineData("servicePrincipal")]
    [InlineData("managedIdentity")]
    public async Task TheSubjectTypeReachesTheCallerAsItsOwnField(string subjectType) {
        var gateway = new GatewayHarness();

        var response = await gateway.SendAsync(
            "GET",
            GatewayHarness.ResourcePath(GatewayHarness.TenantA),
            gateway.Token(GatewayHarness.TenantA, "subject-1", subjectType)
        );

        response.Status.ShouldBe(StatusCodes.Status200OK);

        var caller = gateway.Manager.Callers.ShouldHaveSingleItem();

        caller.SubjectType.ShouldBe(subjectType);
        caller.SubjectId.ShouldBe("subject-1");
    }

    [Fact]
    public void SubIsNotParseableIntoATypedSubject() {
        // ⚠ THE ASSERTION THAT THE PREFIX CONVENTION IS NOT SECRETLY BACK. `SubjectRef` is built from
        // a (type, id) PAIR, and the id alone contains no separator to recover a type from —
        // docs/plan/07 § The model makes user:abc and servicePrincipal:abc two different subjects, so
        // a gateway holding only the id would be guessing which one it had.
        var caller = new CallerContext {
            TenantId = GatewayHarness.TenantA,
            SubjectType = "servicePrincipal",
            SubjectId = "abc"
        };

        caller.SubjectId.ShouldNotContain(":");

        // The two fields are separate, and the rendering that joins them is a rendering — nothing
        // reads the type back out of `SubjectId`.
        caller.ToString().ShouldBe($"servicePrincipal:abc@{GatewayHarness.TenantA:D}");

        var sameIdOtherType = caller with { SubjectType = "user" };

        sameIdOtherType.SubjectId.ShouldBe(caller.SubjectId);
        sameIdOtherType.ToString().ShouldNotBe(caller.ToString());
    }
}

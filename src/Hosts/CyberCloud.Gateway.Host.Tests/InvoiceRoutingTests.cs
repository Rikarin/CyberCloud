using CyberCloud.Billing.Contracts;
using CyberCloud.Gateway.Host.Tests.Infrastructure;
using System.Text.Json;

namespace CyberCloud.Gateway.Host.Tests;

/// <summary>
///     The invoices' route through the whole pipeline — docs/plan/22 § What is owed,
///     <c>billing-http-surface</c>, issue #41.
/// </summary>
/// <remarks>
///     ⚠ The grain behind <c>IInvoiceReader</c> — the <c>read</c> check on the tenant — is
///     <c>CyberCloud.Billing.Tests.InvoiceVisibilityTests</c>'s to prove. These assert the gateway's
///     half: the address under the cost query's namespace, the caller and the token's tenant that
///     reach the seam, the verb, and the rendered document.
/// </remarks>
public sealed class InvoiceRoutingTests {
    static string Invoices(Guid tenant) => new InvoiceAddress(tenant, string.Empty).Path;

    [Fact]
    public async Task AGetListsTheTenantsInvoicesWithTheirLinesForTheTokensCaller() {
        var gateway = new GatewayHarness();

        var response = await gateway.SendAsync("GET", Invoices(GatewayHarness.TenantA), gateway.Token(GatewayHarness.TenantA, "alice"));

        response.Status.ShouldBe(StatusCodes.Status200OK, response.Body);

        var (tenant, caller, number) = gateway.Invoices.Calls.ShouldHaveSingleItem();
        tenant.ShouldBe(GatewayHarness.TenantA);
        caller.SubjectType.ShouldBe("user");
        caller.SubjectId.ShouldBe("alice");
        number.ShouldBeEmpty();

        using var document = JsonDocument.Parse(response.Body);
        var invoice = document.RootElement.GetProperty("value").EnumerateArray().ShouldHaveSingleItem();
        invoice.GetProperty("number").GetString().ShouldBe("CC-INV-00000042");
        invoice.GetProperty("status").GetString().ShouldBe("finalized");
        invoice.GetProperty("periodStart").GetString().ShouldBe("2026-08-01T00:00:00.0000000+00:00");
        invoice.GetProperty("total").GetDecimal().ShouldBe(3.03m);
        invoice.GetProperty("tax").GetProperty("treatment").GetString().ShouldBe("standard");
        invoice.GetProperty("tax").GetProperty("amount").GetDecimal().ShouldBe(0.53m);

        var line = invoice.GetProperty("lines").EnumerateArray().ShouldHaveSingleItem();
        line.GetProperty("meter").GetString().ShouldBe("VCpuHours");
        line.GetProperty("quantity").GetDecimal().ShouldBe(100m);
        line.GetProperty("amount").GetDecimal().ShouldBe(2.50m);
        line.GetProperty("subscriptionId").GetString().ShouldBe(GatewayHarness.Subscription.ToString("D"));

        gateway.Manager.Paths.ShouldBeEmpty("the resource manager was reached for an invoice read");
        gateway.Scopes.Paths.ShouldBeEmpty("the scope manager was reached for an invoice read");
        gateway.Costs.Calls.ShouldBeEmpty("the cost query was reached for an invoice read");
    }

    [Fact]
    public async Task AGetWithANumberReadsThatInvoice() {
        var gateway = new GatewayHarness();

        var response = await gateway.SendAsync(
            "GET",
            new InvoiceAddress(GatewayHarness.TenantA, "CC-INV-00000042").Path,
            gateway.Token(GatewayHarness.TenantA)
        );

        response.Status.ShouldBe(StatusCodes.Status200OK, response.Body);
        gateway.Invoices.Calls.ShouldHaveSingleItem().Number.ShouldBe("CC-INV-00000042");

        using var document = JsonDocument.Parse(response.Body);
        document.RootElement.GetProperty("customer").GetProperty("legalName").GetString().ShouldBe("Firma s.r.o.");
    }

    /// <summary>⚠ The seam's refusal — a caller who may not read the tenant, or a number that isn't there — is the caller's 404.</summary>
    [Fact]
    public async Task AnAbsentNumberIsTheSeamsNotFound() {
        var gateway = new GatewayHarness();

        var response = await gateway.SendAsync(
            "GET",
            new InvoiceAddress(GatewayHarness.TenantA, "CC-INV-00000099").Path,
            gateway.Token(GatewayHarness.TenantA)
        );

        response.Status.ShouldBe(StatusCodes.Status404NotFound);
    }

    [Fact]
    public async Task AListTheSeamRefusesIsA404() {
        var gateway = new GatewayHarness();
        gateway.Invoices.OnList = static () => Result<System.Collections.Immutable.ImmutableArray<Invoice>>.Failure(
            ErrorCode.ResourceNotFound,
            $"'{new InvoiceAddress(GatewayHarness.TenantA, string.Empty).Path}' does not exist."
        );

        var response = await gateway.SendAsync("GET", Invoices(GatewayHarness.TenantA), gateway.Token(GatewayHarness.TenantA, "mallory"));

        response.Status.ShouldBe(StatusCodes.Status404NotFound);
    }

    [Fact]
    public async Task APathNamingAnotherTenantIsTheTokensTenantOrNothing() {
        var gateway = new GatewayHarness();

        var response = await gateway.SendAsync("GET", Invoices(GatewayHarness.TenantB), gateway.Token(GatewayHarness.TenantA));

        response.Status.ShouldBe(StatusCodes.Status404NotFound, "stage 3 refuses a path in another tenant before routing");
        gateway.Invoices.Calls.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    public async Task AWriteIsA405ThatNamesTheVerb(string method) {
        var gateway = new GatewayHarness();

        var response = await gateway.SendAsync(method, Invoices(GatewayHarness.TenantA), gateway.Token(GatewayHarness.TenantA), body: "{}");

        response.Status.ShouldBe(StatusCodes.Status405MethodNotAllowed, response.Body);
        response.Header("Allow").ShouldBe("GET");
        gateway.Invoices.Calls.ShouldBeEmpty();
    }

    /// <summary>
    ///     ⚠ Invoices are a tenant's; on a subscription the namespace's refusal answers, and it names the
    ///     invoices' address so a caller can find it.
    /// </summary>
    [Fact]
    public async Task InvoicesOnASubscriptionAreA400ThatNamesTheTenantAddress() {
        var gateway = new GatewayHarness();

        var response = await gateway.SendAsync(
            "GET",
            ScopeId.Subscription(GatewayHarness.TenantA, GatewayHarness.Subscription).Path + InvoiceAddress.Suffix,
            gateway.Token(GatewayHarness.TenantA)
        );

        response.Status.ShouldBe(StatusCodes.Status400BadRequest);
        response.Body.ShouldContain(InvoiceAddress.Suffix);
        gateway.Invoices.Calls.ShouldBeEmpty();
    }
}

using CyberCloud.Core.Resources;
using Shouldly;

namespace CyberCloud.Core.Tests;

/// <summary>
///     <see cref="InvoiceAddress" /> — a tenant's invoices under the cost query's reserved namespace,
///     issue #41.
/// </summary>
public class InvoiceAddressTests {
    static readonly Guid Tenant = Guid.Parse("2b4a1c66-2e70-4a9d-9d0a-1f7ec1f1a4b3");
    static readonly Guid Subscription = Guid.Parse("7f2d4e88-1a3b-4c5d-8e9f-0a1b2c3d4e5f");

    static string Fill(string template) =>
        template.Replace("{t}", Tenant.ToString("D"), StringComparison.Ordinal)
            .Replace("{s}", Subscription.ToString("D"), StringComparison.Ordinal);

    [Fact]
    public void TheCollectionAndOneInvoiceRoundTrip() {
        foreach (var address in new[] { new InvoiceAddress(Tenant, string.Empty), new InvoiceAddress(Tenant, "CC-INV-00000042") }) {
            CostQueryAddress.IsUnderNamespace(address.Path).ShouldBeTrue("the router finds it by the cost query's namespace test");
            InvoiceAddress.TryParsePath(address.Path, out var parsed).ShouldBeTrue(address.Path);
            parsed.ShouldBe(address);
        }

        new InvoiceAddress(Tenant, string.Empty).Path.ShouldBe($"/tenants/{Tenant:D}/providers/CyberCloud.CostManagement/invoices");
    }

    [Fact]
    public void TheNamespaceIsRecognisedCaseInsensitively() {
        InvoiceAddress.TryParsePath(Fill("/Tenants/{t}/Providers/cybercloud.costmanagement/Invoices/CC-INV-1"), out var parsed).ShouldBeTrue();

        parsed.Number.ShouldBe("CC-INV-1");
        parsed.IsCollection.ShouldBeFalse();
    }

    [Theory]
    // Invoices are the tenant's billing account's: no subscription, group or management group has any.
    [InlineData("/tenants/{t}/subscriptions/{s}/providers/CyberCloud.CostManagement/invoices")]
    [InlineData("/tenants/{t}/subscriptions/{s}/resourceGroups/prod/providers/CyberCloud.CostManagement/invoices")]
    [InlineData("/tenants/{t}/managementGroups/finance/providers/CyberCloud.CostManagement/invoices")]
    // A trailing slash, two segments after the type, a number that could not be printed, and a longer type.
    [InlineData("/tenants/{t}/providers/CyberCloud.CostManagement/invoices/")]
    [InlineData("/tenants/{t}/providers/CyberCloud.CostManagement/invoices/CC-INV-1/lines")]
    [InlineData("/tenants/{t}/providers/CyberCloud.CostManagement/invoices/CC%2FINV")]
    [InlineData("/tenants/{t}/providers/CyberCloud.CostManagement/invoices/CC INV")]
    [InlineData("/tenants/{t}/providers/CyberCloud.CostManagement/invoicesx")]
    // The cost query itself.
    [InlineData("/tenants/{t}/subscriptions/{s}/providers/CyberCloud.CostManagement/query")]
    public void EveryOtherShapeIsNotAnInvoiceAddress(string template) =>
        InvoiceAddress.TryParsePath(Fill(template), out _).ShouldBeFalse(Fill(template));

    [Fact]
    public void ANumberLongerThanTheLimitIsRefused() {
        var path = new InvoiceAddress(Tenant, string.Empty).Path + "/" + new string('1', InvoiceAddress.MaxNumberLength + 1);

        InvoiceAddress.TryParsePath(path, out _).ShouldBeFalse();
    }

    /// <summary>⚠ The two grammars under the namespace never both claim a path.</summary>
    [Theory]
    [InlineData("/tenants/{t}/providers/CyberCloud.CostManagement/invoices")]
    [InlineData("/tenants/{t}/providers/CyberCloud.CostManagement/invoices/CC-INV-00000001")]
    public void AnInvoiceAddressIsNotACostQuery(string template) =>
        CostQueryAddress.ParsePath(Fill(template)).IsFailure.ShouldBeTrue();

    [Fact]
    public void TheGatewaysRebuildReplacesThePathsTenant() {
        var other = Guid.Parse("99999999-9999-4999-8999-999999999999");

        var rebuilt = new InvoiceAddress(Tenant, "CC-INV-1").WithTenant(other);

        rebuilt.TenantId.ShouldBe(other);
        rebuilt.Number.ShouldBe("CC-INV-1");
    }
}

using Microsoft.Extensions.Configuration;

namespace CyberCloud.Billing;

/// <summary>The billing module's host configuration — <c>CyberCloud:Billing</c>.</summary>
/// <remarks>
///     ⚠ <b>The issuer has no default, and an unconfigured silo computes no invoice.</b> An invoice is
///     a legal document naming a legal entity and its VAT number, and its tax is decided from the
///     issuer's country; a silo that printed a placeholder entity would be issuing documents on behalf
///     of nobody. Cost queries and budgets price usage without taxing it and work without an issuer;
///     a draft and a finalization refuse and name the section to set.
/// </remarks>
public sealed class BillingOptions {
    /// <summary>The configuration section.</summary>
    public const string SectionName = "CyberCloud:Billing";

    /// <summary>The entity every invoice and credit note is issued by.</summary>
    public InvoiceIssuer Issuer { get; init; } = new();

    /// <summary>Whether an issuer has been configured.</summary>
    public bool HasIssuer =>
        Issuer.Code.Length > 0 && Issuer.NumberPrefix.Length > 0 && Issuer.Country.Length == 2 && Issuer.LegalName.Length > 0;

    /// <summary>Reads <c>CyberCloud:Billing:Issuer</c>.</summary>
    /// <param name="configuration">The host's configuration.</param>
    public static BillingOptions Bind(IConfiguration configuration) {
        ArgumentNullException.ThrowIfNull(configuration);

        var issuer = configuration.GetSection(SectionName + ":Issuer");

        return new() {
            Issuer = new() {
                Code = issuer["Code"] ?? string.Empty,
                LegalName = issuer["LegalName"] ?? string.Empty,
                Country = issuer["Country"] ?? string.Empty,
                VatId = issuer["VatId"] ?? string.Empty,
                NumberPrefix = issuer["NumberPrefix"] ?? string.Empty
            }
        };
    }
}

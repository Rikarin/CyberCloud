using CyberCloud.Billing.Contracts;
using System.Buffers;
using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace CyberCloud.Gateway.Host.Http;

/// <summary>
///     An invoice as the invoices address answers it, and the list of them. docs/plan/22 § What is
///     owed, <c>billing-http-surface</c>, issue #41.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Every figure is the stored document's, unrounded again by nobody.</b> A line's amount
///         was rounded once when the invoice was finalized (<c>MoneyRounding</c>, rule 2), and a JSON
///         number carries the <see cref="decimal" /> exactly, so what the portal prints is what the
///         customer was charged.
///     </para>
///     <para>
///         ⚠ <b>The list is the collection envelope with no <c>nextLink</c></b>: a tenant has twelve
///         invoices a year, all in one grain's state (docs/plan/22 § What is owed,
///         <c>invoices-in-one-grain</c>), so a page would be a cursor over an array already in memory.
///         Each list item carries its lines, which is what the portal's list shows when a row opens.
///     </para>
/// </remarks>
static class InvoiceBody {
    /// <summary>The body spellings of <see cref="TaxTreatment" />, in enum order after <c>Unknown</c>.</summary>
    public static ImmutableArray<string> TreatmentValues { get; } = ["standard", "reverseCharge", "outOfScope"];

    /// <summary>The <c>{ "value": [ … ] }</c> list, in the order the reader answered.</summary>
    /// <param name="invoices">What the invoice reader answered.</param>
    public static string RenderList(ImmutableArray<Invoice> invoices) =>
        Write(writer => {
                writer.WriteStartObject();
                writer.WritePropertyName("value");
                writer.WriteStartArray();

                foreach (var invoice in invoices) {
                    WriteInvoice(writer, invoice);
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
            }
        );

    /// <summary>One invoice.</summary>
    /// <param name="invoice">What the invoice reader answered.</param>
    public static string Render(Invoice invoice) {
        ArgumentNullException.ThrowIfNull(invoice);
        return Write(writer => WriteInvoice(writer, invoice));
    }

    static void WriteInvoice(Utf8JsonWriter writer, Invoice invoice) {
        writer.WriteStartObject();
        writer.WriteString("number", invoice.Number);
        writer.WriteString("status", invoice.Status == InvoiceStatus.Finalized ? "finalized" : "draft");
        writer.WriteString("periodStart", Stamp(invoice.PeriodStart));
        writer.WriteString("periodEnd", Stamp(invoice.PeriodEnd));

        if (invoice.FinalizedAt is { } finalizedAt) {
            writer.WriteString("finalizedAt", Stamp(finalizedAt));
        }

        writer.WriteString("currency", invoice.Currency);
        writer.WriteNumber("subtotal", invoice.Subtotal);
        writer.WriteNumber("total", invoice.Total);

        writer.WriteStartObject("tax");
        writer.WriteString(
            "treatment",
            invoice.Tax.Treatment == TaxTreatment.Unknown ? "unknown" : TreatmentValues[(int)invoice.Tax.Treatment - 1]
        );
        writer.WriteNumber("ratePercent", invoice.Tax.RatePercent);
        writer.WriteNumber("amount", invoice.Tax.Amount);
        writer.WriteString("country", invoice.Tax.Country);
        writer.WriteString("note", invoice.Tax.Note);
        writer.WriteEndObject();

        writer.WriteStartObject("issuer");
        writer.WriteString("legalName", invoice.Issuer.LegalName);
        writer.WriteString("country", invoice.Issuer.Country);
        writer.WriteString("vatId", invoice.Issuer.VatId);
        writer.WriteEndObject();

        writer.WriteStartObject("customer");
        writer.WriteString("legalName", invoice.Customer.LegalName);
        writer.WriteString("country", invoice.Customer.Country);
        writer.WriteString("vatId", invoice.Customer.VatId);
        writer.WriteEndObject();

        writer.WritePropertyName("lines");
        writer.WriteStartArray();

        foreach (var line in invoice.Lines.IsDefault ? [] : invoice.Lines) {
            writer.WriteStartObject();
            writer.WriteString("subscriptionId", line.SubscriptionId.ToString("D", CultureInfo.InvariantCulture));
            writer.WriteString("meter", line.Meter.ToString());
            writer.WriteString("description", line.Description);
            writer.WriteString("unit", line.Unit);
            writer.WriteNumber("quantity", line.Quantity);
            writer.WriteNumber("amount", line.Amount);
            writer.WriteBoolean("declaredQuantity", line.DeclaredQuantity);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();

        writer.WritePropertyName("notes");
        writer.WriteStartArray();

        foreach (var note in invoice.Notes.IsDefault ? [] : invoice.Notes) {
            writer.WriteStringValue(note);
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    static string Write(Action<Utf8JsonWriter> body) {
        var buffer = new ArrayBufferWriter<byte>(1024);

        using (var writer = new Utf8JsonWriter(buffer)) {
            body(writer);
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    static string Stamp(DateTimeOffset at) => at.ToString("O", CultureInfo.InvariantCulture);
}

using System.Collections.Immutable;
using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CyberCloud.Billing.Payments;

/// <summary>Where the Stripe adapter talks to, and with what.</summary>
/// <remarks>
///     ⚠ <b>Both secrets arrive at run time and neither has a default.</b> The API key and the
///     webhook signing secret are the two credentials that move money or forge a payment, so they come
///     from the host — the vault in production, a generated value in a test — and nothing in the
///     repository holds one. A <see cref="StripeOptions" /> with an empty key is refused by the
///     adapter's constructor.
/// </remarks>
public sealed class StripeOptions {
    /// <summary>Stripe's API — or a <c>stripe/stripe-mock</c> in a test.</summary>
    public Uri BaseAddress { get; init; } = new("https://api.stripe.com/");

    /// <summary>The secret API key, <c>sk_live_…</c> or <c>sk_test_…</c>. ⚠ Never logged, never stored in grain state.</summary>
    public string ApiKey { get; init; } = string.Empty;

    /// <summary>The webhook endpoint's signing secret, <c>whsec_…</c>.</summary>
    public string WebhookSigningSecret { get; init; } = string.Empty;

    /// <summary>
    ///     How old a signed webhook may be. Five minutes, Stripe's own default — the bound on how long a
    ///     captured request can be replayed.
    /// </summary>
    public TimeSpan WebhookTolerance { get; init; } = TimeSpan.FromMinutes(5);
}

/// <summary>
///     <see cref="IPaymentServiceProvider" /> over Stripe's REST API — form-encoded requests, JSON
///     answers, one <see cref="HttpClient" />.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>No Stripe SDK, deliberately.</b> The surface used is five endpoints and one HMAC, and
///         the SDK would be a large dependency to license-scan (ADR-011) and keep current for that.
///         What the SDK would have given — request shapes that Stripe accepts — is what
///         <c>StripePaymentServiceProviderTests</c> proves against <c>stripe/stripe-mock</c>, which
///         validates every parameter against Stripe's published OpenAPI document and refuses the ones
///         it does not know.
///     </para>
///     <para>
///         ⚠ <b>The API version is pinned by header</b> (<see cref="ApiVersion" />), so an account
///         upgraded in Stripe's dashboard does not change the shape of what this code parses.
///     </para>
///     <para>
///         ⚠ <b>What stripe-mock cannot prove</b>, stated so it is not assumed: it is stateless, so a
///         setup intent it "creates" is not one it can later confirm, a payment method is not attached
///         to anything, and a charge never declines. The tests prove the requests are ones Stripe
///         accepts and the answers are parsed; a live test-mode account proves the flow, and that is
///         docs/plan/22 § What is owed, <c>psp-flow-against-test-mode</c>.
///     </para>
/// </remarks>
public sealed class StripePaymentServiceProvider : IPaymentServiceProvider {
    /// <summary>The Stripe API version every request pins.</summary>
    public const string ApiVersion = "2024-06-20";

    /// <summary>The header a webhook's signature arrives in.</summary>
    public const string SignatureHeader = "Stripe-Signature";

    readonly HttpClient http;
    readonly StripeOptions options;

    /// <summary>An adapter over an <see cref="HttpClient" /> the caller owns.</summary>
    /// <param name="http">The client. Its base address is replaced with <see cref="StripeOptions.BaseAddress" />.</param>
    /// <param name="options">Where and with what key.</param>
    /// <exception cref="ArgumentException">The API key is empty.</exception>
    public StripePaymentServiceProvider(HttpClient http, StripeOptions options) {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(options);

        if (string.IsNullOrWhiteSpace(options.ApiKey)) {
            throw new ArgumentException(
                "The Stripe adapter needs an API key, and the repository holds none by design — the host "
                + "supplies it from the vault.",
                nameof(options)
            );
        }

        this.http = http;
        this.options = options;
        http.BaseAddress = options.BaseAddress;
    }

    /// <inheritdoc />
    public async Task<Result<PspCustomer>> CreateCustomerAsync(
        Guid tenantId,
        string legalName,
        string email,
        CancellationToken cancellationToken = default
    ) {
        var answer = await SendAsync(
            HttpMethod.Post,
            "v1/customers",
            [
                new("name", legalName),
                new("email", email),
                new("metadata[tenant_id]", tenantId.ToString("D", CultureInfo.InvariantCulture))
            ],
            idempotencyKey: CustomerIdempotencyKey(tenantId, legalName, email),
            cancellationToken
        );

        return answer.TryGetError(out var error)
            ? Result<PspCustomer>.Failure(error)
            : Result<PspCustomer>.Success(new(Text(answer.GetValueOrThrow(), "id"), Text(answer.GetValueOrThrow(), "email")));
    }

    /// <summary>The idempotency key a customer creation is sent with: the tenant and a digest of what's sent.</summary>
    /// <param name="tenantId">The billing account.</param>
    /// <param name="legalName">The name sent.</param>
    /// <param name="email">The email sent.</param>
    /// <remarks>
    ///     ⚠ <b>The parameters are in the key because Stripe compares them.</b> A key reused with
    ///     different parameters inside Stripe's 24 hours is refused, so the first version's
    ///     <c>customer-{tenant}</c> turned a corrected email into an error for a day. A retry of the same
    ///     request still collapses. ⚠ Neither key stops a second customer for one tenant past the
    ///     24 hours: that takes storing the id this returns and never creating again, which is the host
    ///     wiring docs/plan/22 § What is owed lists as <c>psp-host-wiring-and-webhook-endpoint</c>.
    ///     stripe-mock is stateless and can't show either case.
    /// </remarks>
    public static string CustomerIdempotencyKey(Guid tenantId, string legalName, string email) {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(legalName + "\n" + email));
        return string.Create(CultureInfo.InvariantCulture, $"customer-{tenantId:N}-{Convert.ToHexStringLower(digest.AsSpan(0, 8))}");
    }

    /// <inheritdoc />
    public async Task<Result<PspSetupIntent>> CreateSetupIntentAsync(
        string customerId,
        CancellationToken cancellationToken = default
    ) {
        var answer = await SendAsync(
            HttpMethod.Post,
            "v1/setup_intents",
            [new("customer", customerId), new("usage", "off_session"), new("payment_method_types[]", "card")],
            idempotencyKey: null,
            cancellationToken
        );

        return answer.TryGetError(out var error)
            ? Result<PspSetupIntent>.Failure(error)
            : Result<PspSetupIntent>.Success(SetupIntent(answer.GetValueOrThrow()));
    }

    /// <inheritdoc />
    public async Task<Result<PspSetupIntent>> GetSetupIntentAsync(
        string setupIntentId,
        CancellationToken cancellationToken = default
    ) {
        var answer = await SendAsync(HttpMethod.Get, "v1/setup_intents/" + Uri.EscapeDataString(setupIntentId), [], null, cancellationToken);

        return answer.TryGetError(out var error)
            ? Result<PspSetupIntent>.Failure(error)
            : Result<PspSetupIntent>.Success(SetupIntent(answer.GetValueOrThrow()));
    }

    /// <inheritdoc />
    public async Task<Result<ImmutableArray<PspPaymentMethod>>> ListPaymentMethodsAsync(
        string customerId,
        CancellationToken cancellationToken = default
    ) {
        var answer = await SendAsync(
            HttpMethod.Get,
            "v1/customers/" + Uri.EscapeDataString(customerId) + "/payment_methods?type=card",
            [],
            null,
            cancellationToken
        );

        if (answer.TryGetError(out var error)) {
            return Result<ImmutableArray<PspPaymentMethod>>.Failure(error);
        }

        var methods = ImmutableArray.CreateBuilder<PspPaymentMethod>();

        if (answer.GetValueOrThrow().TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array) {
            foreach (var method in data.EnumerateArray()) {
                var summary = method.TryGetProperty("card", out var card) && card.ValueKind == JsonValueKind.Object
                    ? $"{Text(card, "brand")} ending {Text(card, "last4")}"
                    : Text(method, "type");

                methods.Add(new(Text(method, "id"), Text(method, "type"), summary));
            }
        }

        return Result<ImmutableArray<PspPaymentMethod>>.Success(methods.ToImmutable());
    }

    /// <inheritdoc />
    public async Task<Result<PspPayment>> PayInvoiceAsync(
        PspPaymentRequest request,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(request);

        if (request.AmountMinor <= 0) {
            return Result<PspPayment>.Failure(
                ErrorCode.InvalidRequestBody,
                "A charge is for a positive amount. A zero invoice is settled by being issued, and a negative "
                + "one is a credit note, which is refunded rather than charged."
            );
        }

        if (string.IsNullOrWhiteSpace(request.IdempotencyKey)) {
            return Result<PspPayment>.Failure(
                ErrorCode.InvalidRequestBody,
                "A charge carries an idempotency key derived from the invoice; without one a retry after a timeout "
                + "charges twice."
            );
        }

        var answer = await SendAsync(
            HttpMethod.Post,
            "v1/payment_intents",
            [
                new("amount", request.AmountMinor.ToString(CultureInfo.InvariantCulture)),
                // ⚠ Lower case: Stripe's currency parameter is an ISO code in lower case, and it refuses
                // "EUR" in some endpoints. Upper case is this platform's spelling everywhere else.
                new("currency", request.Currency.ToLowerInvariant()),
                new("customer", request.CustomerId),
                new("payment_method", request.PaymentMethodId),
                new("off_session", "true"),
                new("confirm", "true"),
                new("description", "Invoice " + request.InvoiceNumber),
                new("metadata[invoice_number]", request.InvoiceNumber)
            ],
            request.IdempotencyKey,
            cancellationToken
        );

        if (answer.TryGetError(out var error)) {
            return Result<PspPayment>.Failure(error);
        }

        var intent = answer.GetValueOrThrow();

        return Result<PspPayment>.Success(
            new(
                Text(intent, "id"),
                Text(intent, "status"),
                intent.TryGetProperty("amount", out var amount) && amount.TryGetInt64(out var minor) ? minor : 0,
                Text(intent, "currency").ToUpperInvariant()
            )
        );
    }

    /// <inheritdoc />
    /// <remarks>
    ///     <para>
    ///         Stripe's scheme: the header is <c>t={unix seconds},v1={hex},…</c>; the signed payload is
    ///         <c>{t}.{body}</c>; the signature is HMAC-SHA256 under the endpoint's signing secret. Any
    ///         one <c>v1</c> matching is enough — Stripe sends two while a secret is being rolled.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The comparison is constant-time and the age is checked after it.</b> A byte-by-byte
    ///         early exit would let a caller learn a valid signature a byte at a time; checking the age
    ///         first would answer "too old" to an attacker who had not yet produced a valid signature,
    ///         which says nothing useful and is still one more oracle than necessary.
    ///     </para>
    /// </remarks>
    public Result<PspWebhookEvent> VerifyWebhook(string payload, string signatureHeader, DateTimeOffset now) {
        ArgumentNullException.ThrowIfNull(payload);

        if (string.IsNullOrWhiteSpace(options.WebhookSigningSecret)) {
            return Refused("No webhook signing secret is configured, so no webhook can be believed.");
        }

        long? timestamp = null;
        var signatures = new List<byte[]>();

        foreach (var part in (signatureHeader ?? string.Empty).Split(',', StringSplitOptions.TrimEntries)) {
            var equals = part.IndexOf('=', StringComparison.Ordinal);
            if (equals <= 0) {
                continue;
            }

            var name = part[..equals];
            var value = part[(equals + 1)..];

            if (string.Equals(name, "t", StringComparison.Ordinal)
                && long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)) {
                timestamp = seconds;
            } else if (string.Equals(name, "v1", StringComparison.Ordinal) && TryHex(value, out var bytes)) {
                signatures.Add(bytes);
            }
        }

        if (timestamp is not { } t || signatures.Count == 0) {
            return Refused($"The {SignatureHeader} header carries no timestamp or no v1 signature.");
        }

        var expected = Sign(options.WebhookSigningSecret, t, payload);

        if (!signatures.Any(x => CryptographicOperations.FixedTimeEquals(x, expected))) {
            return Refused("No v1 signature in the header matches the payload under this endpoint's signing secret.");
        }

        var age = now - DateTimeOffset.FromUnixTimeSeconds(t);
        if (age > options.WebhookTolerance || age < -options.WebhookTolerance) {
            return Refused(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"The webhook was signed {age.TotalSeconds:0} s from now and the tolerance is {options.WebhookTolerance.TotalSeconds:0} s. A signature that old is a replay until proven otherwise."
                )
            );
        }

        try {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            var objectId = root.TryGetProperty("data", out var data)
                && data.TryGetProperty("object", out var body)
                && body.ValueKind == JsonValueKind.Object
                    ? Text(body, "id")
                    : string.Empty;

            return Result<PspWebhookEvent>.Success(
                new(
                    Text(root, "id"),
                    Text(root, "type"),
                    objectId,
                    root.TryGetProperty("created", out var created) && created.TryGetInt64(out var at)
                        ? DateTimeOffset.FromUnixTimeSeconds(at)
                        : DateTimeOffset.FromUnixTimeSeconds(t)
                )
            );
        } catch (JsonException error) {
            return Result<PspWebhookEvent>.Failure(ErrorCode.InvalidRequestBody, $"The signed webhook is not JSON: {error.Message}");
        }
    }

    /// <summary>The <c>v1</c> signature Stripe sends for a payload — what a test signs its own webhooks with.</summary>
    /// <param name="secret">The endpoint's signing secret.</param>
    /// <param name="timestamp">Unix seconds.</param>
    /// <param name="payload">The body.</param>
    public static byte[] Sign(string secret, long timestamp, string payload) {
        ArgumentNullException.ThrowIfNull(secret);
        ArgumentNullException.ThrowIfNull(payload);

        return HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(secret),
            Encoding.UTF8.GetBytes(timestamp.ToString(CultureInfo.InvariantCulture) + "." + payload)
        );
    }

    // ── The wire ─────────────────────────────────────────────────────────────────────────────────

    async Task<Result<JsonElement>> SendAsync(
        HttpMethod method,
        string path,
        IReadOnlyList<KeyValuePair<string, string>> form,
        string? idempotencyKey,
        CancellationToken cancellationToken
    ) {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);
        request.Headers.Add("Stripe-Version", ApiVersion);

        if (idempotencyKey is not null) {
            request.Headers.Add("Idempotency-Key", idempotencyKey);
        }

        if (method == HttpMethod.Post) {
            request.Content = new FormUrlEncodedContent(form);
        }

        HttpResponseMessage response;

        try {
            response = await http.SendAsync(request, cancellationToken);
        } catch (HttpRequestException error) {
            return Result<JsonElement>.Failure(
                ErrorCode.InternalError,
                $"Stripe could not be reached at {options.BaseAddress}: {error.Message}"
            );
        }

        using (response) {
            var text = await response.Content.ReadAsStringAsync(cancellationToken);
            JsonElement body;

            try {
                body = JsonDocument.Parse(text).RootElement.Clone();
            } catch (JsonException) {
                return Result<JsonElement>.Failure(
                    ErrorCode.InternalError,
                    string.Create(CultureInfo.InvariantCulture, $"Stripe answered {(int)response.StatusCode} with a body that is not JSON.")
                );
            }

            if (response.IsSuccessStatusCode) {
                return Result<JsonElement>.Success(body);
            }

            // ⚠ Stripe's own sentence, never a paraphrase: it names the parameter, and a support engineer
            // searching Stripe's documentation for it needs the words Stripe used.
            var message = body.TryGetProperty("error", out var error) ? Text(error, "message") : text;

            return Result<JsonElement>.Failure(
                (int)response.StatusCode is >= 400 and < 500 ? ErrorCode.InvalidRequestBody : ErrorCode.InternalError,
                string.Create(CultureInfo.InvariantCulture, $"Stripe refused {method} /{path} with {(int)response.StatusCode}: {message}")
            );
        }
    }

    static PspSetupIntent SetupIntent(JsonElement intent) =>
        new(Text(intent, "id"), Text(intent, "status"), Text(intent, "client_secret"), Text(intent, "payment_method"));

    static string Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : string.Empty;

    static bool TryHex(string text, out byte[] bytes) {
        try {
            bytes = Convert.FromHexString(text);
            return true;
        } catch (FormatException) {
            bytes = [];
            return false;
        }
    }

    static Result<PspWebhookEvent> Refused(string message) =>
        Result<PspWebhookEvent>.Failure(ErrorCode.AuthorizationFailed, message);
}

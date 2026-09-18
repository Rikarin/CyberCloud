using Aspire.Hosting.ApplicationModel;
using CyberCloud.Authorization.Contracts;
using CyberCloud.Identity.Contracts;
using CyberCloud.Identity.Host;
using CyberCloud.Identity.Host.Api;
using CyberCloud.Identity.Host.Tokens;
using Microsoft.Extensions.DependencyInjection;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CyberCloud.AppHost.Tests;

/// <summary>
///     The person half of the join <see cref="TenantOverHttpTests" /> made: a person with no
///     account signs up, signs in, holds a <c>cyc.api</c> token, reads their tenant through the
///     gateway, creates a resource, refreshes, and signs out — against the AppHost's own identity
///     host and gateway, the processes a developer's <c>dotnet run</c> starts.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The AppHost's processes, not in-process hosts.</b> <see cref="TenantOverHttpTests" />
///         builds its own identity host and gateway on ephemeral ports because it configures each
///         for one tenant and one vault seam. A person's path has nothing to configure per test:
///         what it needs — self-serve sign-up open, a default region, the portal's redirect URI,
///         the sign-in page's origin, one issuer, keys that survive a restart — is exactly what
///         <c>CyberCloudTopology</c> sets on the <c>identity</c> resource, and the silos'
///         <c>PlatformBootstrapTask</c> seeds the shard map and the sign-up operator's grant the
///         same way. So this file drives <c>http://localhost:5101</c> and <c>:5100</c> as the
///         portal would, and a green run says the dev run's configuration is whole, not only that
///         the code paths are.
///     </para>
///     <para>
///         ⚠
///         <b>
///             The one-time code is read from the silo's console, and checked against Mailpit's
///             inbox.
///         </b> <c>DevelopmentOtpDelivery</c> logs the code on the silo whose grain minted it
///         and — the AppHost having a relay since #93 — mails it through the platform's own
///         communication service to Mailpit, where a person reads it at
///         <c>http://localhost:8025</c>. The test reads the console through
///         <see cref="ResourceLoggerService" />, watching both silos because the sign-up grain is
///         placed on either, then reads the inbox through Mailpit's API and asserts the two agree.
///         Nothing is substituted at the delivery seam: the code that arrives is the code the grain
///         wrote, and a wrong one is answered <c>verified: false</c> first to prove the check is a
///         check.
///     </para>
///     <para>
///         ⚠ <b>Cookies are carried by hand.</b> Every cookie the identity host sets is
///         <c>__Host-</c> prefixed and therefore <c>Secure</c>, and .NET's <c>CookieContainer</c>
///         refuses to return a <c>Secure</c> cookie to an <c>http://</c> request — as curl does,
///         and as browsers on <c>http://localhost</c> do not. The test copies what
///         <c>Set-Cookie</c> named into the next <c>Cookie</c> header itself, which is also what
///         lets it assert exactly which cookie each response set and cleared.
///     </para>
///     <para>
///         ⚠ <b>The resource is a communication service, not a Sample widget.</b> The story in the
///         design named the Sample provider as "the safe one on a run where k3s is still starting",
///         and the first run found that <c>CyberCloud.Sample/widgets</c> requires a
///         <c>clusterId</c>. <c>CyberCloud.Communication/services</c> declares no cluster and its
///         reconciler reads back from grains alone, so it converges on a run with no cluster
///         attached — and it is the type the design's own dev-run story now names.
///     </para>
///     <para>
///         ⚠ <b>One test, in order, for the reason the sibling gives:</b> each case costs a topology
///         cycle, and the identity host's own suite (<c>GrantsOverHttpTests</c>, <c>SignUpApiTests</c>)
///         already sweeps the branches cheaply. What only this file can say is that a person's
///         request path is joined across three processes and the configuration between them.
///     </para>
/// </remarks>
/// <param name="topology">The running AppHost.</param>
[Collection(LocalTopologySuite.Name)]
public sealed class PersonOverHttpTests(LocalTopology topology) {
    /// <summary>How long the code is given to appear on a silo's console.</summary>
    static readonly TimeSpan CodeBudget = TimeSpan.FromSeconds(90);

    /// <summary>
    ///     How long the resource is given to converge. Its first reconcile pass is one
    ///     <c>OperationGrain.ReminderPeriod</c> away, and Orleans reminders tick at a minute.
    /// </summary>
    static readonly TimeSpan ConvergenceBudget = TimeSpan.FromMinutes(4);

    static readonly Uri IdentityHost = new(CyberCloudResources.IdentityIssuer);

    static readonly Uri Gateway =
        new($"http://localhost:{CyberCloudResources.GatewayPort.ToString(CultureInfo.InvariantCulture)}");

    const string PortalOrigin = "http://localhost:4200";
    const string Version = "?api-version=2026-08-01";
    const string Email = "person@example.com";
    const string DisplayName = "Rene";
    const string Organization = "Person Over HTTP";
    const string Slug = "person-over-http";
    const string Password = "person-over-http-correct-horse-9";

    static readonly Regex DeliveredCode = new(
        @"DEVELOPMENT OTP DELIVERY \(#93\): code (?<code>\d{6}) for user (?<user>[0-9a-f-]{36})",
        RegexOptions.CultureInvariant | RegexOptions.Compiled
    );

    /// <summary>
    ///     Sign up, sign in, read the tenant and its scopes, create a resource, refresh, sign out.
    /// </summary>
    [Fact]
    public async Task APersonSignsUpSignsInAndReadsTheirTenantThroughTheGateway() {
        var cancellationToken = TestContext.Current.CancellationToken;

        await topology.Application.ResourceNotifications.WaitForResourceHealthyAsync(
            CyberCloudResources.Identity,
            cancellationToken
        );
        await topology.Application.ResourceNotifications.WaitForResourceHealthyAsync(
            CyberCloudResources.Gateway,
            cancellationToken
        );

        using var handler = new HttpClientHandler();
        handler.AllowAutoRedirect = false;
        handler.UseCookies = false;
        using var http = new HttpClient(handler);

        // The silo consoles, watched from before the sign-up begins so the code cannot be missed.
        // ⚠ By IResource, not by name: the logger service keys its streams by the resource's
        // INSTANCE name (`silo-2-nhksxzfd`, the suffix Aspire appends per run), and the string
        // overload asked for `silo-2` reads nothing, forever, with no error. The IResource overload
        // resolves the instances itself. The first run of this test found the other overload.
        using var watching = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var console = new ConcurrentQueue<string>();
        var logs = topology.Application.Services.GetRequiredService<ResourceLoggerService>();
        var model = topology.Application.Services.GetRequiredService<DistributedApplicationModel>();
        var watchers = new[] { CyberCloudResources.SiloOne, CyberCloudResources.SiloTwo }
            .Select(silo => model.Resources.Single(resource => string.Equals(
                        resource.Name,
                        silo,
                        StringComparison.Ordinal
                    )
                )
            )
            .Select(silo => Task.Run(() => WatchAsync(logs, silo, console, watching.Token), CancellationToken.None))
            .ToArray();

        // ── Steps 1–2: the portal's /authorize with no session goes to the sign-in page. ─────────
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var state = Base64Url(RandomNumberGenerator.GetBytes(16));

        var authorize = IdentityHostOpenIddict.AuthorizationPath
            + "?response_type=code&client_id="
            + FirstPartyClients.Portal
            + "&redirect_uri="
            + Uri.EscapeDataString(FirstPartyClients.DevelopmentPortalRedirectUri)
            + "&scope="
            + Uri.EscapeDataString(string.Join(' ', FirstPartyClients.AllScopes))
            + "&state="
            + state
            + "&code_challenge="
            + challenge
            + "&code_challenge_method=S256&nonce=n-1";

        var unauthenticated = await SendAsync(
            http,
            HttpMethod.Get,
            new Uri(IdentityHost, authorize),
            null,
            null,
            cancellationToken
        );

        unauthenticated.Status.ShouldBe(HttpStatusCode.Found, unauthenticated.Body);

        var signIn = unauthenticated.Location.ShouldNotBeNull(
            "an unauthenticated /authorize redirects to the sign-in page"
        );
        signIn.GetLeftPart(UriPartial.Path)
            .ShouldBe(
                $"http://localhost:{CyberCloudResources.IdentityAppPort.ToString(CultureInfo.InvariantCulture)}/signin",
                "SignInPageBaseUri is the identity app's dev server on the AppHost"
            );

        var returnUrl = QueryValue(signIn, "returnUrl");
        returnUrl.StartsWith(IdentityHostOpenIddict.AuthorizationPath + "?", StringComparison.Ordinal)
            .ShouldBeTrue("the return URL is the same-origin path and query of the /authorize request: " + returnUrl);
        returnUrl.ShouldContain("state=" + state);

        // ── Step 4: sign-up begins; the ticket cookie is issued. ────────────────────────────────
        var begun = await SendAsync(
            http,
            HttpMethod.Post,
            new Uri(IdentityHost, "/api/signup/begin"),
            null,
            JsonSerializer.Serialize(new { email = Email, returnUrl }),
            cancellationToken
        );

        begun.Status.ShouldBe(HttpStatusCode.OK, begun.Body);
        Json(begun.Body).GetProperty("sent").GetBoolean().ShouldBeTrue();

        var ticket = begun.CookieSet(SignUpTicketCookie.CookieName);
        ticket.ShouldNotBeNullOrEmpty("begin issues " + SignUpTicketCookie.CookieName);

        // ── Step 5: the code, from the silo's console — and the same code from Mailpit's inbox. ──
        var code = await ReadDeliveredCodeAsync(console, cancellationToken);

        TestContext.Current.TestOutputHelper?.WriteLine($"one-time code {code} read from the silo console");

        // ⚠ THE PART #93 ADDED. The console line is still where the test reads the code, because it
        // is where a person is told to look first; this asserts that the same code ALSO arrived
        // where a person would look second: the relay's inbox at http://localhost:8025, sent through
        // the platform's own communication service and the smtp carrier — a real SMTP submission
        // from the silo to a real SMTP server, read back through the server's API.
        var mailed = await ReadMailedCodeAsync(http, cancellationToken);
        mailed.ShouldBe(code, "the code in the inbox is the code on the console — one delivery, two places");

        // ── Step 6: a wrong code is refused, the right one is burnt. ────────────────────────────
        var wrong = await PostJsonAsync(http, "/api/signup/verify", ticket, new { code = "000000" }, cancellationToken);
        Json(wrong.Body).GetProperty("verified")
            .GetBoolean()
            .ShouldBeFalse("a wrong code is answered verified: false, not an error");

        var verified = await PostJsonAsync(http, "/api/signup/verify", ticket, new { code }, cancellationToken);
        Json(verified.Body).GetProperty("verified").GetBoolean().ShouldBeTrue(verified.Body);

        // ── Step 7: complete with a password; the tenant, user, subscription and group exist. ───
        var completed = await PostJsonAsync(
            http,
            "/api/signup/complete",
            ticket,
            new {
                displayName = DisplayName,
                organizationName = Organization,
                credential = new { kind = "password", password = Password },
                returnUrl
            },
            cancellationToken
        );

        completed.Status.ShouldBe(HttpStatusCode.OK, completed.Body);

        var completion = Json(completed.Body);
        completion.GetProperty("succeeded").GetBoolean().ShouldBeTrue(completed.Body);

        var tenantId = Guid.Parse(completion.GetProperty("tenantId").GetString()!, CultureInfo.InvariantCulture);
        var resumed = completion.GetProperty("returnUrl").GetString()!;
        resumed.Contains(
            SignUpApi.TenantParameter + "=" + tenantId.ToString("D", CultureInfo.InvariantCulture),
            StringComparison.Ordinal
        )
            .ShouldBeTrue("the resumed /authorize names the new tenant: " + resumed);

        var session = completed.CookieSet(IdentityHostAuthentication.CookieName);
        session.ShouldNotBeNullOrEmpty("complete signs the person in");
        completed.CookieCleared(SignUpTicketCookie.CookieName).ShouldBeTrue("the ticket is taken by complete");

        // ── Step 8: the resumed /authorize mints a code for the same tenant. ────────────────────
        var authorized = await SendAsync(
            http,
            HttpMethod.Get,
            new Uri(IdentityHost, resumed),
            Cookie(IdentityHostAuthentication.CookieName, session),
            null,
            cancellationToken
        );

        authorized.Status.ShouldBe(HttpStatusCode.Found, authorized.Body);

        var callback = authorized.Location.ShouldNotBeNull();
        callback.GetLeftPart(UriPartial.Path).ShouldBe(FirstPartyClients.DevelopmentPortalRedirectUri);
        QueryValue(callback, "state").ShouldBe(state);
        QueryValue(callback, "iss").ShouldBe(
            CyberCloudResources.IdentityIssuer + "/",
            "one issuer, whichever origin the request arrived on"
        );

        var authorizationCode = QueryValue(callback, "code");

        // ── Step 9: the exchange, cross-origin from the portal, with the verifier. ──────────────
        var exchanged = await PostFormAsync(
            http,
            new Dictionary<string, string>(StringComparer.Ordinal) {
                ["grant_type"] = "authorization_code",
                ["client_id"] = FirstPartyClients.Portal,
                ["redirect_uri"] = FirstPartyClients.DevelopmentPortalRedirectUri,
                ["code"] = authorizationCode,
                ["code_verifier"] = verifier
            },
            null,
            cancellationToken
        );

        exchanged.Status.ShouldBe(HttpStatusCode.OK, exchanged.Body);
        exchanged.Headers.GetValues("Access-Control-Allow-Origin").ShouldBe([PortalOrigin]);
        exchanged.Headers.GetValues("Access-Control-Allow-Credentials").ShouldBe(["true"]);

        var tokens = Json(exchanged.Body);
        tokens.TryGetProperty("refresh_token", out _)
            .ShouldBeFalse("the browser client's refresh token is moved into the cookie");
        // ⚠ Within a second of the policy, not equal to it: OpenIddict answers the seconds LEFT at
        // the moment it writes the body, and a token minted late in one second reads 599.
        tokens.GetProperty("expires_in")
            .GetInt32()
            .ShouldBeInRange(
                (int)AccessTokenPolicy.AccessTokenLifetime.TotalSeconds - 1,
                (int)AccessTokenPolicy.AccessTokenLifetime.TotalSeconds
            );

        var refresh = exchanged.CookieSet(RefreshCookie.Name);
        refresh.ShouldNotBeNullOrEmpty("the refresh token lives in " + RefreshCookie.Name);

        var accessToken = tokens.GetProperty("access_token").GetString()!;
        var payload = Payload(accessToken);

        TestContext.Current.TestOutputHelper?.WriteLine("access token payload: " + payload.GetRawText());

        payload.GetProperty(AccessTokenClaims.TenantId)
            .GetString()
            .ShouldBe(tenantId.ToString("N", CultureInfo.InvariantCulture));
        payload.GetProperty(AccessTokenClaims.SubjectType).GetString().ShouldBe(SubjectTypes.User);
        payload.GetProperty(AccessTokenClaims.Audience).GetString().ShouldBe(AccessTokenPolicy.Audience);
        payload.GetProperty(AccessTokenClaims.AuthorizedParty).GetString().ShouldBe(FirstPartyClients.Portal);

        foreach (var claim in payload.EnumerateObject()) {
            AccessTokenClaims.Permitted.ShouldContain(
                claim.Name,
                $"the person's token carries '{claim.Name}', which is outside the closed set"
            );
        }

        Payload(tokens.GetProperty("id_token").GetString()!).GetProperty("email").GetString().ShouldBe(Email);

        // ── Steps 10–11: the tenant and its scopes, through the gateway. ────────────────────────
        var tenantPath = "/tenants/" + tenantId.ToString("D", CultureInfo.InvariantCulture);

        var tenant = await GetAsync(http, tenantPath, accessToken, cancellationToken);
        tenant.Status.ShouldBe(HttpStatusCode.OK, tenant.Body);
        Json(tenant.Body).GetProperty("name").GetString().ShouldBe(Slug);

        var subscriptions = await GetAsync(http, tenantPath + "/subscriptions", accessToken, cancellationToken);
        subscriptions.Status.ShouldBe(HttpStatusCode.OK, subscriptions.Body);

        var subscription = Json(subscriptions.Body).GetProperty("value")
            .EnumerateArray()
            .ShouldHaveSingleItem("sign-up creates one subscription");
        subscription.GetProperty("name").GetString().ShouldBe("Default");
        subscription.GetProperty("type").GetString().ShouldBe("CyberCloud.Resources/subscriptions");

        var subscriptionPath = subscription.GetProperty("id").GetString()!;
        subscriptionPath.StartsWith(tenantPath + "/subscriptions/", StringComparison.Ordinal)
            .ShouldBeTrue(subscriptionPath);

        var groups = await GetAsync(http, subscriptionPath + "/resourceGroups", accessToken, cancellationToken);
        groups.Status.ShouldBe(HttpStatusCode.OK, groups.Body);

        var group = Json(groups.Body).GetProperty("value")
            .EnumerateArray()
            .ShouldHaveSingleItem("sign-up creates one resource group");
        group.GetProperty("name").GetString().ShouldBe("default");
        group.GetProperty("location").GetString().ShouldBe(CyberCloudResources.DefaultRegion);

        // ── Step 12: a resource, authored by the person's token, converges. ─────────────────────
        var resourcePath = subscriptionPath
            + "/resourceGroups/default/providers/CyberCloud.Communication/services/first";

        var accepted = await SendAsync(
            http,
            HttpMethod.Put,
            new Uri(Gateway, resourcePath + Version),
            null,
            """{"location":"local","properties":{"defaultLocale":"en"}}""",
            cancellationToken,
            accessToken
        );

        accepted.Status.ShouldBe(HttpStatusCode.Accepted, accepted.Body);

        var operation = accepted.Headers.GetValues("Azure-AsyncOperation").Single();
        var status = await PollUntilTerminalAsync(http, operation, accessToken, cancellationToken);

        status.ShouldBe("Succeeded", "the communication service did not converge");

        var created = await GetAsync(http, resourcePath, accessToken, cancellationToken);
        created.Status.ShouldBe(HttpStatusCode.OK, created.Body);
        Json(created.Body).GetProperty("provisioningState").GetString().ShouldBe("Succeeded");

        // ── Step 13: a refresh from the cookie rotates it; the retired cookie is refused. ───────
        var refreshed = await PostFormAsync(
            http,
            new Dictionary<string, string>(StringComparer.Ordinal) {
                ["grant_type"] = "refresh_token", ["client_id"] = FirstPartyClients.Portal
            },
            Cookie(RefreshCookie.Name, refresh),
            cancellationToken
        );

        refreshed.Status.ShouldBe(HttpStatusCode.OK, refreshed.Body);

        var rotated = refreshed.CookieSet(RefreshCookie.Name);
        rotated.ShouldNotBeNullOrEmpty("a refresh replaces the cookie");
        rotated.ShouldNotBe(refresh);

        var refreshedPayload = Payload(Json(refreshed.Body).GetProperty("access_token").GetString()!);
        refreshedPayload.GetProperty(AccessTokenClaims.SessionId)
            .GetString()
            .ShouldBe(
                payload.GetProperty(AccessTokenClaims.SessionId).GetString(),
                "the token session is the same chain"
            );
        refreshedPayload.GetProperty(AccessTokenClaims.AuthenticationTime)
            .GetInt64()
            .ShouldBe(
                payload.GetProperty(AccessTokenClaims.AuthenticationTime).GetInt64(),
                "auth_time is carried across refreshes"
            );

        var replayed = await PostFormAsync(
            http,
            new Dictionary<string, string>(StringComparer.Ordinal) {
                ["grant_type"] = "refresh_token", ["client_id"] = FirstPartyClients.Portal
            },
            Cookie(RefreshCookie.Name, refresh),
            cancellationToken
        );

        replayed.Status.ShouldBe(HttpStatusCode.BadRequest, replayed.Body);
        Json(replayed.Body).GetProperty("error").GetString().ShouldBe("invalid_grant");
        replayed.CookieCleared(RefreshCookie.Name).ShouldBeTrue("a refused refresh clears the cookie");

        var foreign = await PostFormAsync(
            http,
            new Dictionary<string, string>(StringComparer.Ordinal) {
                ["grant_type"] = "refresh_token", ["client_id"] = FirstPartyClients.Portal
            },
            Cookie(RefreshCookie.Name, rotated),
            cancellationToken,
            "http://evil.localhost:4200"
        );

        foreign.Status.ShouldBe(HttpStatusCode.BadRequest, foreign.Body);
        Json(foreign.Body).GetProperty("error")
            .GetString()
            .ShouldBe("invalid_request", "a foreign Origin may not present the cookie");

        // ── Step 15: sign out ends the session; the next /authorize asks again. ─────────────────
        var signedOut = await SendAsync(
            http,
            HttpMethod.Get,
            new Uri(
                IdentityHost,
                IdentityHostOpenIddict.EndSessionPath
                + "?client_id="
                + FirstPartyClients.Portal
                + "&post_logout_redirect_uri="
                + Uri.EscapeDataString(FirstPartyClients.DevelopmentPortalPostLogoutRedirectUri)
            ),
            Cookie(IdentityHostAuthentication.CookieName, session),
            null,
            cancellationToken
        );

        signedOut.Status.ShouldBe(HttpStatusCode.Found, signedOut.Body);
        signedOut.Location.ShouldNotBeNull()
            .ToString()
            .ShouldBe(FirstPartyClients.DevelopmentPortalPostLogoutRedirectUri);
        signedOut.CookieCleared(IdentityHostAuthentication.CookieName).ShouldBeTrue();
        signedOut.CookieCleared(RefreshCookie.Name).ShouldBeTrue();

        var again = await SendAsync(
            http,
            HttpMethod.Get,
            new Uri(IdentityHost, resumed),
            Cookie(IdentityHostAuthentication.CookieName, session),
            null,
            cancellationToken
        );
        again.Status.ShouldBe(HttpStatusCode.Found);
        again.Location.ShouldNotBeNull().AbsolutePath.ShouldBe("/signin", "the revoked session mints nothing");

        await watching.CancelAsync();
        await Task.WhenAll(
            watchers.Select(static async watcher => {
                    try {
                        await watcher;
                    } catch (OperationCanceledException) { }
                }
            )
        );
    }

    // ── The silo console ─────────────────────────────────────────────────────────────────────────

    static async Task WatchAsync(
        ResourceLoggerService logs,
        IResource resource,
        ConcurrentQueue<string> into,
        CancellationToken cancellationToken
    ) {
        await foreach (var batch in logs.WatchAsync(resource).WithCancellation(cancellationToken)) {
            foreach (var line in batch) {
                into.Enqueue(line.Content);
            }
        }
    }

    /// <summary>The last code either silo delivered, once one has been.</summary>
    static async Task<string> ReadDeliveredCodeAsync(
        ConcurrentQueue<string> console,
        CancellationToken cancellationToken
    ) {
        var clock = Stopwatch.StartNew();

        while (clock.Elapsed < CodeBudget) {
            var delivered = console
                .Select(static line => DeliveredCode.Match(line))
                .LastOrDefault(static match => match.Success);

            if (delivered is not null) {
                return delivered.Groups["code"].Value;
            }

            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }

        throw new InvalidOperationException(
            $"No one-time code appeared on either silo's console within {CodeBudget}. DevelopmentOtpDelivery "
            + "logs it at Warning when the silo runs in Development with no CyberCloud:Identity:OtpDelivery "
            + $"configured; {console.Count} console line(s) were read."
        );
    }

    /// <summary>
    ///     The code in the newest message Mailpit holds for <see cref="Email" />, read through its
    ///     API on <c>http://localhost:8025</c>.
    /// </summary>
    /// <remarks>
    ///     The relay answers the silo's <c>250</c> before it has indexed the message, so this polls
    ///     briefly rather than reading once. The body is the free-text OTP send —
    ///     "<i>424242</i> is your code." — and the same regex shape the console reader uses picks the
    ///     six digits out of it.
    /// </remarks>
    static async Task<string> ReadMailedCodeAsync(HttpClient http, CancellationToken cancellationToken) {
        var inbox = new Uri(
            $"http://localhost:{CyberCloudResources.MailpitHttpPort.ToString(CultureInfo.InvariantCulture)}/"
        );
        var clock = Stopwatch.StartNew();

        while (clock.Elapsed < TimeSpan.FromSeconds(30)) {
            using var listing = await http.GetAsync(
                new Uri(inbox, "api/v1/search?query=to:" + Email),
                cancellationToken
            );
            var messages = Json(await listing.Content.ReadAsStringAsync(cancellationToken)).GetProperty("messages");

            if (messages.GetArrayLength() > 0) {
                var id = messages[0].GetProperty("ID").GetString()!;
                using var message = await http.GetAsync(new Uri(inbox, "api/v1/message/" + id), cancellationToken);
                var text = Json(await message.Content.ReadAsStringAsync(cancellationToken)).GetProperty("Text")
                    .GetString()
                    ?? string.Empty;

                var digits = Regex.Match(text, @"\b(?<code>\d{6})\b", RegexOptions.CultureInvariant);
                digits.Success.ShouldBeTrue("the mailed body carries the six-digit code: " + text);

                return digits.Groups["code"].Value;
            }

            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }

        throw new InvalidOperationException(
            $"No message to {Email} reached Mailpit within 30 s. The silo's PlatformBootstrapTask writes the "
            + "platform's communication service when CyberCloud:Communication:Smtp is set, and "
            + "DevelopmentOtpDelivery mails the code through it beside logging it; a 'was NOT mailed' Warning "
            + "on the silo console says why the relay refused."
        );
    }

    // ── HTTP ─────────────────────────────────────────────────────────────────────────────────────

    /// <summary>One answer: the status, the headers, the body, and the cookies it set.</summary>
    readonly record struct Answer(HttpStatusCode Status, HttpResponseHeaders Headers, string Body) {
        public Uri? Location => Headers.Location;

        /// <summary>The value a <c>Set-Cookie</c> gave the named cookie, or <c>null</c> when none did.</summary>
        public string? CookieSet(string name) =>
            SetCookies()
                .Where(cookie => cookie.StartsWith(name + "=", StringComparison.Ordinal))
                .Select(cookie => cookie[(name.Length + 1)..].Split(';')[0])
                .FirstOrDefault(static value => value.Length > 0);

        /// <summary>Whether a <c>Set-Cookie</c> emptied the named cookie.</summary>
        public bool CookieCleared(string name) =>
            SetCookies().Any(cookie => cookie.StartsWith(name + "=;", StringComparison.Ordinal));

        IEnumerable<string> SetCookies() => Headers.TryGetValues("Set-Cookie", out var values) ? values : [];
    }

    static string Cookie(string name, string value) => name + "=" + value;

    static async Task<Answer> SendAsync(
        HttpClient http,
        HttpMethod method,
        Uri uri,
        string? cookies,
        string? body,
        CancellationToken cancellationToken,
        string? bearer = null,
        string? origin = null
    ) {
        using var request = new HttpRequestMessage(method, uri);

        if (cookies is not null) {
            request.Headers.Add("Cookie", cookies);
        }

        if (origin is not null) {
            request.Headers.Add("Origin", origin);
        }

        if (bearer is not null) {
            request.Headers.Authorization = new("Bearer", bearer);
        }

        if (body is not null) {
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }

        using var response = await http.SendAsync(request, cancellationToken);

        return new(response.StatusCode, response.Headers, await response.Content.ReadAsStringAsync(cancellationToken));
    }

    static Task<Answer> PostJsonAsync(
        HttpClient http,
        string path,
        string ticket,
        object body,
        CancellationToken cancellationToken
    ) =>
        SendAsync(
            http,
            HttpMethod.Post,
            new Uri(IdentityHost, path),
            Cookie(SignUpTicketCookie.CookieName, ticket),
            JsonSerializer.Serialize(body),
            cancellationToken
        );

    static async Task<Answer> PostFormAsync(
        HttpClient http,
        Dictionary<string, string> form,
        string? cookies,
        CancellationToken cancellationToken,
        string origin = PortalOrigin
    ) {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            new Uri(IdentityHost, IdentityHostOpenIddict.TokenPath)
        );
        request.Content = new FormUrlEncodedContent(form);

        request.Headers.Add("Origin", origin);
        if (cookies is not null) {
            request.Headers.Add("Cookie", cookies);
        }

        using var response = await http.SendAsync(request, cancellationToken);

        return new(response.StatusCode, response.Headers, await response.Content.ReadAsStringAsync(cancellationToken));
    }

    static Task<Answer> GetAsync(HttpClient http, string path, string bearer, CancellationToken cancellationToken) =>
        SendAsync(
            http,
            HttpMethod.Get,
            new Uri(Gateway, path + Version),
            null,
            null,
            cancellationToken,
            bearer
        );

    static async Task<string> PollUntilTerminalAsync(
        HttpClient http,
        string operation,
        string bearer,
        CancellationToken cancellationToken
    ) {
        var clock = Stopwatch.StartNew();
        var last = "NotStarted";

        while (clock.Elapsed < ConvergenceBudget) {
            var polled = await SendAsync(
                http,
                HttpMethod.Get,
                new Uri(operation),
                null,
                null,
                cancellationToken,
                bearer
            );
            polled.Status.ShouldBe(HttpStatusCode.OK, polled.Body);

            last = Json(polled.Body).GetProperty("status").GetString()!;
            if (last is "Succeeded" or "Failed" or "Canceled") {
                return last;
            }

            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
        }

        return last;
    }

    // ── Encodings ────────────────────────────────────────────────────────────────────────────────

    static string QueryValue(Uri uri, string name) {
        var query = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries);

        foreach (var pair in query) {
            var separator = pair.IndexOf('=', StringComparison.Ordinal);
            var key = separator < 0 ? pair : pair[..separator];

            if (string.Equals(Uri.UnescapeDataString(key), name, StringComparison.Ordinal)) {
                return separator < 0 ? string.Empty : Uri.UnescapeDataString(pair[(separator + 1)..]);
            }
        }

        throw new InvalidOperationException($"'{uri}' carries no '{name}' parameter.");
    }

    static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    static JsonElement Json(string body) => JsonDocument.Parse(body).RootElement.Clone();

    /// <summary>The token's payload, decoded and not verified — the gateway is what verifies.</summary>
    static JsonElement Payload(string jwt) {
        var segment = jwt.Split('.')[1].Replace('-', '+').Replace('_', '/');
        var padded = segment.PadRight(segment.Length + (4 - segment.Length % 4) % 4, '=');

        return Json(Encoding.UTF8.GetString(Convert.FromBase64String(padded)));
    }
}

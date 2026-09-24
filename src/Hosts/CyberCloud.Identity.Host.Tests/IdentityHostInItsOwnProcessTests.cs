using CyberCloud.Core.Resources;
using CyberCloud.Identity.Contracts;
using CyberCloud.Identity.Host.Tests.Infrastructure;
using CyberCloud.Identity.Host.Tokens;
using System.Net;
using System.Text.Json;

namespace CyberCloud.Identity.Host.Tests;

/// <summary>
///     #43's grain calls, made by the identity host running as its own process: the device flow's
///     begin, answer, poll and redemption, and an invitation described and accepted.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Why a process, when every other suite here is in-process.</b> The silos, the
///         identity host's Orleans client and the test's own client share one type manifest in the
///         test process, and that hides a type Orleans would refuse between two processes. #39's
///         shard-map confirmation passed every test on its branch and failed the first sign-up
///         against the AppHost. The review of #43 found that no call this issue added had crossed
///         a real process boundary. <see cref="IdentityHostProcess" /> says how it does here.
///     </para>
///     <para>
///         In the Mailpit collection because that fixture's silo can deliver an invitation. With
///         the default seam, creating one is refused before any call this suite is about.
///     </para>
/// </remarks>
[Collection(MailpitIdentityHostSuite.Name)]
public sealed class IdentityHostInItsOwnProcessTests(MailpitIdentityHostFixture fixture) {
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    static Guid Tenant => IdentityHostFixture.Tenant;

    const string Scope = "openid profile offline_access cyc.api";

    [Fact]
    public async Task TheDeviceFlowAndAnInvitationWorkThroughAHostInItsOwnProcess() {
        await using var host = await IdentityHostProcess.StartAsync(
            [.. fixture.ClusterClientSettings(), $"--{IdentityHostOptions.SectionName}:SignInPageBaseUri={IdentityHostFixture.SignInPageBaseUri}"],
            Ct
        );

        // ── The device flow: IDeviceAuthorizationGrain's four calls and its wire types. ──────────
        var person = await fixture.SignInFreshPersonAsync(IdentityHostFixture.SignInPageBaseUri, host.BaseAddress);

        using (person.Browser) {
            using var device = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }) {
                BaseAddress = host.BaseAddress
            };

            var codes = await StartDeviceAsync(device, host);

            using (var decided = await person.Browser.PostJsonAsync(
                       "/api/device/decision",
                       new { userCode = codes.GetProperty("user_code").GetString(), decision = "allow" },
                       Ct
                   )) {
                var decision = await BrowserClient.JsonAsync(decided, Ct);

                decision.GetProperty("status").GetString().ShouldBe("approved", decision.GetRawText() + host.Output);
            }

            using var collected = await PollAsync(device, codes.GetProperty("device_code").GetString()!);
            var tokens = await BrowserClient.JsonAsync(collected, Ct);

            collected.StatusCode.ShouldBe(HttpStatusCode.OK, tokens.GetRawText() + host.Output);
            BrowserClient.Payload(tokens.GetProperty("access_token").GetString()!)
                .GetProperty(AccessTokenClaims.Subject)
                .GetString()
                .ShouldBe(person.UserId.ToString("N"));

            // And the review's case over the same boundary: approved, suspended, refused.
            var later = await StartDeviceAsync(device, host);

            using (await person.Browser.PostJsonAsync(
                       "/api/device/decision",
                       new { userCode = later.GetProperty("user_code").GetString(), decision = "allow" },
                       Ct
                   )) { }

            (await fixture.For(Tenant).GetGrain<IUserGrain>(GrainKeys.User(person.UserId)).SetStatusAsync(UserStatus.Suspended))
                .IsSuccess.ShouldBeTrue();

            using var refused = await PollAsync(device, later.GetProperty("device_code").GetString()!);
            var answer = await BrowserClient.JsonAsync(refused, Ct);

            refused.StatusCode.ShouldBe(HttpStatusCode.BadRequest, answer.GetRawText());
            answer.GetProperty("error").GetString().ShouldBe("invalid_grant");
        }

        // ── An invitation: IInvitationGrain's describe and accept, and Withdrawn on the wire. ────
        var email = $"process-{Guid.NewGuid():N}@grants.example";
        var (first, second) = (Guid.NewGuid(), Guid.NewGuid());
        var (firstSecret, secondSecret) = (Secret(), Secret());

        await InviteAsync(first, email, firstSecret);
        await InviteAsync(second, email, secondSecret);

        using var colleague = new BrowserClient(host.BaseAddress, IdentityHostFixture.SignInPageBaseUri);

        (await DescribeAsync(colleague, first, firstSecret)).GetProperty("status").GetString().ShouldBe("pending");

        using (var accepted = await colleague.PostJsonAsync(
                   "/api/invitations/accept",
                   new {
                       tenant = Tenant.ToString("N"),
                       invitation = first.ToString("N"),
                       token = firstSecret,
                       displayName = "Across Processes",
                       password = IdentityHostFixture.Password
                   },
                   Ct
               )) {
            var body = await BrowserClient.JsonAsync(accepted, Ct);

            body.GetProperty("succeeded").GetBoolean().ShouldBeTrue(body.GetRawText() + host.Output);
        }

        // The other link names a member now, so it reads withdrawn: #43's new status, read off an
        // Invitation that came across the boundary.
        (await DescribeAsync(colleague, second, secondSecret)).GetProperty("status").GetString().ShouldBe("withdrawn");
    }

    // ── Helpers ────────────────────────────────────────────────────────────────────────────────

    static async Task<JsonElement> StartDeviceAsync(HttpClient device, IdentityHostProcess host) {
        using var started = await device.PostAsync(
            IdentityHostOpenIddict.DeviceAuthorizationPath,
            new FormUrlEncodedContent(
                new Dictionary<string, string>(StringComparer.Ordinal) { ["client_id"] = FirstPartyClients.Cli, ["scope"] = Scope }
            ),
            Ct
        );
        var codes = await BrowserClient.JsonAsync(started, Ct);

        started.StatusCode.ShouldBe(HttpStatusCode.OK, codes.GetRawText() + host.Output);

        return codes;
    }

    static Task<HttpResponseMessage> PollAsync(HttpClient device, string deviceCode) =>
        device.PostAsync(
            IdentityHostOpenIddict.TokenPath,
            new FormUrlEncodedContent(
                new Dictionary<string, string>(StringComparer.Ordinal) {
                    ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
                    ["client_id"] = FirstPartyClients.Cli,
                    ["device_code"] = deviceCode
                }
            ),
            Ct
        );

    async Task InviteAsync(Guid invitationId, string email, string secret) {
        var created = await fixture.For(Tenant)
            .GetGrain<IInvitationGrain>(GrainKeys.Invitation(invitationId))
            .CreateAsync(new() { Email = email, InvitedBy = fixture.UserId, TenantName = IdentityHostFixture.Slug }, secret);

        created.IsSuccess.ShouldBeTrue(created.Error?.Message);
    }

    static async Task<JsonElement> DescribeAsync(BrowserClient browser, Guid invitationId, string secret) {
        using var response = await browser.PostJsonAsync(
            "/api/invitations/describe",
            new { tenant = Tenant.ToString("N"), invitation = invitationId.ToString("N"), token = secret },
            Ct
        );
        var body = await BrowserClient.JsonAsync(response, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, body.GetRawText());

        return body;
    }

    static string Secret() => BrowserClient.Base64Url(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
}

using CyberCloud.Identity.Host.Tests.Infrastructure;
using CyberCloud.Identity.Host.Tokens;
using CyberCloud.Sdk;
using System.Net;
using System.Text;
using System.Text.Json;

namespace CyberCloud.Identity.Host.Tests;

/// <summary>
///     <c>cyc login</c>'s protocol half — the SDK's <see cref="DeviceCodeCredential" />, its refresh
///     and <see cref="CyberCloudSignOut" /> — against the real identity host rather than the
///     scripted server <c>cyc.Tests</c> uses. Issue #43, step 8.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Why this suite exists, in one line: the scripted server agreed with the SDK and the
///         host did not.</b> The SDK signed in as <c>cyc</c> (the host registers <c>cyc-cli</c>),
///         asked for <c>https://api.cybercloud.io/.default</c> (the host registers <c>cyc.api</c>),
///         polled with a <c>scope</c> (OpenIddict refuses one on a device-code request) and asked for
///         no <c>offline_access</c> (so nothing would have been cached). Every <c>cyc</c> test passed
///         against a server scripted to accept exactly that. The only test of the pair is the pair.
///     </para>
///     <para>
///         The person is a <see cref="BrowserClient" /> the prompt callback drives — what the human
///         does between the CLI printing the code and the CLI's next poll. The poll waits the
///         server's five-second interval in real time: the SDK's delay seam is internal to it, and
///         one real interval is the honest cost of running the SDK's loop unmodified.
///     </para>
/// </remarks>
[Collection(IdentityHostSuite.Name)]
public sealed class DeviceFlowThroughTheSdkTests(IdentityHostFixture fixture) {
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void TheSdkSignsInAsTheClientTheHostRegisters() {
        CyberCloudCliCredential.CliClientId.ShouldBe(FirstPartyClients.Cli);
        CyberCloudScopes.Default.ShouldBe(IdentityHostOpenIddict.Scopes.Api);
        CyberCloudScopes.OfflineAccess.ShouldBe(IdentityHostOpenIddict.Scopes.OfflineAccess);
    }

    [Fact]
    public async Task CycLoginsProtocolSignsInRefreshesOnUseAndLogoutRevokes() {
        var cache = TokenCache.CreateInMemory();
        var options = new CyberCloudCredentialOptions { AuthorityHost = fixture.BaseAddress, TokenCache = cache };
        var (browser, userId, _) = await fixture.SignInFreshPersonAsync(IdentityHostFixture.SignInPageBaseUri);
        DeviceCodeInfo? shown = null;

        using (browser) {
            // ── cyc login --device-code: the SDK asks, the person approves on another machine, the
            //    SDK polls and caches.
            using var login = new DeviceCodeCredential(
                CyberCloudCliCredential.CliClientId,
                async (info, cancellationToken) => {
                    shown = info;

                    using var decided = await browser.PostJsonAsync(
                        "/api/device/decision",
                        new { userCode = info.UserCode, decision = "allow" },
                        cancellationToken
                    );

                    var body = JsonDocument.Parse(await decided.Content.ReadAsStringAsync(cancellationToken)).RootElement;

                    body.GetProperty("status").GetString().ShouldBe("approved", body.GetRawText());
                },
                options
            );

            var token = await login.GetTokenAsync(new TokenRequestContext([CyberCloudScopes.Default]), Ct);

            shown.ShouldNotBeNull();
            shown.VerificationUri.AbsolutePath.ShouldBe(IdentityHostOpenIddict.EndUserVerificationPath);
            BrowserClient.Payload(token.Token).GetProperty("sub").GetString().ShouldBe(userId.ToString("N"));

            var key = TokenCache.KeyFor(fixture.BaseAddress, CyberCloudCliCredential.CliClientId, null);
            var cached = await cache.GetAsync(key, Ct);

            cached.ShouldNotBeNull("the sign-in cached nothing — no refresh token came back");
            cached.RefreshToken.ShouldNotBeNullOrEmpty();

            // ⚠ The record does not fit one Windows credential — CRED_MAX_CREDENTIAL_BLOB_SIZE is
            // 2,560 bytes and a real record is past it — which is what made cyc login fail on Windows
            // after the person had approved it. This assertion was `<= 2560` and went red at 2,951;
            // the cache now chunks (WindowsCredentialManagerTokenCache), and on Windows the real
            // record is round-tripped through the real Credential Manager under a throwaway key.
            JsonSerializer.SerializeToUtf8Bytes(cached).Length.ShouldBeGreaterThan(2_560, "the premise of the chunking");

            if (OperatingSystem.IsWindows()) {
                var keychain = TokenCache.CreatePersistent();
                var throwaway = "identity-host-tests|" + Guid.NewGuid().ToString("N") + "|-";

                keychain.IsAvailable.ShouldBeTrue();

                try {
                    await keychain.SetAsync(throwaway, cached, Ct);
                    (await keychain.GetAsync(throwaway, Ct)).ShouldBe(cached);
                } finally {
                    await keychain.RemoveAsync(throwaway, Ct);
                }

                (await keychain.GetAsync(throwaway, Ct)).ShouldBeNull("the chunks outlived the removal");
            }

            // ── The next command: refresh on use, #94's rotation. No prompt — a second prompt
            //    would be the cache not working.
            using var nextCommand = new DeviceCodeCredential(
                CyberCloudCliCredential.CliClientId,
                static (_, _) => throw new InvalidOperationException("the cached sign-in was not used"),
                options
            );

            var refreshed = await nextCommand.GetTokenAsync(new TokenRequestContext([CyberCloudScopes.Default]), Ct);

            refreshed.Token.ShouldNotBe(token.Token);
            (await cache.GetAsync(key, Ct))!.RefreshToken.ShouldNotBe(cached.RefreshToken, "the chain did not rotate");

            var live = (await cache.GetAsync(key, Ct))!.RefreshToken!;

            // ── cyc logout: revoked at the host, forgotten here.
            var signedOut = await CyberCloudSignOut.SignOutAsync(options, CyberCloudCliCredential.CliClientId, Ct);

            signedOut.HadSignIn.ShouldBeTrue();
            signedOut.Revoked.ShouldBeTrue(signedOut.Detail);
            (await cache.GetAsync(key, Ct)).ShouldBeNull();

            // And the server agrees: the refresh token that was live is dead everywhere.
            using var http = new HttpClient { BaseAddress = fixture.BaseAddress };
            using var afterLogout = await http.PostAsync(
                IdentityHostOpenIddict.TokenPath,
                new FormUrlEncodedContent(
                    new Dictionary<string, string>(StringComparer.Ordinal) {
                        ["grant_type"] = "refresh_token",
                        ["client_id"] = CyberCloudCliCredential.CliClientId,
                        ["refresh_token"] = live
                    }
                ),
                Ct
            );

            afterLogout.StatusCode.ShouldBe(HttpStatusCode.BadRequest, Encoding.UTF8.GetString(await afterLogout.Content.ReadAsByteArrayAsync(Ct)));
        }
    }

    [Fact]
    public async Task ADeclinedSignInIsAnErrorTheSdkNames() {
        var (browser, _, _) = await fixture.SignInFreshPersonAsync(IdentityHostFixture.SignInPageBaseUri);

        using (browser) {
            using var login = new DeviceCodeCredential(
                CyberCloudCliCredential.CliClientId,
                async (info, cancellationToken) => {
                    using var _ = await browser.PostJsonAsync(
                        "/api/device/decision",
                        new { userCode = info.UserCode, decision = "deny" },
                        cancellationToken
                    );
                },
                new CyberCloudCredentialOptions { AuthorityHost = fixture.BaseAddress, TokenCache = TokenCache.CreateInMemory() }
            );

            var refused = await Should.ThrowAsync<AuthenticationFailedException>(async () =>
                await login.GetTokenAsync(new TokenRequestContext([CyberCloudScopes.Default]), Ct)
            );

            refused.ErrorCode.ShouldBe("access_denied");
        }
    }
}

using CyberCloud.Core;
using CyberCloud.Core.Resources;
using CyberCloud.Identity.Contracts;
using Microsoft.Extensions.Logging;
using Orleans.Multitenant;
using System.Globalization;

namespace CyberCloud.Identity.Host.Tokens;

/// <summary>
///     The device-flow half of the token surface: minting the two codes, answering a poll, and
///     reaching the grain a user code names. docs/plan/11 § Protocol, RFC 8628.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Every grain here is in the platform tenant, because no other tenant exists yet.</b>
///         The device asks before anybody has signed in; the person names their tenant on the
///         sign-in page afterwards. <c>GrainKeys.DeviceAuthorization</c> carries the argument, and
///         the tenant the person chose travels in the approval, not in the qualification.
///     </para>
///     <para>
///         Called from OpenIddict's pipeline (both halves of <c>DegradedModeHandlers.StoreDeviceCodes</c>),
///         from the token endpoint's passthrough (<c>TokenApi.MintForDeviceCodeAsync</c>) and from the
///         page's API
///         (<c>Api.DeviceApi</c>), so the rule for turning a typed or presented code into a grain
///         reference has one home.
///     </para>
/// </remarks>
/// <param name="grains">The cluster. ⚠ Every reference goes through <c>ForTenant</c>.</param>
/// <param name="logger">Where a draw that kept colliding is reported.</param>
public sealed class DeviceFlow(IGrainFactory grains, ILogger<DeviceFlow> logger) {
    /// <summary>
    ///     How many user codes to draw before giving up. A collision needs a live code already
    ///     holding the same eight letters out of 2.56 × 10¹⁰, so a second draw is already rare and
    ///     a fifth is a broken random-number generator, not bad luck.
    /// </summary>
    public const int MaxDraws = 5;

    /// <summary>
    ///     Starts a device authorization: draws a user code, records it with a fresh secret, and
    ///     returns both codes.
    /// </summary>
    /// <param name="request">What the device asked for.</param>
    /// <param name="cancellationToken">Cancels between draws.</param>
    /// <returns>The device code and the unformatted user code, or the grain's refusal.</returns>
    public async Task<Result<(string DeviceCode, string UserCode)>> BeginAsync(
        DeviceAuthorizationRequest request,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(request);

        Error? last = null;

        for (var draw = 0; draw < MaxDraws; draw++) {
            cancellationToken.ThrowIfCancellationRequested();

            var userCode = DeviceCodes.NewUserCode();
            var secret = DeviceCodes.NewSecret();
            var begun = await Grain(userCode).BeginAsync(request, secret);

            if (begun.IsSuccess) {
                return Result<(string, string)>.Success((DeviceCodes.Compose(userCode, secret), userCode));
            }

            last = begun.Error;

            if (last!.Code != ErrorCode.Conflict) {
                break;
            }
        }

        GrantLog.DeviceAuthorizationNotBegun(logger, request.ClientId, last?.Message ?? string.Empty);

        return Result<(string, string)>.Failure(
            last ?? new Error(ErrorCode.Conflict, "No user code could be drawn.")
        );
    }

    /// <summary>
    ///     A poll, answered by the grain the device code names — or <see cref="DevicePollOutcome.Unknown" />
    ///     for a value that is not a device code at all, without touching a grain.
    /// </summary>
    /// <param name="deviceCode">The <c>device_code</c> parameter, verbatim.</param>
    public async Task<DevicePoll> PollAsync(string? deviceCode) {
        if (!DeviceCodes.TryParse(deviceCode, out var userCode, out var secret)) {
            return new(DevicePollOutcome.Unknown, TimeSpan.Zero, string.Empty, [], null);
        }

        var polled = await Grain(userCode).PollAsync(secret);

        return polled.IsSuccess
            ? polled.GetValueOrThrow()
            : new(DevicePollOutcome.Unknown, TimeSpan.Zero, string.Empty, [], null);
    }

    /// <summary>
    ///     Redeems an approved device code for the token session about to be opened.
    /// </summary>
    /// <param name="deviceCode">The <c>device_code</c> parameter.</param>
    /// <param name="tokenSessionId">The session id the caller minted and will open next.</param>
    public async Task<Result<DeviceRedemption>> RedeemAsync(string? deviceCode, Guid tokenSessionId) =>
        DeviceCodes.TryParse(deviceCode, out var userCode, out var secret)
            ? await Grain(userCode).RedeemAsync(secret, tokenSessionId)
            : Result<DeviceRedemption>.Failure(ErrorCode.AuthorizationFailed, "device-code-malformed");

    /// <summary>The grain a normalized user code names.</summary>
    /// <param name="userCode">A code <see cref="DeviceCodes.NormalizeUserCode" /> produced.</param>
    public IDeviceAuthorizationGrain Grain(string userCode) =>
        grains.ForTenant(Guid.Empty.ToString("D", CultureInfo.InvariantCulture))
            .GetGrain<IDeviceAuthorizationGrain>(GrainKeys.DeviceAuthorization(userCode));
}

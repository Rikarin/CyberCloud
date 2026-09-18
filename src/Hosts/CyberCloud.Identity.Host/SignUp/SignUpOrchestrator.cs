using CyberCloud.Authorization.Contracts;
using CyberCloud.Core;
using CyberCloud.Core.Resources;
using CyberCloud.Identity.Contracts;
using CyberCloud.Identity.SignIn;
using CyberCloud.ResourceManager.Contracts;
using CyberCloud.Tenancy.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Multitenant;
using System.Globalization;
using System.Text.Json;

namespace CyberCloud.Identity.Host.SignUp;

/// <summary>
///     Everything <c>POST /api/signup/complete</c> hands the orchestrator, already checked.
/// </summary>
/// <param name="SignUp">The sign-up, as the grain describes it. ⚠ <c>Verified</c> is the caller's to have checked.</param>
/// <param name="DisplayName">What to call the person.</param>
/// <param name="OrganizationName">The tenant's display name.</param>
/// <param name="Slug">
///     The tenant's slug — <c>ResourceNaming.Slugify</c> of <paramref name="OrganizationName" />,
///     already validated.
/// </param>
/// <param name="Password">The password to enrol, or <see langword="null" /> for a passkey.</param>
/// <param name="Passkey">
///     The passkey to enrol, already verified against the registration challenge, or
///     <see langword="null" /> for a password. ⚠ Exactly one of the two is set.
/// </param>
/// <param name="Context">What the host knows about the request, for the session it opens.</param>
public sealed record SignUpCompletion(
    SignUpDescriptor SignUp,
    string DisplayName,
    string OrganizationName,
    string Slug,
    string? Password,
    PasskeyCredential? Passkey,
    SignInContext Context
);

/// <summary>What completing a sign-up produced, or where it stopped.</summary>
/// <param name="Session">The session opened for the new user, or <see langword="null" /> on failure.</param>
/// <param name="FailedStep">The step that failed, or <see langword="null" /> on success.</param>
/// <param name="Error">Why it failed, or <see langword="null" /> on success. ⚠ Internal; the API maps it to a sentence.</param>
public sealed record SignUpOutcome(SignInOutcome? Session, SignUpStep? FailedStep, Error? Error) {
    /// <summary>Whether every step ran and the person is signed in.</summary>
    public bool Succeeded => Session is not null;
}

/// <summary>
///     Runs the create steps of a self-serve sign-up in order, recording each in
///     <see cref="ISignUpGrain" /> so a retry resumes where the last attempt stopped.
///     docs/plan/11 § Sign-up and tenant creation.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Here, in the identity host, and not in a grain — because the host is a client.</b>
///         <c>PlatformCrossTenantAuthorizer</c> denies a platform-tenant grain reaching into a
///         tenant, and this sequence reaches into the new tenant from the first step. An Orleans
///         client is outside that filter, which is why <c>ScopeManagerService</c> — written for the
///         gateway — already does the same cross-tenant sequence on <c>CreateTenantAsync</c>'s
///         behalf, and why this class sits beside it: <c>IScopeManager</c> is resolved from the same
///         <c>AddCyberCloudResourceManager</c> registration the gateway and the feeds host use.
///     </para>
///     <para>
///         <b>The order is the design, and each step's authority is different.</b> The tenant is
///         created as the sign-up operator (<c>IdentityBootstrap.SignUpOperator</c>, whose
///         <c>platform:root#operator</c> grant the silo seeded) with the new user named as its owner
///         — <c>IScopeManager.CreateTenantAsync</c>'s remarks say why the operator is never the
///         owner. The user and the credential are grain calls under the new tenant. The subscription
///         and the resource group are created <i>as the new user</i> through the ordinary scope
///         path, which is exactly what the portal would do next and exercises the owner tuple the
///         tenant step wrote: tenant owner → <c>write</c> on the tenant → a subscription, and its
///         parent edge → <c>write</c> on the subscription → a group.
///     </para>
///     <para>
///         ⚠ <b>Re-drivable, and the grain is what makes it so.</b> Every step is skipped when the
///         grain already recorded it, and recorded only after its own write returned. A completion
///         that fails at the subscription step answers "something went wrong" and leaves the ticket
///         valid; the next <c>complete</c> skips the tenant, the user and the credential and starts
///         at the subscription. The tenant id, the user id and the subscription id were allocated
///         at <c>begin</c>, so the retry re-drives the same objects rather than minting a second
///         tenant — <c>SignUpApiTests.AFailedStepLeavesTheTicketRedrivableAndResumesAtTheNextStep</c>.
///     </para>
///     <para>
///         ⚠ <b>The slug is checked against the directory before anything is created.</b>
///         <c>ScopeManagerService.CreateTenantAsync</c> creates the tenant grain and writes the owner
///         tuple <i>before</i> it registers the directory entry, so a slug conflict from the
///         directory would leave a tenant grain holding the sign-up's pre-allocated id under a slug
///         the person is about to change — and the retry with a new name would then be refused by
///         the tenant grain as a rename. One <c>LookupBySlugAsync</c> first turns "taken" into an
///         answer with nothing created. The race between two sign-ups choosing one name in the
///         same second is left to the directory's own conflict, which is the generic failure.
///     </para>
///     <para>
///         ⚠ <b>Every grain reference goes through <c>ForTenant</c></b>, as everywhere in this host
///         — CC1006 keeps it true. The one exception is the directory, which is a null-tenant
///         platform grain and is reached through the plain factory as <c>ScopeManagerService</c>
///         reaches it.
///     </para>
/// </remarks>
public sealed class SignUpOrchestrator(
    IScopeManager scopes,
    IGrainFactory grains,
    SignInService signIn,
    IOptions<IdentityHostOptions> options,
    ILogger<SignUpOrchestrator> logger
) {
    /// <summary>The default subscription's display name.</summary>
    public const string DefaultSubscriptionName = "Default";

    /// <summary>The default resource group's name.</summary>
    public const string DefaultResourceGroupName = "default";

    /// <summary>
    ///     How long to wait before the one retry of the subscription step.
    /// </summary>
    /// <remarks>
    ///     ⚠ The owner tuple the tenant step wrote is what the subscription step's check reads, and
    ///     the check grain's cache may not have seen it yet — docs/plan/07 § Consistency. The seam
    ///     renders a denied check as <c>ResourceNotFound</c>, so that is the code retried, once.
    /// </remarks>
    public static TimeSpan OwnerPropagationDelay { get; } = TimeSpan.FromMilliseconds(250);

    readonly IdentityHostOptions options = options.Value;

    /// <summary>Runs steps b–h of <c>POST /api/signup/complete</c>.</summary>
    /// <param name="completion">What to create, already checked by <c>SignUpApi</c>.</param>
    /// <param name="cancellationToken">Cancels the run between steps; a step in flight completes.</param>
    public async Task<SignUpOutcome> CompleteAsync(SignUpCompletion completion, CancellationToken cancellationToken) {
        ArgumentNullException.ThrowIfNull(completion);

        var signup = completion.SignUp;
        var grain = Platform().GetGrain<ISignUpGrain>(GrainKeys.SignUp(signup.SignupId));

        foreach (var (step, run) in Steps(completion)) {
            if (signup.CompletedSteps.Contains(step)) {
                continue;
            }

            cancellationToken.ThrowIfCancellationRequested();

            var ran = await run();
            if (ran.TryGetError(out var error)) {
                IdentityLog.SignUpFailed(logger, signup.SignupId, signup.TenantId, step, error.Message);
                return new(null, step, error);
            }

            // ⚠ AFTER the step's write returned, never before — ISignUpGrain.RecordStepAsync. The
            // last step is the grain's own CompleteAsync, after which it records nothing further.
            var recorded = step == SignUpStep.Completed
                ? await grain.CompleteAsync()
                : await grain.RecordStepAsync(step);
            if (recorded.TryGetError(out var unrecorded)) {
                IdentityLog.SignUpFailed(logger, signup.SignupId, signup.TenantId, step, unrecorded.Message);
                return new(null, step, unrecorded);
            }
        }

        // ── h. The cookie session, opened as any sign-in opens one. ─────────────────────────────
        var method = completion.Passkey is not null ? AuthenticationMethod.Passkey : AuthenticationMethod.Password;

        var session = await signIn.OpenSessionAsync(signup.TenantId, signup.UserId, method, completion.Context);
        if (session.TryGetError(out var refused)) {
            IdentityLog.SignUpFailed(logger, signup.SignupId, signup.TenantId, SignUpStep.Completed, refused.Message);
            return new(null, SignUpStep.Completed, refused);
        }

        return new(session.GetValueOrThrow(), null, null);
    }

    /// <summary>The steps, in <see cref="SignUpStep" /> order, each as the call that runs it.</summary>
    IEnumerable<(SignUpStep Step, Func<Task<Result>> Run)> Steps(SignUpCompletion completion) {
        yield return (SignUpStep.TenantCreated, () => CreateTenantAsync(completion));
        yield return (SignUpStep.UserCreated, () => CreateUserAsync(completion));
        yield return (SignUpStep.CredentialSet, () => SetCredentialAsync(completion));
        yield return (SignUpStep.SubscriptionCreated, () => CreateSubscriptionAsync(completion.SignUp));
        yield return (SignUpStep.ResourceGroupCreated, () => CreateResourceGroupAsync(completion.SignUp));
        yield return (SignUpStep.Completed, static () => Task.FromResult(Result.Success));
    }

    // ── b. The tenant, as the sign-up operator, owned by the new user ─────────────────────────

    async Task<Result> CreateTenantAsync(SignUpCompletion completion) {
        var signup = completion.SignUp;

        if (string.IsNullOrWhiteSpace(options.DefaultRegion)) {
            return Result.Failure(
                ErrorCode.InvalidRequestBody,
                "CyberCloud:Identity:DefaultRegion is not set, and a tenant is homed to exactly one "
                + "region at creation. Set it to the region this deployment places sign-ups in."
            );
        }

        var held = await grains
            .GetGrain<ITenantDirectoryGrain>(GrainKeys.TenantDirectory())
            .LookupBySlugAsync(completion.Slug);

        if (held.IsSuccess && held.GetValueOrThrow().TenantId != signup.TenantId) {
            return Result.Failure(
                ErrorCode.Conflict,
                $"Slug '{completion.Slug}' is already held by tenant {held.GetValueOrThrow().TenantId:D}."
            );
        }

        var created = await scopes.CreateTenantAsync(
            new() {
                TenantId = signup.TenantId,
                Slug = completion.Slug,
                DisplayName = completion.OrganizationName,
                HomeRegion = options.DefaultRegion,
                OwnerSubjectType = SubjectTypes.User,
                OwnerSubjectId = N(signup.UserId)
            },
            new() {
                TenantId = Guid.Empty,
                SubjectType = SubjectTypes.ServicePrincipal,
                SubjectId = N(IdentityBootstrap.SignUpOperator),
                CorrelationId = N(signup.SignupId)
            }
        );

        return created.ToResult();
    }

    // ── c. The user, under the new tenant, holding the email-index claim ───────────────────────

    async Task<Result> CreateUserAsync(SignUpCompletion completion) {
        var signup = completion.SignUp;
        var tenant = grains.ForTenant(D(signup.TenantId));
        var index = tenant.GetGrain<IEmailIndexGrain>(GrainKeys.EmailIndex(signup.TenantId, signup.Email));

        // docs/plan/06 § Two-phase create: the index claim first, so a crash between the steps
        // leaves a leased name rather than a user nobody can find by address. Re-driven, the claim
        // is already this user's and the grain answers the same entry.
        var claimed = await index.TryClaimAsync(signup.Email, signup.UserId);
        if (claimed.TryGetError(out var taken)) {
            return Result.Failure(taken);
        }

        var created = await tenant
            .GetGrain<IUserGrain>(GrainKeys.User(signup.UserId))
            .CreateAsync(signup.Email, completion.DisplayName, UserStatus.Active);

        if (created.TryGetError(out var uncreated)) {
            return Result.Failure(uncreated);
        }

        return (await index.ConfirmAsync(signup.UserId)).ToResult();
    }

    // ── d. The credential ──────────────────────────────────────────────────────────────────────

    async Task<Result> SetCredentialAsync(SignUpCompletion completion) {
        var signup = completion.SignUp;
        var user = grains.ForTenant(D(signup.TenantId)).GetGrain<IUserGrain>(GrainKeys.User(signup.UserId));

        if (completion.Passkey is { } passkey) {
            return (await user.AddPasskeyAsync(passkey)).ToResult();
        }

        return await user.SetPasswordAsync(completion.Password ?? string.Empty);
    }

    // ── e. The default subscription, as the new user ───────────────────────────────────────────

    async Task<Result> CreateSubscriptionAsync(SignUpDescriptor signup) {
        var request = new ScopeRequest {
            Path = ScopeId.Subscription(signup.TenantId, signup.SubscriptionId).Path,
            Body = JsonSerializer.Serialize(
                new Dictionary<string, string>(StringComparer.Ordinal) {
                    [ScopeBodyProperties.DisplayName] = DefaultSubscriptionName
                }
            ),
            Caller = Caller(signup)
        };

        var created = await scopes.CreateAsync(request);

        if (created.TryGetError(out var error) && error.Code == ErrorCode.ResourceNotFound) {
            // ⚠ One retry, because the owner tuple was written moments ago and the seam renders a
            // check that has not seen it yet as "does not exist" — OwnerPropagationDelay.
            await Task.Delay(OwnerPropagationDelay);
            created = await scopes.CreateAsync(request);
        }

        return created.ToResult();
    }

    // ── f. The default resource group, as the new user ─────────────────────────────────────────

    async Task<Result> CreateResourceGroupAsync(SignUpDescriptor signup) {
        var created = await scopes.CreateAsync(
            new() {
                Path = ScopeId.Group(signup.TenantId, signup.SubscriptionId, DefaultResourceGroupName).Path,
                Body = JsonSerializer.Serialize(
                    new Dictionary<string, string>(StringComparer.Ordinal) {
                        [ScopeBodyProperties.Location] = options.DefaultRegion
                    }
                ),
                Caller = Caller(signup)
            }
        );

        return created.ToResult();
    }

    // ── Internals ──────────────────────────────────────────────────────────────────────────────

    static CallerContext Caller(SignUpDescriptor signup) =>
        new() {
            TenantId = signup.TenantId,
            SubjectType = SubjectTypes.User,
            SubjectId = N(signup.UserId),
            CorrelationId = N(signup.SignupId)
        };

    // ⚠ TenantGrainFactory and not IGrainFactory — CC1006's discriminator is the type. SignInApi
    // carries the same note.
    TenantGrainFactory Platform() => grains.ForTenant(D(Guid.Empty));

    static string N(Guid value) => value.ToString("N", CultureInfo.InvariantCulture);

    static string D(Guid value) => value.ToString("D", CultureInfo.InvariantCulture);
}

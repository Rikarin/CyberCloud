using CyberCloud.Gateway.Host.Tests.Infrastructure;
using System.Text;

namespace CyberCloud.Gateway.Host.Tests;

/// <summary>
///     <c>404</c> never <c>403</c>, and the two <c>404</c>s are the same bytes.
/// </summary>
/// <remarks>
///     ⚠ <b>The status code is only half the defence and the plan only writes down that half.</b>
///     docs/plan/07 § The enforcement seam requires <c>404</c> for a resource the caller cannot read,
///     because <i>"a 403 confirms the resource exists, which is an enumeration oracle"</i>. It says
///     nothing about the body — and the resource manager writes one message for an absent resource
///     and the enforcement seam writes another for an invisible one. Two different sentences under
///     one status code reconstruct exactly the distinction the status code erased, so
///     <c>ResultShaper</c> re-renders every not-found through <c>GatewayErrors.NotFound</c> and this
///     file compares the bytes.
/// </remarks>
public sealed class NotFoundNeverForbiddenTests {
    [Fact]
    public async Task TheAbsentAndTheUnauthorized404sAreByteIdentical() {
        var absent = await Answer(request => Result<ResourceSnapshot>.Failure(
            ErrorCode.ResourceNotFound,
            $"'{request.Path}' does not exist."
        ));

        // What a seam that had NOT been forced through one renderer would plausibly write. It is a
        // different sentence, it names the caller, and it is a complete oracle on its own.
        var unauthorized = await Answer(request => Result<ResourceSnapshot>.Failure(
            ErrorCode.ResourceNotFound,
            $"The caller is not permitted to read '{request.Path}' and the resource exists."
        ));

        absent.Status.ShouldBe(StatusCodes.Status404NotFound);
        unauthorized.Status.ShouldBe(StatusCodes.Status404NotFound);

        Encoding.UTF8.GetBytes(unauthorized.Body).ShouldBe(Encoding.UTF8.GetBytes(absent.Body));
        absent.Body.ShouldNotContain("permitted");
        absent.Body.ShouldNotContain("exists");
    }

    [Fact]
    public async Task ASubscriptionThatDoesNotExistGetsTheSameBodyAsAResourceThatDoesNot() {
        var resource = await Answer(request => Result<ResourceSnapshot>.Failure(
            ErrorCode.ResourceNotFound,
            $"'{request.Path}' does not exist."
        ));

        // ⚠ A distinct "no such subscription" body would let a prober enumerate subscription ids
        // inside a tenant, which is a smaller leak than cross-tenant and still a real one.
        var subscription = await Answer(_ => Result<ResourceSnapshot>.Failure(
            ErrorCode.SubscriptionNotFound,
            "That subscription does not exist in this tenant."
        ));

        subscription.Status.ShouldBe(StatusCodes.Status404NotFound);
        subscription.Body.ShouldBe(resource.Body);
    }

    [Fact]
    public async Task A403IsReturnedOnlyWhenTheSeamSaysTheCallerCanReadButNotAct() {
        var gateway = new GatewayHarness();

        gateway.Manager.OnWrite = _ => Result<WriteAccepted>.Failure(
            ErrorCode.AuthorizationFailed,
            "The caller may read this server but not write it."
        );

        var response = await gateway.SendAsync(
            "PUT",
            GatewayHarness.ResourcePath(GatewayHarness.TenantA),
            gateway.Token(GatewayHarness.TenantA),
            body: "{\"properties\":{}}"
        );

        response.Status.ShouldBe(StatusCodes.Status403Forbidden);

        // ⚠ And the 403 keeps its own message. That is the point of the distinction: a caller who
        // can see the resource is owed a reason, and telling them costs nothing they did not know.
        response.Body.ShouldContain("AuthorizationFailed");
        response.Body.ShouldContain("but not write");
    }

    /// <summary>
    ///     ⚠ The negative control for the rewrite: a code that is <i>not</i> a not-found keeps its
    ///     message, so the rewrite is targeted rather than a blanket message eraser.
    /// </summary>
    [Fact]
    public async Task ANonNotFoundFailureKeepsItsOwnMessage() {
        var gateway = new GatewayHarness();

        gateway.Manager.OnWrite = _ => Result<WriteAccepted>.Failure(
            ErrorCode.QuotaExceeded,
            "Subscription quota for 'vcpu' in region 'eu-central' would be exceeded (requested 8, available 2)."
        );

        var response = await gateway.SendAsync(
            "PUT",
            GatewayHarness.ResourcePath(GatewayHarness.TenantA),
            gateway.Token(GatewayHarness.TenantA),
            body: "{\"properties\":{}}"
        );

        response.Status.ShouldBe(StatusCodes.Status429TooManyRequests);
        response.Body.ShouldContain("requested 8, available 2");
    }

    static async Task<GatewayResponse> Answer(Func<WriteRequest, Result<ResourceSnapshot>> onRead) {
        var gateway = new GatewayHarness();
        gateway.Manager.OnRead = onRead;

        return await gateway.SendAsync(
            "GET",
            GatewayHarness.ResourcePath(GatewayHarness.TenantA),
            gateway.Token(GatewayHarness.TenantA)
        );
    }
}

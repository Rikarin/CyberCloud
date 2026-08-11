using CyberCloud.Gateway.Host.Http;
using CyberCloud.Gateway.Host.Operations;
using CyberCloud.Gateway.Host.Tests.Infrastructure;

namespace CyberCloud.Gateway.Host.Tests;

/// <summary>docs/plan/10 § API versioning — required, dated, immutable, and no "latest".</summary>
public sealed class ApiVersionTests {
    [Fact]
    public async Task AMissingApiVersionIs400NamingTheCurrentVersion() {
        var gateway = new GatewayHarness();

        var response = await gateway.SendAsync(
            "GET",
            GatewayHarness.ResourcePath(GatewayHarness.TenantA),
            gateway.Token(GatewayHarness.TenantA),
            ""
        );

        response.Status.ShouldBe(StatusCodes.Status400BadRequest);
        response.Body.ShouldContain("InvalidApiVersion");
        response.Body.ShouldContain(OneTypeRegistry.TheVersion);

        // ⚠ And nothing was dispatched. A missing version is decided before any grain call.
        gateway.Manager.Paths.ShouldBeEmpty();
    }

    [Fact]
    public async Task AnUnknownApiVersionIs400NamingTheCurrentVersion() {
        var gateway = new GatewayHarness();

        var response = await gateway.SendAsync(
            "GET",
            GatewayHarness.ResourcePath(GatewayHarness.TenantA),
            gateway.Token(GatewayHarness.TenantA),
            "api-version=2099-01-01"
        );

        response.Status.ShouldBe(StatusCodes.Status400BadRequest);
        response.Body.ShouldContain(OneTypeRegistry.TheVersion);
    }

    /// <summary>
    ///     ⚠ The property the whole scheme exists for: an old client keeps getting the shape it was
    ///     written against.
    /// </summary>
    [Fact]
    public async Task AnOlderRegisteredVersionStillWorksAndReachesDispatchAtThatVersion() {
        var gateway = new GatewayHarness();
        string? dispatchedVersion = null;

        gateway.Manager.OnRead = request => {
            dispatchedVersion = request.ApiVersion;
            return Result<ResourceSnapshot>.Success(new() { Path = request.Path, ApiVersion = request.ApiVersion });
        };

        var response = await gateway.SendAsync(
            "GET",
            GatewayHarness.ResourcePath(GatewayHarness.TenantA),
            gateway.Token(GatewayHarness.TenantA),
            "api-version=" + OneTypeRegistry.OlderVersion
        );

        response.Status.ShouldBe(StatusCodes.Status200OK);
        dispatchedVersion.ShouldBe(OneTypeRegistry.OlderVersion);
    }

    [Fact]
    public async Task AMissingApiVersionIsReportedBeforeAnUnknownPath() {
        var gateway = new GatewayHarness();

        // ⚠ A 404 here would send a caller who forgot the parameter looking for a missing resource.
        var response = await gateway.SendAsync(
            "GET",
            $"/tenants/{GatewayHarness.TenantA:D}/subscriptions/{GatewayHarness.Subscription:D}"
            + "/resourceGroups/prod/providers/CyberCloud.Nope/widgets/one",
            gateway.Token(GatewayHarness.TenantA),
            ""
        );

        response.Status.ShouldBe(StatusCodes.Status400BadRequest);
        response.Body.ShouldContain("InvalidApiVersion");
    }

    [Fact]
    public async Task AnUnknownResourceTypeIsTheCanonical404() {
        var gateway = new GatewayHarness();

        var path = $"/tenants/{GatewayHarness.TenantA:D}/subscriptions/{GatewayHarness.Subscription:D}"
            + "/resourceGroups/prod/providers/CyberCloud.Nope/widgets/one";

        var response = await gateway.SendAsync("GET", path, gateway.Token(GatewayHarness.TenantA));

        response.Status.ShouldBe(StatusCodes.Status404NotFound);

        // ⚠ Byte-identical to the canonical not-found for that path — the same bytes an existing
        // type's missing resource produces. A distinct "no such provider" message would let a caller
        // enumerate which provider namespaces the platform serves, and once providers are
        // per-customer that is a customer list.
        var canonical = System.Text.Encoding.UTF8.GetString(ErrorBody.Render(GatewayErrors.NotFound(path)));

        response.Body.ShouldBe(canonical);
    }
}

/// <summary>docs/plan/10 § Long-running operations — Azure's pattern exactly, plus the progress array.</summary>
public sealed class LongRunningOperationTests {
    static readonly Guid TheOperation = Guid.Parse("11111111-1111-1111-1111-111111111111");

    [Fact]
    public async Task APutIs202WithAzureAsyncOperationAndRetryAfter() {
        var gateway = new GatewayHarness();

        var response = await gateway.SendAsync(
            "PUT",
            GatewayHarness.ResourcePath(GatewayHarness.TenantA),
            gateway.Token(GatewayHarness.TenantA),
            body: "{\"properties\":{\"sku\":\"gp1\"}}"
        );

        response.Status.ShouldBe(StatusCodes.Status202Accepted);

        // The pair is what makes Operation<T> and `--wait` standard rather than bespoke.
        response.Header(GatewayHeaders.AsyncOperation)
            .ShouldBe($"https://api.cybercloud.io/operations/{TheOperation:D}?api-version={OneTypeRegistry.TheVersion}");

        response.Header(GatewayHeaders.RetryAfter).ShouldBe("10");
    }

    [Fact]
    public async Task ADeleteAndAnActionAreAlso202WithTheSameHeaders() {
        var gateway = new GatewayHarness();
        var token = gateway.Token(GatewayHarness.TenantA);

        var deleted = await gateway.SendAsync("DELETE", GatewayHarness.ResourcePath(GatewayHarness.TenantA), token);
        deleted.Status.ShouldBe(StatusCodes.Status202Accepted);
        deleted.Header(GatewayHeaders.AsyncOperation).ShouldNotBeEmpty();

        var acted = await gateway.SendAsync(
            "POST",
            GatewayHarness.ResourcePath(GatewayHarness.TenantA) + "/restart",
            token
        );

        acted.Status.ShouldBe(StatusCodes.Status202Accepted);
        acted.Header(GatewayHeaders.RetryAfter).ShouldBe("10");
    }

    [Fact]
    public async Task AnUndeclaredActionIsTheCanonical404() {
        var gateway = new GatewayHarness();

        var response = await gateway.SendAsync(
            "POST",
            GatewayHarness.ResourcePath(GatewayHarness.TenantA) + "/selfDestruct",
            gateway.Token(GatewayHarness.TenantA)
        );

        response.Status.ShouldBe(StatusCodes.Status404NotFound);
        gateway.Manager.Paths.ShouldBeEmpty();
    }

    [Fact]
    public async Task TheOperationEndpointReturnsTheProgressArray() {
        var gateway = new GatewayHarness();

        gateway.Operations.OnRead = id => Result<OperationStatus>.Success(new() {
            OperationId = id,
            State = OperationState.Running,
            PercentComplete = 40,
            ResourcePath = GatewayHarness.ResourcePath(GatewayHarness.TenantA),
            StartedAt = gateway.Clock.UtcNow,
            Progress = [
                new() { At = gateway.Clock.UtcNow, Step = "etcd", Detail = "etcd cluster ready", PercentComplete = 40 }
            ]
        });

        var response = await gateway.SendAsync(
            "GET",
            $"/operations/{TheOperation:D}",
            gateway.Token(GatewayHarness.TenantA)
        );

        response.Status.ShouldBe(StatusCodes.Status200OK);
        response.Body.ShouldContain("\"status\":\"Running\"");
        response.Body.ShouldContain("\"percentComplete\":40");
        response.Body.ShouldContain("\"step\":\"etcd\"");
        response.Body.ShouldContain("\"message\":\"etcd cluster ready\"");

        // Still running, so keep polling.
        response.Header(GatewayHeaders.RetryAfter).ShouldBe("10");
    }

    [Fact]
    public async Task ATerminalOperationDoesNotAskTheCallerToKeepPolling() {
        var gateway = new GatewayHarness();

        gateway.Operations.OnRead = id => Result<OperationStatus>.Success(new() {
            OperationId = id,
            State = OperationState.Succeeded,
            PercentComplete = 100,
            ResourcePath = GatewayHarness.ResourcePath(GatewayHarness.TenantA)
        });

        var response = await gateway.SendAsync(
            "GET",
            $"/operations/{TheOperation:D}",
            gateway.Token(GatewayHarness.TenantA)
        );

        response.Body.ShouldContain("\"status\":\"Succeeded\"");
        response.Header(GatewayHeaders.RetryAfter).ShouldBe("");
    }

    [Fact]
    public async Task AnOperationTheCallerMayNotSeeIsTheCanonical404() {
        var gateway = new GatewayHarness();

        gateway.Operations.OnRead = _ => Result<OperationStatus>.Failure(
            ErrorCode.ResourceNotFound,
            "The caller may not read the resource this operation drives."
        );

        var response = await gateway.SendAsync(
            "GET",
            $"/operations/{TheOperation:D}",
            gateway.Token(GatewayHarness.TenantA)
        );

        response.Status.ShouldBe(StatusCodes.Status404NotFound);
        response.Body.ShouldNotContain("may not read");
    }

    /// <summary>
    ///     ⚠ <b>A poll costs one call into the seam, and does not read the operation's resource.</b>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <c>IResourceManager</c> had no <c>GetOperationAsync</c> while
    ///         docs/plan/10 § Long-running operations described the endpoint, so the gateway's reader
    ///         asked the only question the interface could answer: <c>ReadAsync</c> on the operation's
    ///         <i>resource</i>. That was the right refusal to decide anything here and the wrong cost —
    ///         an index resolve, a check, a resource-grain read and an api-version projection on every
    ///         poll, and <c>cyc --wait</c> polls a nine-minute cluster create continuously.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>This asserts the property rather than the shape.</b>
    ///         <c>RecordingResourceManager.Paths</c> records every <i>resource</i> the manager was
    ///         asked about; an empty list is "no resource was read". Put the old two-step back into
    ///         <c>TenantScopedOperationReader</c> and this goes red on that list, not on a signature.
    ///     </para>
    ///     <para>
    ///         ⚠ The other half — that the gateway still decides nothing — is
    ///         <c>GatewayIsolationTests</c>' business and stays there: this reader names no permission,
    ///         no check and no engine, it forwards a caller and copies an answer.
    ///     </para>
    /// </remarks>
    [Fact]
    public async Task PollingAnOperationAsksTheSeamOnceAndNeverReadsTheResource() {
        var manager = new RecordingResourceManager();
        var reader = new TenantScopedOperationReader(manager);

        var caller = new CallerContext {
            TenantId = GatewayHarness.TenantA,
            SubjectType = "user",
            SubjectId = "alice"
        };

        var status = await reader.ReadAsync(caller, TheOperation, TestContext.Current.CancellationToken);

        status.IsSuccess.ShouldBeTrue(status.Error?.Message);
        status.GetValueOrThrow().OperationId.ShouldBe(TheOperation);

        manager.Operations.ShouldHaveSingleItem().ShouldBe(TheOperation);
        manager.Paths.ShouldBeEmpty(
            "a poll must not cost a resource read. IResourceManager.GetOperationAsync runs the "
            + "enforcement seam once, inside the manager, which is where docs/plan/07 § The "
            + "enforcement seam puts it."
        );
    }

    /// <summary>
    ///     Whatever the seam answered, unchanged — including the <c>404</c> that does not disclose
    ///     existence.
    /// </summary>
    [Fact]
    public async Task TheReaderCopiesTheSeamsRefusalRatherThanReinterpretingIt() {
        var manager = new RecordingResourceManager {
            OnGetOperation = id => Result<OperationStatus>.Failure(
                ErrorCode.ResourceNotFound,
                $"Operation {id:D} does not exist."
            )
        };

        var reader = new TenantScopedOperationReader(manager);

        var status = await reader.ReadAsync(
            new() { TenantId = GatewayHarness.TenantA, SubjectType = "user", SubjectId = "alice" },
            TheOperation,
            TestContext.Current.CancellationToken
        );

        status.IsFailure.ShouldBeTrue();
        status.Error!.Code.ShouldBe(ErrorCode.ResourceNotFound);
        manager.Paths.ShouldBeEmpty();
    }
}

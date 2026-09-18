namespace CyberCloud.Sdk.Tests;

/// <summary>A credential that hands out whatever the test tells it to, and counts how often it was asked.</summary>
public sealed class FakeCredential : TokenCredential {
    readonly Func<int, AccessToken> next;
    readonly bool slow;

    int calls;

    public FakeCredential(string token = "token-1", TimeSpan? lifetime = null)
        : this(_ => new AccessToken(token, DateTimeOffset.UtcNow + (lifetime ?? TimeSpan.FromHours(1)))) { }

    public FakeCredential(Func<int, AccessToken> next) {
        this.next = next;
    }

    /// <summary>A credential whose fetch takes long enough for a second caller to arrive during it.</summary>
    public FakeCredential(bool async)
        : this(static _ => new AccessToken("token-1", DateTimeOffset.UtcNow.AddHours(1))) {
        slow = async;
    }

    /// <summary>How many times the pipeline asked for a token. The evidence that a refresh happened.</summary>
    public int Calls => Volatile.Read(ref calls);

    public override async ValueTask<AccessToken> GetTokenAsync(
        TokenRequestContext context,
        CancellationToken cancellationToken = default
    ) {
        var call = Interlocked.Increment(ref calls);

        if (slow) {
            await Task.Delay(50, cancellationToken);
        }

        return next(call);
    }
}

/// <summary>Builds a client over a scripted transport. The one place the tests wire the SDK up.</summary>
public static class TestClient {
    public const string Scope = "/tenants/t/subscriptions/s/resourceGroups/prod";

    public static CyberCloudClient Create(
        ScriptedTransport transport,
        TokenCredential? credential = null,
        Action<CyberCloudClientOptions>? configure = null
    ) {
        var options = new CyberCloudClientOptions {
            Transport = transport,
            // ⚠ Zero, so a three-poll operation costs three round trips and no wall-clock time. It is
            // the documented meaning of the option, not a test-only escape — see
            // CyberCloudClientOptions.PollingInterval.
            PollingInterval = TimeSpan.Zero
        };

        // The retry backoff has to be small too, or a 5xx test spends a second per attempt.
        options.Retry.Delay = TimeSpan.FromMilliseconds(1);
        options.Retry.MaxDelay = TimeSpan.FromMilliseconds(5);

        configure?.Invoke(options);

        return new(
            new Uri("https://api.cybercloud.test/"),
            credential ?? new FakeCredential(),
            options
        );
    }

    public static WidgetCollection Widgets(this CyberCloudClient client) => new(client.Context, Scope);

    public static WidgetData SampleData() =>
        new("eu-central") { Properties = new() { ClusterId = "cluster-1", Message = "hello" } };

    public const string OperationUri = "https://api.cybercloud.test/operations/op-1?api-version=2026-08-01";

    /// <summary>The path of the widget every scripted read serves.</summary>
    public const string WidgetPath = Scope + "/providers/CyberCloud.Sample/widgets/main";

    /// <summary>
    ///     One widget, member for member as <c>ResponseBodies.Resource</c> serves it: the five
    ///     envelope members the server owns, <c>location</c>, the body, <c>tags</c>.
    /// </summary>
    /// <remarks>
    ///     ⚠ It carried the body alone — <c>location</c> and <c>properties</c> — until the
    ///     2026-09-15 review of issue #85, which is why nothing noticed that the stand-in dropped
    ///     the envelope on every read: no scripted response had one to drop.
    /// </remarks>
    public static string WidgetBody => WidgetNamed("main");

    /// <summary>A served widget with the given name, in the same shape as <see cref="WidgetBody" />.</summary>
    /// <param name="name">The last path segment; the id is built from it under <see cref="Scope" />.</param>
    /// <param name="location">The <c>location</c> served, in the envelope and nowhere else — issue #72.</param>
    /// <param name="state">The <c>provisioningState</c> served, as the wire spells it.</param>
    public static string WidgetNamed(string name, string location = "eu-central", string state = "Succeeded") =>
        $$$"""
           {"id":"{{{Scope}}}/providers/CyberCloud.Sample/widgets/{{{name}}}","name":"{{{name}}}","type":"CyberCloud.Sample/widgets","location":"{{{location}}}","provisioningState":"{{{state}}}","etag":"etag-7","properties":{"clusterId":"cluster-1","message":"hello"},"tags":{}}
           """;
}

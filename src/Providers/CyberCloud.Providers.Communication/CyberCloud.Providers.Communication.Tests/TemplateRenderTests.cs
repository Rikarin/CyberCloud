using System.Collections.Immutable;
using System.Text.Json;

namespace CyberCloud.Providers.Communication.Tests;

/// <summary>
///     <c>render</c>: pure over the template's own body, refusing a missing variable by name.
/// </summary>
public sealed class TemplateRenderTests {
    static readonly TemplateRenderHandler Handler = new();

    [Fact]
    public async Task RendersSubjectAndBodyWithTheArgumentsSubstituted() {
        using var desired = JsonDocument.Parse(
            CommunicationTemplates.Body(
                body: "Your code is {code}. It expires in {minutes} minutes.",
                subject: "Code {code}",
                locale: "en-GB",
                variables: ["code"],
                optionalVariables: ["minutes"]
            )
        );

        using var request = JsonDocument.Parse("""{"arguments":["code=482913","minutes=10"]}""");

        var rendered = await Handler.InvokeAsync(Context(desired.RootElement, request.RootElement), TestContext.Current.CancellationToken);

        rendered.IsSuccess.ShouldBeTrue(rendered.Error?.Message);

        using var body = JsonDocument.Parse(rendered.GetValueOrThrow());
        body.RootElement.GetProperty("subject").GetString().ShouldBe("Code 482913");
        body.RootElement.GetProperty("body").GetString().ShouldBe("Your code is 482913. It expires in 10 minutes.");
        body.RootElement.GetProperty("locale").GetString().ShouldBe("en-GB");
        CommunicationTemplates.RenderResponse.Validate(body.RootElement).IsSuccess.ShouldBeTrue("the render body does not match the published response shape");
    }

    [Fact]
    public async Task AMissingRequiredVariableIsRefusedNamingIt() {
        using var desired = JsonDocument.Parse(CommunicationTemplates.Body(body: "Hello {name}, your code is {code}.", variables: ["name", "code"]));
        using var request = JsonDocument.Parse("""{"arguments":["code=1"]}""");

        var refused = await Handler.InvokeAsync(Context(desired.RootElement, request.RootElement), TestContext.Current.CancellationToken);

        refused.IsFailure.ShouldBeTrue("a render with a missing required variable returned a body");
        refused.Error!.Code.ShouldBe(ErrorCode.InvalidRequestBody);
        refused.Error.Message.ShouldContain("'name'");
        refused.Error.Message.ShouldContain("requires 'name'", customMessage: "the refusal names the missing variable, not the supplied one");
    }

    [Fact]
    public async Task AnUnsuppliedOptionalVariableIsLeftAsWrittenSoTheTenantSeesIt() {
        using var desired = JsonDocument.Parse(CommunicationTemplates.Body(body: "Code {code}; expires in {minutes}.", variables: ["code"], optionalVariables: ["minutes"]));
        using var request = JsonDocument.Parse("""{"arguments":["code=7"]}""");

        var rendered = await Handler.InvokeAsync(Context(desired.RootElement, request.RootElement), TestContext.Current.CancellationToken);

        using var body = JsonDocument.Parse(rendered.GetValueOrThrow());
        body.RootElement.GetProperty("body").GetString().ShouldBe("Code 7; expires in {minutes}.");
    }

    [Fact]
    public async Task AValueContainingAnEqualsSignKeepsEverythingAfterTheFirst() {
        using var desired = JsonDocument.Parse(CommunicationTemplates.Body(body: "Link: {url}", variables: ["url"]));
        using var request = JsonDocument.Parse("""{"arguments":["url=https://example.com/reset?token=a=b"]}""");

        var rendered = await Handler.InvokeAsync(Context(desired.RootElement, request.RootElement), TestContext.Current.CancellationToken);

        using var body = JsonDocument.Parse(rendered.GetValueOrThrow());
        body.RootElement.GetProperty("body").GetString().ShouldBe("Link: https://example.com/reset?token=a=b");
    }

    [Fact]
    public void ParametersAreRequiredFirstThenOptionalAndNeverTwice() {
        using var desired = JsonDocument.Parse(CommunicationTemplates.Body(variables: ["code", "name"], optionalVariables: ["name", "minutes"]));

        var parameters = CommunicationTemplates.ParametersOf(desired.RootElement);

        parameters.Select(x => (x.Name, x.Required))
            .ShouldBe([("code", true), ("name", true), ("minutes", false)], customMessage: "a name listed as both is required — the stricter reading");
    }

    [Fact]
    public void MatchesComparesContentAndChannelAndNeverTheVersionNumber() {
        using var desired = JsonDocument.Parse(CommunicationTemplates.Body(channel: "sms", body: "Hi {name}", subject: "", variables: ["name"]));

        var version = new MessageTemplateVersion {
            Version = 17,
            Channel = ChannelKind.Sms,
            Parameters = [new() { Name = "name", Required = true }],
            Bodies = [new() { Locale = "en", Subject = "", Body = "Hi {name}" }]
        };

        CommunicationTemplates.Matches(version, desired.RootElement).ShouldBeTrue();
        CommunicationTemplates.Matches(version with { Channel = ChannelKind.Email }, desired.RootElement).ShouldBeFalse();
        CommunicationTemplates.Matches(version with { Bodies = [new() { Locale = "en", Body = "Hello {name}" }] }, desired.RootElement).ShouldBeFalse();
        CommunicationTemplates.Matches(version with { Parameters = ImmutableArray<TemplateParameter>.Empty }, desired.RootElement).ShouldBeFalse();
    }

    static ActionContext Context(JsonElement desired, JsonElement request) =>
        new(
            CommunicationTestCluster.Child(CommunicationTemplates.Type, "svc", "welcome"),
            CommunicationServices.V2026,
            CommunicationTemplates.RenderAction,
            request,
            desired,
            string.Empty,
            null,
            new CyberCloud.ResourceManager.Conformance.InMemorySecretVault()
        );
}

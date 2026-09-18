using CyberCloud.ResourceGraph.Query;

namespace CyberCloud.ResourceGraph.Tests.Query;

/// <summary>The <c>$skipToken</c>: an offset tied to one query's text.</summary>
public sealed class ResourceGraphContinuationTests {
    const string Query = "resources | project name";

    [Fact]
    public void ATokenRoundTripsItsOffsetForTheQueryThatMintedIt() {
        var token = ResourceGraphContinuation.Encode(Query, 150);

        token.ShouldStartWith("150.");
        ResourceGraphContinuation.Decode(Query, token).GetValueOrThrow().ShouldBe(150);
        ResourceGraphContinuation.Decode(Query, "").GetValueOrThrow().ShouldBe(0, "no token is the first page");
    }

    [Fact]
    public void ATokenPresentedWithAnotherQueryIsRefused() {
        var token = ResourceGraphContinuation.Encode(Query, 50);
        var refused = ResourceGraphContinuation.Decode(Query + " | take 10", token);

        refused.IsFailure.ShouldBeTrue();
        refused.Error!.Code.ShouldBe(ErrorCode.InvalidRequestBody);
        refused.Error.Message.ShouldContain("different query");
    }

    [Theory]
    [InlineData("garbage")]
    [InlineData(".abcdef0123456789")]
    [InlineData("-5.abcdef0123456789")]
    [InlineData("50")]
    [InlineData("fifty.abcdef0123456789")]
    public void AMalformedTokenIsRefusedWithoutAQueryRunning(string token) {
        var refused = ResourceGraphContinuation.Decode(Query, token);

        refused.IsFailure.ShouldBeTrue($"'{token}' was accepted");
        refused.Error!.Code.ShouldBe(ErrorCode.InvalidRequestBody);
        refused.Error.Message.ShouldContain("$skipToken");
    }
}

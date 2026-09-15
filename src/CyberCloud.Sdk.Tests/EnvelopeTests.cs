namespace CyberCloud.Sdk.Tests;

/// <summary>
///     The read envelope reaches the caller — issue #85's .NET half, which the 2026-09-15 review
///     found declared and never populated.
/// </summary>
/// <remarks>
///     ⚠ <b>Every assertion here was false before that review, and no test noticed.</b> The
///     stand-in declared the five members, every call site built the resource from the body alone,
///     and <c>TestClient.WidgetBody</c> carried no envelope for a reader to drop. The three routes
///     a resource reaches a caller by — a <c>GET</c>, a list element, the value of a completed
///     operation — are each read back here, from a body in the shape <c>ResponseBodies.Resource</c>
///     actually serves.
/// </remarks>
public sealed class EnvelopeTests {
    static void IsTheServedWidget(WidgetResource resource) {
        resource.Id.ShouldBe(TestClient.WidgetPath);
        resource.Name.ShouldBe("main");
        resource.Type.ShouldBe("CyberCloud.Sample/widgets");
        resource.ProvisioningState.ShouldBe(ProvisioningState.Succeeded);
        resource.Etag.ShouldBe("etag-7");

        // The id is the URL — the document's own description of it — and the body is still there.
        resource.Uri.ShouldBe(new Uri("https://api.cybercloud.test" + TestClient.WidgetPath));
        resource.Data.Location.ShouldBe("eu-central");
        resource.Data.Properties!.Message.ShouldBe("hello");
    }

    [Fact]
    public async Task A_GET_carries_the_five_members_the_server_owns() {
        var transport = new ScriptedTransport((request, index) => Responses.Json(HttpStatusCode.OK, TestClient.WidgetBody));
        using var client = TestClient.Create(transport);

        var response = await client.Widgets().GetAsync("main", Cancel.Token);

        IsTheServedWidget(response.Value);
    }

    [Fact]
    public async Task A_list_element_carries_them_too_and_its_URL_comes_from_its_id() {
        var transport = new ScriptedTransport((request, index) => Responses.Json(
                HttpStatusCode.OK,
                $$"""{"value":[{{TestClient.WidgetNamed("main")}},{{TestClient.WidgetNamed("other", state: "Deleting")}}]}"""
            )
        );
        using var client = TestClient.Create(transport);

        var widgets = new List<WidgetResource>();

        await foreach (var widget in client.Widgets().GetAll(Cancel.Token)) {
            widgets.Add(widget);
        }

        widgets.Count.ShouldBe(2);
        IsTheServedWidget(widgets[0]);

        // ⚠ Deleting is a state a listing still shows — the document's own remark on the member.
        widgets[1].Name.ShouldBe("other");
        widgets[1].ProvisioningState.ShouldBe(ProvisioningState.Deleting);
        widgets[1].Uri.AbsolutePath.ShouldEndWith("/widgets/other");
    }

    [Fact]
    public async Task A_completed_operation_s_value_carries_them() {
        var transport = new ScriptedTransport((request, index) => index switch {
                0 => Responses.Accepted(TestClient.OperationUri),
                1 => Responses.Operation("Succeeded", []),
                _ => Responses.Json(HttpStatusCode.OK, TestClient.WidgetBody),
            }
        );
        using var client = TestClient.Create(transport);

        var operation = await client.Widgets()
            .CreateOrUpdateAsync(WaitUntil.Completed, "main", TestClient.SampleData(), Cancel.Token);

        IsTheServedWidget(operation.Value);
    }

    /// <summary>
    ///     ⚠ The enum is read through the emitter's <c>[JsonStringEnumMemberName]</c>, not through
    ///     the member's own name — the two happen to coincide for <c>ProvisioningState</c> and would
    ///     not for a body enum like <c>m1.2xlarge</c>, so the mechanism is the thing under test.
    ///     A value this api-version's document does not declare is refused rather than read as
    ///     <c>Unknown</c>.
    /// </summary>
    [Fact]
    public async Task A_state_the_document_does_not_declare_is_refused() {
        var transport = new ScriptedTransport((request, index) => Responses.Json(
                HttpStatusCode.OK,
                TestClient.WidgetNamed("main", state: "Provisioning")
            )
        );
        using var client = TestClient.Create(transport);

        await Should.ThrowAsync<System.Text.Json.JsonException>(async () =>
            await client.Widgets().GetAsync("main", Cancel.Token)
        );
    }
}

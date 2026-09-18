using System.Collections.Immutable;
using System.Text;
using System.Text.Json;

namespace CyberCloud.ResourceGraph.Tests;

/// <summary>The event on the wire, and one row in and out of ClickHouse's JSON.</summary>
public sealed class ResourceChangedJsonTests {
    static ResourceChangedEvent Full() =>
        new() {
            Change = ResourceChangeKind.Updated,
            ResourceId = Guid.Parse("aaaaaaaa-0000-4000-8000-00000000000a"),
            TenantId = Guid.Parse("11111111-1111-4111-8111-111111111111"),
            SubscriptionId = Guid.Parse("33333333-3333-4333-8333-333333333333"),
            ResourceGroup = "prod",
            Provider = "CyberCloud.Testing",
            Type = "widgets",
            Name = "emitted",
            ApiVersion = "2026-08-01",
            ProvisioningState = ProvisioningState.Succeeded,
            Location = "eu-central",
            ClusterId = Guid.Parse("44444444-4444-4444-8444-444444444444"),
            Tags = ImmutableDictionary<string, string>.Empty.Add("env", "prod").Add("owner", "alice"),
            CreatedAt = new DateTimeOffset(2026, 9, 17, 10, 0, 0, 123, TimeSpan.Zero),
            ModifiedAt = new DateTimeOffset(2026, 9, 17, 11, 30, 0, 456, TimeSpan.FromHours(2)),
            DesiredHash = "sha256:abc",
            Version = 42
        };

    [Fact]
    public void AnEventRoundTripsWithEnumsByNameAndTheComputedPropertiesLeftOut() {
        var bytes = ResourceChangedJson.Encode(Full());
        var text = Encoding.UTF8.GetString(bytes);

        // Names, not numbers: a consumer that is not this assembly reads "Updated", and a
        // renumbering is a visible break rather than a silent one.
        text.ShouldContain("\"change\":\"Updated\"");
        text.ShouldContain("\"provisioningState\":\"Succeeded\"");
        text.ShouldNotContain("subject", Case.Insensitive, "the subject is recomputed from the fields, never trusted from the body");
        text.ShouldNotContain("streamNamespace");

        var decoded = ResourceChangedJson.Decode(bytes).GetValueOrThrow();

        decoded.ShouldBe(Full() with { Tags = decoded.Tags }, "every scalar member survives");
        decoded.Tags.Count.ShouldBe(2);
        decoded.Tags["env"].ShouldBe("prod");
        decoded.ModifiedAt.ShouldBe(Full().ModifiedAt, "an offset is preserved, not folded to UTC");
        decoded.Subject.ShouldBe(Full().Subject);
    }

    [Fact]
    public void BytesThatAreNotAnEventAreARefusalNotAThrow() {
        // The projector terminates such a message rather than redelivering it forever; that needs a
        // Result, not an exception it would have to catch by type.
        ResourceChangedJson.Decode("not json"u8.ToArray()).IsFailure.ShouldBeTrue();
        ResourceChangedJson.Decode("null"u8.ToArray()).IsFailure.ShouldBeTrue();
        ResourceChangedJson.Decode("{}"u8.ToArray()).IsSuccess.ShouldBeTrue("an empty object is an event with defaults; the projector refuses it for its empty ids, not here");
    }

    [Fact]
    public void ARowRoundTripsClickHousesOwnSpellingOfATimestampAndAQuotedInteger() {
        // ⚠ ClickHouse writes DateTime64 as `2026-09-17 10:00:00.123` — a space, no offset — and
        // quotes a UInt64 unless asked not to. Both forms come back from a server that ignored the
        // request's settings, and the reader must not depend on which the server did.
        const string body =
            """
            {"resource_id":"aaaaaaaa-0000-4000-8000-00000000000a","tenant_id":"11111111-1111-4111-8111-111111111111","subscription_id":"33333333-3333-4333-8333-333333333333","resource_group":"prod","provider":"CyberCloud.Testing","type":"widgets","name":"emitted","api_version":"2026-08-01","provisioning_state":"Succeeded","location":"eu-central","cluster_id":"00000000-0000-0000-0000-000000000000","tags":{"env":"prod"},"created_at":"2026-09-17 10:00:00.123","modified_at":"2026-09-17 11:30:00.456","desired_hash":"sha256:abc","version":42,"change":"Updated","is_deleted":0,"access":["user:alice","group:eng#member"],"projected_at":"2026-09-17 11:30:01.000"}

            """;

        var row = ResourceGraphJson.DecodeFirstRow(body).ShouldNotBeNull();

        row.ResourceId.ShouldBe(Guid.Parse("aaaaaaaa-0000-4000-8000-00000000000a"));
        row.CreatedAt.ShouldBe(new DateTimeOffset(2026, 9, 17, 10, 0, 0, 123, TimeSpan.Zero));
        row.ModifiedAt.ShouldBe(new DateTimeOffset(2026, 9, 17, 11, 30, 0, 456, TimeSpan.Zero));
        row.Version.ShouldBe(42);
        row.Access.ShouldBe(["user:alice", "group:eng#member"]);
        row.Tags["env"].ShouldBe("prod");

        ResourceGraphJson.DecodeNumber("{\"version\":\"42\"}\n", "version").ShouldBe(42, "a quoted 64-bit integer is still the number");
        ResourceGraphJson.DecodeNumber("{\"version\":42}\n", "version").ShouldBe(42);
        ResourceGraphJson.DecodeNumber("", "version").ShouldBeNull("no rows is no number");

        // And what goes in is ISO 8601 in UTC, with the offset applied, so best_effort parsing has
        // nothing ambiguous to read.
        var encoded = ResourceGraphJson.EncodeRow(row with { ModifiedAt = new DateTimeOffset(2026, 9, 17, 11, 30, 0, 456, TimeSpan.FromHours(2)) });
        encoded.ShouldContain("\"modified_at\":\"2026-09-17T09:30:00.456Z\"");
        encoded.ShouldContain("\"is_deleted\":0");
        JsonDocument.Parse(encoded).RootElement.GetProperty("access").GetArrayLength().ShouldBe(2);
    }

    [Fact]
    public void TheDdlNamesEveryColumnTheRowWrites() {
        // The INSERT is FORMAT JSONEachRow, so a column the row writes and the table lacks is a
        // refused insert at runtime and nowhere else. This is the compiler for that.
        var tenant = Guid.Parse("11111111-1111-4111-8111-111111111111");
        var ddl = ResourceGraphTable.CreateTable(tenant);
        var written = JsonDocument.Parse(ResourceGraphJson.EncodeRow(new ResourceGraphRow())).RootElement;

        foreach (var column in written.EnumerateObject()) {
            ddl.ShouldContain("\n    " + column.Name + " ", customMessage: $"the table has no column for the row's '{column.Name}'");
        }

        ResourceGraphTable.Database(tenant).ShouldBe("tenant_11111111111141118111111111111111");
        ddl.ShouldContain("ENGINE = ReplacingMergeTree(version)");
        ddl.ShouldContain("ORDER BY resource_id");
    }
}

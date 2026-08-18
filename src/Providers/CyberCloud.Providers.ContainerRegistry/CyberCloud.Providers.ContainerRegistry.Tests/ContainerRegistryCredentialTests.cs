using CyberCloud.ResourceManager.Conformance;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.ContainerRegistry.Tests;

/// <summary>
///     The six credentials a registry has: where they come from, where they go, and the one published
///     constant that must never appear.
/// </summary>
/// <remarks>
///     ⚠ <b>This file exists because of failure class (c), and this row's instance of it is not an
///     absence — it is a published constant.</b> The three earlier sightings are things that are
///     <i>unset</i>: SeaweedFS with no identities file serves anonymous admin, Qdrant's chart leaves
///     <c>service.api_key</c> unset, MariaDB's operator generates a root password.
///     <c>goharbor/harbor-helm</c>'s <c>values.yaml</c> ships <c>harborAdminPassword: "Harbor12345"</c>
///     and consumes it with no generation fallback, while randomising every other credential in the
///     same template.
/// </remarks>
public sealed class ContainerRegistryCredentialTests {
    /// <summary>
    ///     The password <c>goharbor/harbor-helm</c> ships as a live default. ⚠ A literal on purpose.
    /// </summary>
    /// <remarks>
    ///     ⚠ It is written out here so that the assertions below have something to search for. Deriving
    ///     it from anything would make this file test itself.
    /// </remarks>
    const string UpstreamChartsPublishedPassword = "Harbor12345";

    [Fact]
    public async Task NoRenderedObjectCarriesTheUpstreamChartsPublishedPassword() {
        // ⚠ THE ASSERTION THIS ROW MOST NEEDS, AND IT IS CHEAP. `goharbor/harbor-helm`'s values.yaml
        // line 295 is `harborAdminPassword: "Harbor12345"` and its core-secret template consumes it
        // with NO generation fallback — while, in the same file, `secret`, `CSRF_KEY`,
        // `JOBSERVICE_SECRET` and `REGISTRY_HTTP_SECRET` all end in `| default (randAlphaNum 16)`. The
        // administrator's password is the ONE credential in that chart that is not randomised, so the
        // natural way to get a Harbor running is a registry reachable at admin/Harbor12345.
        //
        // This is the test that goes red the day somebody "just gets it working".
        var connection = new RecordingConnection();
        using var body = JsonDocument.Parse(ContainerRegistries.Body(ClusterId));

        await new ContainerRegistryReconciler(new FixedClockForCredentials()).ReconcileAsync(
            ContainerRegistryReconcilerTests.Context(connection, body.RootElement),
            TestContext.Current.CancellationToken
        );

        connection.Applied.ShouldNotBeEmpty();

        foreach (var command in connection.Applied) {
            command.Body.ShouldNotContain(
                UpstreamChartsPublishedPassword,
                Case.Sensitive,
                $"'{command.Target.Name}' carries the password goharbor/harbor-helm publishes in its "
                + "own values.yaml. Every credential this type has is minted into the tenant's vault."
            );

            // ⚠ Base64 as well, because the credentials Secret carries `data` rather than `stringData`
            // and a plain-text search would miss it there.
            command.Body.ShouldNotContain(
                Convert.ToBase64String(Encoding.UTF8.GetBytes(UpstreamChartsPublishedPassword)),
                Case.Sensitive,
                $"'{command.Target.Name}' carries the upstream chart's published password, base64 "
                + "encoded"
            );
        }
    }

    [Fact]
    public async Task EveryCredentialReachesAWorkloadAsASecretKeyRefAndNeverAsAValue() {
        // ⚠ TWO THINGS AT ONCE, AND BOTH MATTER. A rendered value would put the administrator's
        // password into a Deployment's spec, readable by anyone holding `get deployments` — a strictly
        // weaker right than `get secrets`. And it would make the fourteen non-Secret documents depend
        // on what the vault holds, which is what `ASecondPassWithTheSameBodyChangesNothing` proves they
        // do not.
        var connection = new RecordingConnection();
        var vault = new InMemorySecretVault();
        using var body = JsonDocument.Parse(ContainerRegistries.Body(ClusterId));

        await new ContainerRegistryReconciler(new FixedClockForCredentials()).ReconcileAsync(
            ContainerRegistryReconcilerTests.Context(connection, body.RootElement, vault),
            TestContext.Current.CancellationToken
        );

        var path = ContainerRegistries.SecretPath(
            ContainerRegistryReconcilerTests.Address(
                "observed",
                Guid.Parse("11111111-1111-4111-8111-11111111111c"),
                Guid.Parse("22222222-2222-4222-8222-22222222222c")
            )
        );

        foreach (var field in ContainerRegistries.CredentialFields) {
            var value = vault.Peek(path, field);

            value.ShouldNotBeNull($"'{field}' was not minted at all");

            foreach (var command in connection.Applied.Where(x => x.Target.Kind.Kind != "Secret")) {
                command.Body.ShouldNotContain(
                    value,
                    Case.Sensitive,
                    $"'{command.Target.Name}' carries the value of '{field}' rather than a "
                    + "secretKeyRef to it"
                );
            }
        }
    }

    [Fact]
    public async Task TheCredentialsSecretCarriesAllSixFieldsAndTheVaultHoldsTheSameSix() {
        // ⚠ THE OTHER HALF OF THE TEST ABOVE, AND WITHOUT IT THAT ONE IS SATISFIED BY A RECONCILER THAT
        // MINTS AND THEN RENDERS NOTHING. A Secret applied with five of six fields is six components
        // where one of them cannot resolve its secretKeyRef, which is a pod stuck in
        // CreateContainerConfigError — and nothing else in the suite would say which field.
        var connection = new RecordingConnection();
        var vault = new InMemorySecretVault();
        using var body = JsonDocument.Parse(ContainerRegistries.Body(ClusterId));

        await new ContainerRegistryReconciler(new FixedClockForCredentials()).ReconcileAsync(
            ContainerRegistryReconcilerTests.Context(connection, body.RootElement, vault),
            TestContext.Current.CancellationToken
        );

        var rendered = connection.Applied.Single(x => x.Target.Kind.Kind == "Secret").Body;
        var data = JsonNode.Parse(rendered)!["data"]!.AsObject();

        var path = ContainerRegistries.SecretPath(
            ContainerRegistryReconcilerTests.Address(
                "observed",
                Guid.Parse("11111111-1111-4111-8111-11111111111c"),
                Guid.Parse("22222222-2222-4222-8222-22222222222c")
            )
        );

        data.Count.ShouldBe(ContainerRegistries.CredentialFields.Length);

        foreach (var field in ContainerRegistries.CredentialFields) {
            // ⚠ Decoded, because the Secret carries `data` rather than `stringData` — see
            // ContainerRegistries.CredentialsSecretJson for why. A test that searched the base64 for a
            // plain string would pass whatever the reconciler wrote.
            var encoded = data[field]?.GetValue<string>();

            encoded.ShouldNotBeNullOrEmpty(field);

            Encoding.UTF8.GetString(Convert.FromBase64String(encoded!)).ShouldBe(
                vault.Peek(path, field),
                $"the rendered Secret's '{field}' is not the value the vault holds, so the component "
                + "that reads it authenticates against something the platform cannot hand out"
            );
        }
    }

    [Fact]
    public void TheCsrfKeyIsExactlyThirtyTwoCharactersAndTheOthersAreLonger() {
        // ⚠ HARBOR REFUSES TO START ON ANY OTHER LENGTH, because CSRF_KEY is an AES-256 key. A value of
        // the wrong size is a core that crash-loops with a message about key size, per pod, after the
        // caller was told 202 — and the resource then never converges for a reason no reconciler sees.
        var generated = ContainerRegistries.GenerateCredentials();

        generated[ContainerRegistries.CsrfKeyField].Length.ShouldBe(32);

        foreach (var field in ContainerRegistries.CredentialFields) {
            generated.ShouldContainKey(field);
            generated[field].ShouldAllBe(x => ContainerRegistries.PasswordAlphabet.Contains(x, StringComparison.Ordinal));
        }
    }

    [Fact]
    public void TwoCallsGenerateDifferentCredentialsAndThatIsWhatMintOnceIsFor() {
        // ⚠ Reads like a clause-1 violation and is not — see GenerateCredentials' own remarks. The
        // alternative is deriving a password from the resource id, which is an administrator credential
        // anyone who knows a GUID can compute.
        var first = ContainerRegistries.GenerateCredentials();
        var second = ContainerRegistries.GenerateCredentials();

        foreach (var field in ContainerRegistries.CredentialFields) {
            first[field].ShouldNotBe(second[field], field);
        }
    }

    [Fact]
    public void TheVaultPathIsKeyedOnTheGuidRatherThanTheName() {
        // ⚠ docs/plan/06 § Identifiers makes a name reusable, so a path built from the name would hand
        // a brand-new registry the credentials of one somebody deleted — and mint-once would make that
        // permanent. ⚠ On a SOFT-DELETABLE type the name is held for the whole window rather than
        // released immediately, which makes the GUID more necessary rather than less: a restore has to
        // find the same credential the tenant was using.
        var first = ContainerRegistries.SecretPath(Address(Guid.Parse("aaaaaaaa-0000-4000-8000-000000000001")));
        var second = ContainerRegistries.SecretPath(Address(Guid.Parse("bbbbbbbb-0000-4000-8000-000000000002")));

        first.ShouldNotBe(second);
        first.ShouldContain("aaaaaaaa-0000-4000-8000-000000000001");
        first.ShouldStartWith("tenants/", Case.Sensitive);
        first.ShouldContain(ContainerRegistries.ProviderNamespace);
    }

    static readonly Guid ClusterId = Guid.Parse("eeeeeeee-0000-4000-8000-00000000000c");

    static ResourceId Address(Guid id) =>
        new(
            Guid.Parse("11111111-1111-4111-8111-11111111111c"),
            Guid.Parse("22222222-2222-4222-8222-22222222222c"),
            "prod",
            ContainerRegistries.Type,
            "images",
            id
        );
}

/// <summary>A clock that does not move.</summary>
sealed class FixedClockForCredentials : CyberCloud.Core.Time.IClock {
    public DateTimeOffset UtcNow => new(2026, 8, 18, 12, 0, 0, TimeSpan.Zero);
}

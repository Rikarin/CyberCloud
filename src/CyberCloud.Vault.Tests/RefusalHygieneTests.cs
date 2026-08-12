namespace CyberCloud.Vault.Tests;

/// <summary>
///     What a tenant is allowed to read when a secret cannot be resolved, and what the operator gets
///     instead.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>THE FAILURE CLASS IS A FAILED <see cref="Result{T}" /> THAT NOBODY REDACTS.</b> A
///         refusal out of <see cref="OpenBaoSecretResolver" /> becomes
///         <c>ReconcileOutcome.Failed</c>, streams to <c>operation-progress</c> and lands in
///         <c>ResourceSnapshot.LastFailure</c>, which the portal renders. Nothing on that path
///         removes anything, so the message is tenant-visible by construction.
///     </para>
///     <para>
///         ⚠ <b>The forbidden-substring shape is
///         <c>KubeFailureMappingTests.AnRbacRefusalNamesNothingInternalToTheTenantAndEverythingToTheOperator</c>'s,
///         copied deliberately.</b> That test asserts an RBAC denial names nothing internal —
///         <c>system:serviceaccount</c>, <c>RBAC</c>, <c>clusterrole</c> — while the operator log
///         gets all of it. The vault's list is the same idea over a different vocabulary: an address,
///         a mount, a path, a field, a role, a namespace and the words that would tell a tenant which
///         product the platform's secret store is.
///     </para>
///     <para>
///         ⚠ <b>Asserted over <i>every</i> builder rather than over the ones a test happened to
///         drive.</b> A sixth failure mode added later gets the check for free, which is the
///         difference between a rule and five examples of one.
///     </para>
/// </remarks>
public sealed class RefusalHygieneTests {
    const string Address = "https://openbao.cc-vault.svc:8200";
    const string Mount = "platform-kv";
    const string Role = "cc-silo-reader";

    static readonly SecretRef Handle = new() {
        Path = "tenants/9f2b/postgres/main",
        Field = "adminPassword",
        Version = "7",
    };

    /// <summary>Every refusal this assembly can produce, with realistic arguments.</summary>
    /// <remarks>
    ///     ⚠ A method rather than a field, because <see cref="Handle" /> and the constants have to be
    ///     the same strings the forbidden list is built from — a fixture that drifted from the list
    ///     would pass by naming something the list does not know about.
    /// </remarks>
    public static TheoryData<string, VaultRefusal> EveryRefusal() =>
        new() {
            { nameof(VaultFailures.EmptyHandle), VaultFailures.EmptyHandle(new() { Path = Handle.Path }) },
            {
                nameof(VaultFailures.AuthenticationFailed),
                VaultFailures.AuthenticationFailed(
                    $"OpenBao at {Address} refused the login as role '{Role}' with HTTP 403"
                )
            },
            { nameof(VaultFailures.Unreachable), VaultFailures.Unreachable($"{Address} could not be reached") },
            { nameof(VaultFailures.NotFound), VaultFailures.NotFound(Handle, Address, Mount) },
            {
                nameof(VaultFailures.PermissionDenied),
                VaultFailures.PermissionDenied(Handle, Address, Mount, Role)
            },
            {
                nameof(VaultFailures.FieldMissing),
                VaultFailures.FieldMissing(Handle, Address, Mount, ["password", "username"])
            },
            { nameof(VaultFailures.Unreadable), VaultFailures.Unreadable("it is not JSON") },
        };

    [Theory]
    [MemberData(nameof(EveryRefusal))]
    public void ATenantMessageNamesNothingInternal(string builder, VaultRefusal refusal) {
        foreach (var leak in new[] {
                     // The vault itself, and which product it is.
                     "openbao", "bao", "hashicorp", "kv-v2", "x-vault", "8200",
                     // Where it is and how the platform gets in.
                     "openbao.cc-vault.svc", "cc-vault", "https://", "http://", Role, "kubernetes",
                     "serviceaccount", "service-account", "token", "login", "policy", "lease",
                     // What was asked for.
                     Handle.Path, Handle.Field, Mount, "tenants/", "version",
                     // The status codes, which say more than a tenant needs and less than is true.
                     "403", "404", "http ",
                 }) {
            refusal.TenantMessage.ShouldNotContain(
                leak,
                Case.Insensitive,
                $"VaultFailures.{builder}'s tenant message names '{leak}', and that message is "
                + "rendered in the portal against the tenant's own resource"
            );
        }
    }

    [Theory]
    [MemberData(nameof(EveryRefusal))]
    public void ATenantMessageStillSaysEnoughToActOn(string builder, VaultRefusal refusal) {
        // ⚠ The other half, and the reason the list above is not simply "say nothing". A refusal a
        // tenant cannot act on is a support ticket; the one thing they can act on is knowing this is
        // not their request's fault. That is what the shared closing sentence carries.
        refusal.TenantMessage.ShouldContain(
            VaultFailures.Escalation,
            Case.Sensitive,
            $"VaultFailures.{builder} must tell the tenant an operator is needed"
        );

        refusal.TenantMessage.Length.ShouldBeGreaterThan(
            VaultFailures.Escalation.Length,
            $"VaultFailures.{builder} must say something before the escalation sentence"
        );
    }

    [Theory]
    [MemberData(nameof(EveryRefusal))]
    public void AnOperatorDetailIsAlwaysRicherThanWhatTheTenantSees(string builder, VaultRefusal refusal) {
        refusal.OperatorDetail.ShouldNotBe(
            refusal.TenantMessage,
            $"VaultFailures.{builder} hands the operator exactly what it hands the tenant, so one of "
            + "the two is wrong: either the tenant is reading platform internals or the operator is "
            + "being told nothing"
        );
    }

    [Fact]
    public void TheOperatorDetailNamesTheHandleAndTheVault() {
        // ⚠ The positive half, and it is the assertion that would catch over-correction. Redaction
        // has a natural end state where every message says "a secret could not be read" and an
        // operator at 03:00 cannot tell which of ten thousand handles it was.
        var refusal = VaultFailures.NotFound(Handle, Address, Mount);

        refusal.OperatorDetail.ShouldContain(Handle.Path);
        refusal.OperatorDetail.ShouldContain(Address);
        refusal.OperatorDetail.ShouldContain(Mount);
        refusal.OperatorDetail.ShouldContain(Handle.Version, Case.Sensitive);
    }

    [Fact]
    public void ARefusedLoginAndARefusedReadAreNotTheSameFault() {
        // ⚠ BOTH ARE 403 FROM OPENBAO AND THEY SEND AN OPERATOR TO DIFFERENT PLACES. A refused login
        // means this pod's service account is not what the role is bound to, which breaks every
        // secret on the silo. A refused read means the policy on a perfectly good token does not
        // cover one path. Folding them together sends somebody to reconfigure a role that is fine.
        var login = VaultFailures.AuthenticationFailed("HTTP 403 from the login");
        var read = VaultFailures.PermissionDenied(Handle, Address, Mount, Role);

        login.Code.ShouldBe(
            ErrorCode.InternalError,
            "the platform failing to reach its own vault is not the caller's authorization problem, "
            + "and AuthorizationFailed renders as 403 to a tenant who would then go and check their "
            + "own permissions"
        );

        read.Code.ShouldBe(ErrorCode.AuthorizationFailed);
        login.TenantMessage.ShouldNotBe(read.TenantMessage);
    }

    [Fact]
    public void AMissingFieldAndAMissingPathReadTheSameToATenantAndDifferentlyToAnOperator() {
        // ⚠ The tenant cannot act on the difference; the operator's fix is completely different —
        // "write the secret" against "the reconciler asked for the wrong key". So the tenant messages
        // are allowed to be identical and the operator details must not be.
        var absent = VaultFailures.NotFound(Handle, Address, Mount);
        var misspelt = VaultFailures.FieldMissing(Handle, Address, Mount, ["password"]);

        absent.TenantMessage.ShouldBe(misspelt.TenantMessage);
        misspelt.OperatorDetail.ShouldContain("password", Case.Sensitive);
        misspelt.OperatorDetail.ShouldContain(Handle.Field);
        absent.OperatorDetail.ShouldNotContain("no 'adminPassword' field");
    }

    [Fact]
    public void TheUnreadableRefusalDoesNotQuoteTheBody() {
        // ⚠ THE ONE PLACE IN THIS ASSEMBLY THAT COULD WRITE A VALUE TO A LOG, AND IT IS SHUT.
        // CyberCloud.Kubernetes' equivalent quotes its serializer's message for the same class of
        // fault; that one is not carrying a password. A kv-v2 response body IS the secret, so a
        // malformed-response path that echoed what it could not parse would put one in the operator
        // log every time OpenBao and this client disagreed about a shape.
        var refusal = VaultFailures.Unreadable("it is not JSON");

        refusal.OperatorDetail.ShouldContain("body is deliberately not");
        refusal.Code.ShouldBe(ErrorCode.InternalError);
    }
}

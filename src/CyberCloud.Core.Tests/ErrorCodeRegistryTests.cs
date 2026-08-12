using Shouldly;
using System.Collections.Immutable;
using System.Reflection;

namespace CyberCloud.Core.Tests;

/// <summary>
///     The error-code registry is a closed, checked-in set, and this is the gate on additions —
///     docs/plan/08 § Errors.
/// </summary>
public class ErrorCodeRegistryTests {
    /// <summary>
    ///     ⚠ <b>THE GOLDEN SET. Changing a line here is changing the public API contract.</b>
    ///     docs/plan/08 § Errors:
    ///     <i>
    ///         "code is a stable, documented, greppable identifier. It is part of
    ///         the API contract; changing one is a breaking change."
    ///     </i>
    ///     Adding a code means adding a
    ///     line here and to <see cref="ErrorCode.All" />; removing or renaming one means a
    ///     deprecation, not an edit.
    /// </summary>
    static readonly ImmutableArray<string> Golden = [
        "AuthorizationFailed",
        "Conflict",
        "InternalError",
        "InvalidApiVersion",
        "InvalidGrainKey",
        "InvalidRequestBody",
        "InvalidResourceId",
        "InvalidResourceName",
        "InvalidResourceType",
        "OperationCanceled",
        "OperationInProgress",
        "OperationTimeout",
        "PolicyViolation",
        "PreconditionFailed",
        "ProvisioningFailed",
        "QuotaExceeded",
        "ResourceAlreadyExists",
        "ResourceGroupNotFound",
        // Added with the child-delete gate (docs/plan/08 § Deleting a parent resource that has
        // children): a delete refused because the resource still has children is a 409 the caller
        // recovers from by deleting the children, which is a different recovery from ScopeLocked's.
        "ResourceHasChildren",
        "ResourceNotFound",
        // Added with CyberCloud.Authorization (docs/plan/07 § The model): a check that names a
        // permission the schema does not define must be neither a denial nor an allow.
        "SchemaInvalid",
        "ScopeLocked",
        "SubscriptionNotFound",
        "TenantNotFound",
        "TenantSuspended"
    ];

    [Fact]
    public void TheRegistryMatchesTheGoldenSetExactly() {
        var actual = ErrorCode.All.Select(x => x.Value).Order(StringComparer.Ordinal).ToImmutableArray();

        actual.ShouldBe(
            Golden.Order(StringComparer.Ordinal).ToImmutableArray(),
            "the error-code registry changed. Every code is part of the public API contract "
            + "(docs/plan/08 § Errors) — if this is an addition, add it to the golden set above in the "
            + "same commit; if it is a rename or a removal, it is a breaking change and needs a "
            + "deprecation, not an edit."
        );
    }

    [Fact]
    public void EveryDeclaredCodeIsInAll() {
        // Catches the "declared a field, forgot the All list" mistake, which would otherwise make
        // a code exist but be unresolvable from the wire.
        var declared = typeof(ErrorCode)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(x => x.FieldType == typeof(ErrorCode))
            .Select(x => (ErrorCode)x.GetValue(null)!)
            .ToImmutableArray();

        declared.Length.ShouldBe(ErrorCode.All.Length);
        foreach (var code in declared) {
            ErrorCode.All.ShouldContain(code, $"{code.Value} is declared but missing from All");
        }
    }

    [Fact]
    public void CodesAreUnique() =>
        ErrorCode.All.Select(x => x.Value)
            .Distinct(StringComparer.Ordinal)
            .Count()
            .ShouldBe(ErrorCode.All.Length);

    [Fact]
    public void TheSetIsClosedThereIsNoPublicConstructor() {
        // This is what makes a code impossible to invent: no public constructor, no public
        // conversion from string, no factory. The compiler is the gate, not a convention.
        typeof(ErrorCode)
            .GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .ShouldBeEmpty();

        typeof(ErrorCode)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(x => x.Name is "op_Implicit" or "op_Explicit")
            .ShouldBeEmpty();
    }

    [Fact]
    public void EveryCodeResolvesFromItsWireValue() {
        foreach (var code in ErrorCode.All) {
            ErrorCode.TryFromValue(code.Value, out var resolved).ShouldBeTrue();
            resolved.ShouldBeSameAs(code);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("NotACode")]
    [InlineData("quotaexceeded")] // ordinal: casing is part of the contract
    [InlineData(" QuotaExceeded")]
    public void AnUnregisteredValueDoesNotResolve(string? value) {
        ErrorCode.TryFromValue(value, out var code).ShouldBeFalse();
        code.ShouldBeNull();
    }

    [Fact]
    public void CodeStringsAreGreppableIdentifiers() {
        // A code that is not a bare PascalCase identifier is a code that grep, a JSON schema enum
        // and a CLI `--query` filter will each mangle differently.
        foreach (var code in ErrorCode.All) {
            code.Value.ShouldNotBeNullOrWhiteSpace();
            char.IsAsciiLetterUpper(code.Value[0]).ShouldBeTrue($"{code.Value} must start upper");
            code.Value.ShouldAllBe(c => char.IsAsciiLetterOrDigit(c));
            code.Value.Length.ShouldBeLessThanOrEqualTo(64);
        }
    }

    [Fact]
    public void EqualityIsReferenceEqualityWhichForAClosedSetIsValueEquality() {
        ErrorCode.TryFromValue("QuotaExceeded", out var a).ShouldBeTrue();

        (a == ErrorCode.QuotaExceeded).ShouldBeTrue();
        (a != ErrorCode.Conflict).ShouldBeTrue();
        a.Equals(ErrorCode.QuotaExceeded).ShouldBeTrue();
        a.GetHashCode().ShouldBe(ErrorCode.QuotaExceeded.GetHashCode());
        a.ToString().ShouldBe("QuotaExceeded");
    }
}

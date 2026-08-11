using System.Globalization;
using System.Reflection;
using CyberCloud.Authorization.Contracts;
using Shouldly;

namespace CyberCloud.Authorization.Tests;

/// <summary>A stand-in application service, so the scanner has something real to find.</summary>
/// <remarks>
///     ⚠ There is no <c>CyberCloud.ResourceManager</c> yet (docs/plan/08), so the only
///     <c>[RequiresPermission]</c> attributes in the repository are these. That is exactly the limit
///     <see cref="PermissionNameTests" /> is about: the scanner sees what is loaded, and today what
///     is loaded is this class.
/// </remarks>
public sealed class SampleApplicationService
{
    /// <summary>A method whose permission is named with the schema's own constants.</summary>
    [RequiresPermission(ObjectTypes.ResourceGroup, Permissions.Delete)]
    public static void DeleteResourceGroup()
    {
    }

    /// <summary>A method whose permission is named with a string literal, which also compiles.</summary>
    [RequiresPermission("subscription", "assignRole")]
    public static void AssignRole()
    {
    }
}

/// <summary>A deliberately wrong attribute, used to prove the scanner is not vacuous.</summary>
sealed class MisspelledService
{
    [RequiresPermission("resourceGroup", "delet")]
    public static void Typo()
    {
    }
}

/// <summary>
///     ⚠ <b>What is actually enforceable about permission names today, versus what docs/plan/07
///     claims.</b>
/// </summary>
/// <remarks>
///     <para>
///         docs/plan/07 § The model: <i>"The schema is <b>C# and compiled</b>, not a DSL file. It
///         gets type checking, IDE navigation, and — the real reason — <b>the analyzer can then
///         verify that every <c>[Authorize]</c> on a provider's application service names a
///         permission that exists</b>."</i>
///     </para>
///     <para>
///         <b>THERE IS NO SUCH ANALYZER.</b> A Roslyn analyzer needs
///         <c>Microsoft.CodeAnalysis.CSharp</c>, which is not in docs/plan/02's dependency register
///         and therefore needs an ADR before it can be added. The same overclaim has appeared twice
///         before in this repository — docs/plan/05 § Serialization rule 5 says "the analyzer makes
///         it a compile error" for a missing <c>[Alias]</c> (it is a reflection test), and
///         docs/plan/00 § Coding standards says <c>async void</c> / <c>.Result</c> / <c>.Wait()</c>
///         are "analyzer-enforced" (.editorconfig records honestly that none of the three is fully
///         covered). This is the third instance of the same pattern.
///     </para>
///     <para>
///         <b>What this file actually enforces, and what it cannot.</b> It is a reflection test: it
///         scans every loaded <c>CyberCloud.*</c> assembly for <see cref="RequiresPermissionAttribute" />
///         and asserts each one names a permission the shipped schema defines. Its limit is that
///         <b>it only sees assemblies the test host has loaded</b>. A provider assembly that no test
///         references is invisible to it, which a compile-time analyzer would not be. The mitigation
///         is a floor on how many attributes it found, so "zero, because nothing was loaded" fails
///         rather than passes — and <see cref="TheScannerActuallyRejectsATypo" /> proves the scanner
///         rejects a wrong name rather than finding nothing to reject.
///     </para>
/// </remarks>
public sealed class PermissionNameTests
{
    [Fact]
    public void EveryRequiresPermissionAttributeNamesAPermissionTheSchemaDefines()
    {
        var found = Scan(x => x != typeof(MisspelledService)).ToList();

        found.ShouldNotBeEmpty(
            "the scanner found no [RequiresPermission] at all. That is a pass only if nothing in "
            + "the repository uses it; the floor below is what distinguishes 'nothing to check' "
            + "from 'the scan is broken'.");

        found.Count.ShouldBeGreaterThanOrEqualTo(
            2, "SampleApplicationService carries two; a lower count means the scan lost assemblies");

        var unknown = found
            .Where(x => CyberCloudSchema.Instance.Member(x.ObjectType, x.Permission) is null)
            .Select(x => x.Where)
            .ToList();

        unknown.ShouldBeEmpty(
            "an attribute names a permission the shipped schema does not define. docs/plan/07 "
            + "§ The model calls a typo'd permission name 'a silent allow-nothing or, worse in the "
            + "wrong evaluator, a silent allow-everything'.");
    }

    [Fact]
    public void TheScannerActuallyRejectsATypo()
    {
        // ⚠ Without this, the test above could be green because the scanner finds nothing, or
        // because Member(...) never returns null. MisspelledService exists to be caught.
        var typo = Scan(x => x == typeof(MisspelledService)).ToList();

        var found = typo.ShouldHaveSingleItem();
        CyberCloudSchema.Instance.Member(found.ObjectType, found.Permission).ShouldBeNull();
    }

    [Fact]
    public void EveryPermissionConstantIsDefinedOnAtLeastOneType()
    {
        // The compile-time half, such as it is: a call site that uses `Permissions.Delete` cannot
        // typo it, because a typo is CS0117. This asserts the constants themselves are not stale —
        // a constant naming a permission no type declares would compile at every call site and
        // deny everywhere.
        var constants = typeof(Permissions)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(x => x.IsLiteral && x.FieldType == typeof(string))
            .Select(x => (string)x.GetRawConstantValue()!)
            .ToList();

        constants.ShouldNotBeEmpty();

        foreach (var permission in constants)
        {
            CyberCloudSchema.Instance.TypeNames
                .Any(type => CyberCloudSchema.Instance.Member(type, permission)?.IsPermission == true)
                .ShouldBeTrue($"'{permission}' is a constant no object type declares as a permission");
        }
    }

    [Fact]
    public void EveryRelationConstantIsDeclaredOnAtLeastOneType()
    {
        var constants = typeof(Relations)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(x => x.IsLiteral && x.FieldType == typeof(string))
            .Select(x => (string)x.GetRawConstantValue()!)
            .ToList();

        foreach (var relation in constants)
        {
            CyberCloudSchema.Instance.TypeNames
                .Any(type => CyberCloudSchema.Instance.Member(type, relation)?.IsPermission == false)
                .ShouldBeTrue($"'{relation}' is a constant no object type declares as a relation");
        }
    }

    [Fact]
    public void EveryObjectTypeConstantIsInTheSchema()
    {
        var constants = typeof(ObjectTypes)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(x => x.IsLiteral && x.FieldType == typeof(string))
            .Select(x => (string)x.GetRawConstantValue()!)
            .ToList();

        foreach (var type in constants)
        {
            CyberCloudSchema.Instance.HasType(type).ShouldBeTrue(type);
        }
    }

    [Fact]
    public void TheSchemaItselfIsTheStrongestGateAndItRunsAtStartup()
    {
        // The one enforcement that is genuinely airtight, and it covers a DIFFERENT mistake from
        // the one the document describes: a typo INSIDE the schema cannot reach production at all,
        // because SchemaBuilder.Build() throws rather than returning a schema with an unresolvable
        // reference. CyberCloudSchema.Instance is built in a static initialiser, so a broken schema
        // fails the first time anything touches the engine.
        Should.NotThrow(() => CyberCloudSchema.Instance.TypeNames.Length);

        Should.Throw<SchemaDefinitionException>(() =>
            Schema.DefineType("doc")
                .Relation("owner", Rewrite.This)
                .Permission("act", Rewrite.Rel("ownr"))
                .Build());
    }

    static IEnumerable<(string ObjectType, string Permission, string Where)> Scan(
        Func<Type, bool> include) =>
        AppDomain.CurrentDomain.GetAssemblies()
            .Where(x => x.GetName().Name?.StartsWith("CyberCloud", StringComparison.Ordinal) == true)
            .SelectMany(SafeTypes)
            .Where(include)
            .SelectMany(type => type
                .GetMembers(BindingFlags.Public | BindingFlags.NonPublic
                    | BindingFlags.Instance | BindingFlags.Static)
                .SelectMany(member => member
                    .GetCustomAttributes<RequiresPermissionAttribute>()
                    .Select(attribute => (
                        attribute.ObjectType,
                        attribute.Permission,
                        Where: string.Create(
                            CultureInfo.InvariantCulture, $"{type.FullName}.{member.Name}"))))
                .Concat(type
                    .GetCustomAttributes<RequiresPermissionAttribute>()
                    .Select(attribute => (
                        attribute.ObjectType,
                        attribute.Permission,
                        Where: type.FullName ?? type.Name))));

    static IEnumerable<Type> SafeTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException loaded)
        {
            return loaded.Types.Where(x => x is not null)!;
        }
    }
}

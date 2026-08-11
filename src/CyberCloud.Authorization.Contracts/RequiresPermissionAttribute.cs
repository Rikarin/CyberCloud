namespace CyberCloud.Authorization.Contracts;

/// <summary>
///     Names the permission an application-service method requires — the attribute docs/plan/07
///     § The model calls <c>[Authorize]</c>.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>READ THIS BEFORE BELIEVING THE DOCUMENT.</b> docs/plan/07 § The model says the
///         schema being C# and compiled means "the analyzer can then verify that every
///         <c>[Authorize]</c> on a provider's application service names a permission that exists".
///         <b>There is no such analyzer, and this attribute does not create one.</b> A Roslyn
///         analyzer needs a <c>Microsoft.CodeAnalysis.CSharp</c> reference, which is not in
///         docs/plan/02's dependency register and therefore needs an ADR before it can be added.
///     </para>
///     <para>
///         <b>What is actually enforceable today, in increasing strength:</b>
///     </para>
///     <list type="number">
///         <item>
///             <b>A reflection test</b> — <c>PermissionNameTests</c> walks every loaded
///             <c>CyberCloud.*</c> assembly for this attribute and asserts each
///             <see cref="Permission" /> is defined on <see cref="ObjectType" /> in the built-in
///             schema. It is a real gate and it is the one that exists. Its limit is honest and
///             specific: <b>it only sees assemblies the test host has loaded</b>, so a provider
///             assembly no test references is invisible to it. The mitigation is that the test
///             asserts a floor on how many attributes it found, so "zero, because nothing was
///             loaded" fails rather than passes.
///         </item>
///         <item>
///             <b>Compile-time, if the call site cooperates</b> — writing
///             <c>[RequiresPermission(ObjectTypes.ResourceGroup, Permissions.Delete)]</c> against
///             <c>CyberCloudSchema</c>'s constants makes a typo <c>CS0117</c>. Nothing forces a
///             caller to use them; a string literal compiles.
///         </item>
///         <item>
///             <b>Startup</b> — <c>SchemaBuilder.Build()</c> throws on a schema that references a
///             relation it does not define, so a typo <i>inside the schema</i> cannot reach
///             production at all. That is the strongest of the three and it covers a different
///             mistake from the one the document describes.
///         </item>
///     </list>
///     <para>
///         The document's claim is therefore an overstatement of (1) and (2). Making it true would
///         cost an analyzer project and an ADR; saying so is cheaper than pretending.
///     </para>
/// </remarks>
/// <param name="objectType">The schema object type the permission is defined on.</param>
/// <param name="permission">The permission name.</param>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
public sealed class RequiresPermissionAttribute(string objectType, string permission) : Attribute
{
    /// <summary>The schema object type the permission is defined on.</summary>
    public string ObjectType { get; } = objectType;

    /// <summary>The permission name.</summary>
    public string Permission { get; } = permission;
}

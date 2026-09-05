namespace CyberCloud.Sdk;

/// <summary>
///     ⚠ <b>This type has no members and exists to be read.</b> It is the written contract between
///     the hand-written half of the SDK — everything else in this assembly — and the Roslyn emitter
///     that <c>Build.Generate</c> runs (ADR-012, docs/plan/21 § Generation). It lives in code rather
///     than in a Markdown file so that every <c>&lt;see cref&gt;</c> below is compiler-checked: a
///     rename here fails the build instead of silently making the contract wrong.
/// </summary>
/// <remarks>
///     <para>
///         docs/plan/21 § Generation draws the line: <i>"Hand-written on top: the credential types, the
///         pipeline policies, the convenience methods (<c>GetConnectionStringAsync</c>), and the tests.
///         Everything else is regenerated per release and never edited."</i>
///     </para>
///
///     <para><b>─── 1. What the emitter must produce ───</b></para>
///
///     <para>
///         <b>Per resource type</b> (one <c>x-cybercloud-resource-type</c> in
///         <c>openapi/{version}.json</c>), four <c>partial</c> types:
///     </para>
///     <list type="table">
///         <item>
///             <term><c>{Type}Data</c></term>
///             <description>
///                 The body. Properties from the type's schema; nullable reference types honouring
///                 <c>required</c>; a constructor taking the required members and init-only setters for
///                 the rest. Serialisation is <b>source-generated</b>
///                 <c>System.Text.Json</c> — a <c>[JsonSerializable]</c> entry in a generated
///                 <c>JsonSerializerContext</c>, in the same shape as
///                 <see cref="SdkJsonContext" />. ⚠ No reflection-based
///                 <c>JsonSerializer.Deserialize&lt;T&gt;(json)</c> anywhere: the .csproj sets
///                 <c>IsAotCompatible</c>, which makes that call IL2026 and a build failure, and
///                 docs/plan/21 § The .NET SDK makes single-file AOT a requirement of the CLI that
///                 consumes this assembly.
///             </description>
///         </item>
///         <item>
///             <term><c>{Type}Resource</c></term>
///             <description>
///                 One instance. Holds a <see cref="CyberCloudClientContext" /> and its own URL, exposes
///                 <c>Data</c>, and carries the type's operations and its
///                 <c>x-cybercloud-action</c> actions.
///                 ⚠ <b><c>Data</c> is <c>required</c>, so a hand-written constructor that assigns it
///                 needs <c>[SetsRequiredMembers]</c>.</b> It was <c>= new()</c> until 2026-09-05, and
///                 that is <c>CS9035</c> for every type whose schema requires a property — the emitter
///                 gives those members C#'s own <c>required</c> so that a body the API would refuse
///                 does not compile, and a defaulted empty body is precisely such a body. 110 of them
///                 were checked in, over 22 resource types, and the first thing to read them was the
///                 <c>Generated SDK compiles</c> gate that issue #73 added.
///                 <c>CyberCloud.Sdk.Tests/StandIn/WidgetStandIn.cs</c> is the one instance of this
///                 row, and it carries the attribute: drop it there and that file stops compiling,
///                 which is the whole reason the stand-in lives in a project that is built.
///             </description>
///         </item>
///         <item>
///             <term><c>{Type}Collection</c></term>
///             <description>
///                 The children of one parent scope: <c>GetAsync</c>, <c>GetIfExistsAsync</c>,
///                 <c>ExistsAsync</c>, <c>GetAll()</c> returning
///                 <see cref="AsyncPageable{T}" />, and <c>CreateOrUpdateAsync</c> returning
///                 <see cref="Operation{T}" />.
///             </description>
///         </item>
///         <item>
///             <term><c>{Type}OperationSource</c></term>
///             <description>
///                 An <see cref="IOperationSource{T}" /> that turns the resource <c>GET</c> that follows
///                 a successful operation into a <c>{Type}Resource</c>.
///             </description>
///         </item>
///     </list>
///     <para>
///         ⚠ <b>Every generated type is <c>partial</c>, without exception.</b> That single word is what
///         makes docs/plan/21 § Generation's "convenience methods" clause possible:
///         <c>GetConnectionStringAsync</c> is hand-written into the other half of
///         <c>PostgresServerResource</c>, and a sealed non-partial generated class would force it into
///         an extension method that cannot see the resource's own state.
///     </para>
///     <para>
///         ⚠ <b>The emitter's project file needs <c>&lt;NoWarn&gt;$(NoWarn);CA1711&lt;/NoWarn&gt;</c>.</b>
///         CA1711 refuses a type name ending in <c>Collection</c>, and <c>{Type}Collection</c> is the
///         name docs/plan/21 § The .NET SDK's conventions table specifies. Found the first time the
///         stand-in in <c>CyberCloud.Sdk.Tests/StandIn/</c> was compiled, which is what that stand-in
///         is for.
///     </para>
///
///     <para><b>─── 2. What the emitter must NOT produce ───</b></para>
///     <list type="bullet">
///         <item>
///             <b>The <c>Error</c>, <c>ErrorCode</c> and <c>ErrorResponse</c> schemas.</b> They are
///             <see cref="CyberCloudError" /> and <see cref="CyberCloudRequestFailedException" />,
///             hand-written, because the pipeline maps every failure through them and cannot depend on
///             a type that may or may not have been emitted this release.
///         </item>
///         <item>
///             <b>The <c>OperationStatus</c>, <c>OperationState</c> and <c>OperationProgress</c>
///             schemas.</b> They are <see cref="OperationStatus" />, <see cref="OperationState" /> and
///             <see cref="OperationProgress" />, for the same reason —
///             <see cref="OperationPoller" /> parses them.
///         </item>
///         <item>
///             <b>Anything to do with authentication, retry, correlation or the api-version query
///             parameter.</b> All four are pipeline handlers
///             (<see cref="BearerTokenHandler" />, <see cref="RetryHandler" />,
///             <see cref="CorrelationRequestIdHandler" />, <see cref="ApiVersionHandler" />) and a
///             generated method that set any of them by hand would be a second implementation that
///             drifts.
///         </item>
///         <item>
///             <b>Synchronous overloads.</b> See § 4.
///         </item>
///     </list>
///
///     <para><b>─── 3. The exact call shapes ───</b></para>
///     <para>A read:</para>
///     <code>
///     public virtual async Task&lt;Response&lt;WidgetResource&gt;&gt; GetAsync(string name, CancellationToken cancellationToken = default) {
///         using var request = context.CreateRequest(HttpMethod.Get, $"{scope}/providers/CyberCloud.Sample/widgets/{Uri.EscapeDataString(name)}");
///         var response = await context.Pipeline.SendAsync(request, cancellationToken).ConfigureAwait(false);
///
///         if (response.IsError)
///             throw CyberCloudClientContext.CreateFailure(response);
///
///         return Response.FromValue(new WidgetResource(context, …), response);
///     }
///     </code>
///     <para>A long-running write — docs/plan/21 § The .NET SDK's worked example, generated:</para>
///     <code>
///     public virtual async Task&lt;Operation&lt;WidgetResource&gt;&gt; CreateOrUpdateAsync(
///         WaitUntil waitUntil, string name, WidgetData data, CancellationToken cancellationToken = default) {
///         var uri = new Uri(context.Endpoint, $"{scope}/providers/CyberCloud.Sample/widgets/{Uri.EscapeDataString(name)}");
///         using var request = context.CreateRequest(HttpMethod.Put, uri);
///         CyberCloudClientContext.SetJsonBody(request, JsonSerializer.SerializeToUtf8Bytes(data, WidgetJsonContext.Default.WidgetData));
///
///         var response = await context.Pipeline.SendAsync(request, cancellationToken).ConfigureAwait(false);
///
///         if (response.IsError)
///             throw CyberCloudClientContext.CreateFailure(response);
///
///         var operation = new Operation&lt;WidgetResource&gt;(new WidgetOperationSource(context), context, uri, response, "Widgets.CreateOrUpdate");
///
///         if (waitUntil == WaitUntil.Completed)
///             await operation.WaitForCompletionAsync(cancellationToken).ConfigureAwait(false);
///
///         return operation;
///     }
///     </code>
///     <para>
///         ⚠ <b>The <c>uri</c> passed to the operation is the URL of the write, not the poll URL.</b>
///         docs/plan/10 § Long-running operations, over HTTP ends with <i>"then GET the resource"</i>,
///         and that is the URL it means. The poll URL comes from the <c>Azure-AsyncOperation</c> header
///         and the emitter never touches it.
///     </para>
///     <para>A list:</para>
///     <code>
///     public virtual AsyncPageable&lt;WidgetResource&gt; GetAll(CancellationToken cancellationToken = default)
///         =&gt; AsyncPageable&lt;WidgetResource&gt;.Create(async (continuationToken, pageSizeHint, token) =&gt; {
///             using var request = continuationToken is null
///                 ? context.CreateRequest(HttpMethod.Get, $"{scope}/providers/CyberCloud.Sample/widgets")
///                 : context.CreateRequest(HttpMethod.Get, new Uri(continuationToken));
///             …
///             return new Page&lt;WidgetResource&gt;(values, nextLink, response);
///         }, cancellationToken);
///     </code>
///
///     <para><b>─── 4. Async only ───</b></para>
///     <para>
///         ⚠ There is no synchronous entry point anywhere in this assembly and the emitter must not
///         invent one. Every operation performs I/O; a synchronous wrapper over that deadlocks a caller
///         with a synchronization context, which is the failure CC1001 (docs/plan/00 § Non-negotiables)
///         exists to stop. Owning these shapes rather than inheriting Azure's is what makes the choice
///         available — Azure.Core's <c>Operation</c> declares an abstract synchronous
///         <c>UpdateStatus</c> and would have forced one.
///     </para>
///
///     <para><b>─── 5. What the SDK needs from the identity server ───</b></para>
///     <para>
///         <c>CyberCloud.Identity.Host</c> (docs/plan/11 § Hosts) does not exist yet. This SDK is built
///         against its <b>discovery document</b> rather than against hard-coded paths, so the list
///         below is the whole contract and is what a reviewer should reconcile against the identity
///         module's own statement.
///     </para>
///     <list type="bullet">
///         <item>
///             <c>GET /.well-known/openid-configuration</c> returning at least <c>issuer</c>,
///             <c>token_endpoint</c>, <c>authorization_endpoint</c>,
///             <c>device_authorization_endpoint</c> and <c>jwks_uri</c> —
///             <see cref="OpenIdConfiguration" />.
///         </item>
///         <item>
///             <c>GET {jwks_uri}</c> returning RFC 7517 keys with <c>kid</c> and either
///             <c>n</c>/<c>e</c> or <c>crv</c>/<c>x</c>/<c>y</c>. ⚠ docs/plan/11 § Protocol rotates them
///             every 30 days with both published for 60; <see cref="SigningKeyCache" /> re-reads on an
///             unknown <c>kid</c> and at most daily.
///         </item>
///         <item>
///             <c>POST {token_endpoint}</c>, form-encoded, accepting the five grants in
///             <see cref="OAuthGrants" /> plus <c>client_assertion_type</c> /
///             <c>client_assertion</c> for the certificate credential, and a <c>tenant_id</c> field —
///             docs/plan/11 § Protocol makes a token scoped to a tenant and no standard field carries
///             that.
///         </item>
///         <item>
///             <c>POST {device_authorization_endpoint}</c> returning RFC 8628's
///             <c>device_code</c>, <c>user_code</c>, <c>verification_uri</c>, <c>expires_in</c> and
///             <c>interval</c>.
///         </item>
///         <item>
///             RFC 6749 § 5.2 error bodies with a machine-readable <c>error</c>. ⚠ Three codes are
///             load-bearing: <c>invalid_grant</c> tells <see cref="RefreshTokenExchange" /> the refresh
///             chain is finished and the cache entry must be cleared, and
///             <c>authorization_pending</c> / <c>slow_down</c> drive the device-code poll. A failure
///             that arrives <i>without</i> an <c>error</c> code is treated as ambiguous and poisons the
///             cached refresh token — so an identity server that returns a bare <c>500</c> with an HTML
///             body where it means <c>invalid_grant</c> will cost users an extra sign-in.
///         </item>
///         <item>
///             <c>expires_in</c> on every token response. The SDK never parses its own access token —
///             docs/plan/11 § Protocol keeps roles and permissions out of it and a client that read
///             claims would start depending on them.
///         </item>
///     </list>
///
///     <para><b>─── 6. What the SDK needs from the gateway ───</b></para>
///     <list type="bullet">
///         <item>
///             <c>202</c> on every long-running write, with <b>both</b> <c>Azure-AsyncOperation</c>
///             (absolute URL) and <c>Retry-After</c>. ⚠ Without the first,
///             <see cref="OperationPoller" /> throws at construction — there is nothing to poll.
///         </item>
///         <item>
///             <c>429</c> with <c>Retry-After</c> — docs/plan/10 § Rate limiting.
///             <see cref="RetryHandler" /> honours it exactly and retries no other <c>4xx</c>.
///         </item>
///         <item>
///             <c>x-cybercloud-request-id</c> on every response, surfaced as
///             <see cref="Response.ServiceRequestId" /> and put in every exception message.
///         </item>
///         <item>
///             The one error shape on every failure, with <c>target</c> as an RFC 6901 JSON Pointer —
///             docs/plan/08 § Errors. It reaches the caller as
///             <see cref="CyberCloudRequestFailedException.Target" />.
///         </item>
///     </list>
/// </remarks>
static class EmitterContract;

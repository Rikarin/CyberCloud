/**
 * `libs/resource-forms` — the JSON Schema → xUI form renderer. **A stub at M1.**
 *
 * docs/plan/20 § The shape that makes 100 resource types affordable: "Almost every screen is
 * generated (ADR-012). A resource type contributes a JSON Schema; the portal renders it." That
 * renderer is a separate 1.2 EM of work and the schemas it consumes are being emitted right now by
 * the generator; building a renderer against a schema shape that is still moving would produce a
 * renderer that has to be rewritten.
 *
 * What is here instead is the *interface* — the contract between the emitter and the renderer,
 * stated so both sides can be built against it. Nothing in it is implemented.
 *
 * ## What this library needs from the form-schema emitter
 *
 * 1. **One JSON Schema per `(resourceType, apiVersion)`, fetched at runtime, not imported.**
 *    docs/plan/20 § Performance budget: "Schemas are fetched per type, cached, and versioned by the
 *    api-version — which is also what lets the portal support an old api-version without shipping
 *    two apps." A schema that arrives as a TypeScript module would be a module in the bundle graph,
 *    and a hundred of them would blow the 120 KB route-chunk budget on the first resource route.
 *    So the emitter must publish schemas to an endpoint, and the build must not be able to import
 *    them.
 * 2. **The `x-cybercloud-widget` vocabulary, closed and enumerated.** docs/plan/20 lists
 *    `region`, `cluster`, `storageclass`, `subnet`, `sku`, `secret-ref`, `cron`, `cidr`,
 *    `duration`. The renderer needs the emitter to guarantee that set is exhaustive for a given
 *    schema version, so an unknown hint is a build failure in the emitter rather than a silently
 *    degraded control in the portal.
 * 3. **`@section` annotations for layout**, mapping to the tabs and groups in
 *    docs/plan/20's `layout/` box.
 * 4. **`x-immutable`, `x-secret` and `x-cozy-preset` on the fields that need them.** Two of these
 *    are correctness-critical rather than cosmetic: `x-secret` must never render a plain value
 *    (docs/plan/20 § The shape that makes 100 resource types affordable maps
 *    `format: password`/`x-secret` to "`@xui/input` + a Vault `SecretRef` picker — **never a plain
 *    value**"), and `x-immutable` must be known before the create form is rendered, not discovered
 *    from a rejected update.
 * 5. **A stable error shape from server-side validation** that can be merged with client-side
 *    schema validation — docs/plan/20's `validation/` box asks for "schema + async server
 *    validation, one message shape". The renderer cannot produce one message shape out of two
 *    different ones.
 * 6. **A machine-readable schema-version stamp**, so the cache in point 1 can be keyed and
 *    invalidated without a portal deploy.
 *
 * ## The override contract
 *
 * `libs/resource-forms-overrides` replaces a generated form for a `(resourceType, apiVersion)`.
 * docs/plan/20 is strict about the limit: "**Every override must render the same schema**, verified
 * by a test that submits the override's output against the schema. An override that accepts
 * something the API rejects is worse than the generated form."
 */

/** A JSON Schema document as fetched, deliberately opaque — the renderer walks it, nothing else does. */
export type ResourceSchema = Readonly<Record<string, unknown>>;

/** Identifies which schema a form is for. Both halves are required; a type without a version is ambiguous. */
export interface ResourceSchemaKey {
  readonly resourceType: string;
  readonly apiVersion: string;
}

/**
 * Fetches and caches schemas. ⚠ Deliberately async and deliberately not an import: see point 1
 * above.
 */
export interface ResourceSchemaSource {
  load(key: ResourceSchemaKey): Promise<ResourceSchema>;
}

/** One validation message, from either the schema or the server. One shape, per docs/plan/20's `validation/` box. */
export interface FormValidationMessage {
  /** JSON Pointer into the form value. Empty string for a form-level message. */
  readonly pointer: string;
  readonly message: string;
  readonly severity: 'error' | 'warning';
}

/**
 * What a hand-written override must implement — `libs/resource-forms-overrides`.
 *
 * ⚠ `validateAgainstSchema` is not optional and not a convenience. It is the hook the mandatory
 * override test calls: submit the override's output against the schema it claims to render, and
 * fail if the schema rejects it.
 */
export interface ResourceFormOverride {
  readonly key: ResourceSchemaKey;
  validateAgainstSchema(value: unknown, schema: ResourceSchema): readonly FormValidationMessage[];
}

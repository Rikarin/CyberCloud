/**
 * `libs/resource-forms` — the JSON Schema → xUI form renderer.
 *
 * docs/plan/20 § The shape that makes 100 resource types affordable: "Almost every screen is
 * generated (ADR-012). A resource type contributes a JSON Schema; the portal renders it." The
 * renderer reads `generated/forms/{apiVersion}.json` — `FormsEmitter`'s output, one form per
 * resource type plus the two scope forms — and renders every field it lists with the xUI control
 * the emitter chose.
 *
 * ## What the emitter promised, and what became of each point
 *
 * The stub that stood here listed six things the renderer needed from the emitter. What the
 * document delivers:
 *
 * 1. **Fetched at runtime, never imported.** `ResourceFormSource` fetches `/forms/{apiVersion}.json`
 *    over `HttpClient` and caches it per injector. `angular.json` copies the document in as a
 *    static asset; nothing in the bundle graph can import it. The 120 KB route-chunk budget is what
 *    `scripts/bundle-budget.mjs` would fail on if that ever changed.
 * 2. **The widget vocabulary, closed.** Fourteen `control` names — `FieldControl` in `schema.ts` —
 *    and `FormFieldNode` maps each. ⚠ Four of them (`region`, `cluster`, `storageclass`,
 *    `subnet`) are pickers over lists the API does not serve yet, so they render as inputs
 *    carrying the schema's own pattern and example; see `FormFieldNode`'s header.
 * 3. **Layout.** The emitter writes `section`, and the renderer does not use it: two groups at
 *    different depths can share a section name, so the tree is built from the JSON Pointers
 *    instead (`treeOf`), and a group is a `<fieldset>`.
 * 4. **`x-immutable`** arrives as `disabledAfterCreate` + `disabledReason` and is honoured in
 *    `edit`. **`x-secret`** is not in this api-version's document — no type declares one — and the
 *    renderer's `format: password` branch is there for the first that does. **`x-cozy-preset`**
 *    is a closed set like any other and renders as a select.
 * 5. **One message shape.** Schema validators and the platform's `target`-bearing errors both land
 *    on the control and both read through `messageFor` — `validation.ts`.
 * 6. **A version stamp.** The document carries `apiVersion` and `format`, and the cache is keyed by
 *    the former.
 *
 * ## The override contract
 *
 * `libs/resource-forms-overrides` replaces a generated form for a `(resourceType, apiVersion)`.
 * docs/plan/20 is strict about the limit: "**Every override must render the same schema**, verified
 * by a test that submits the override's output against the schema. An override that accepts
 * something the API rejects is worse than the generated form."
 */

export {
  buildForm,
  controlsByPointer,
  hintOf,
  isGroup,
  isList,
  isTagBag,
  messageFor,
  toBody,
  treeOf,
  typeOf
} from './lib/form-model';
export type { FieldNode, FormMode } from './lib/form-model';
export { FORMS_BASE_PATH, ResourceFormSource } from './lib/form-source';
export type { ResourceSchemaKey } from './lib/form-source';
export { FormFieldNode } from './lib/renderer/form-field-node';
export { ResourceFormRenderer } from './lib/renderer/resource-form';
export type {
  AnyForm,
  FieldChoice,
  FieldControl,
  FieldType,
  FormAction,
  FormField,
  FormsDocument,
  ResourceForm,
  ScopeForm
} from './lib/schema';
export { applyServerErrors, messagesOf } from './lib/validation';
export type { FormValidationMessage, PlatformError } from './lib/validation';

import type { ResourceSchemaKey } from './lib/form-source';
import type { FormValidationMessage } from './lib/validation';

/** A form document as fetched, for an override that walks it itself. */
export type ResourceSchema = Readonly<Record<string, unknown>>;

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

/**
 * The shape of `generated/forms/{apiVersion}.json`, as `FormsEmitter` writes it.
 *
 * ⚠ **Read from the document, never restated.** Every name here is one the emitter chose, and
 * `form-model.spec.ts` builds a form from every entry in the real document so a member the emitter
 * adds or renames fails a test rather than rendering as a blank. What is typed here is what the
 * renderer reads; a member it does not read is left out on purpose so nothing depends on it by
 * accident.
 */

/** The xUI control a field is rendered with — docs/plan/20 § The shape that makes 100 resource types affordable. */
export type FieldControl =
  | 'xui-input'
  | 'xui-slider'
  | 'xui-switch'
  | 'xui-select'
  | 'xui-region'
  | 'xui-cluster'
  | 'xui-tag-input'
  | 'xui-cozy-preset'
  | 'xui-storageclass'
  | 'xui-cidr'
  | 'xui-sku'
  | 'xui-chip-input'
  | 'xui-subnet'
  | 'xui-numeric-input'
  | 'xui-secret-ref'
  | 'xui-group';

/** The JSON Schema type behind a field. */
export type FieldType = 'string' | 'integer' | 'number' | 'boolean' | 'object' | 'array';

/** One allowed value of a closed set, with the label the portal shows for it. */
export interface FieldChoice {
  readonly value: string;
  readonly label: string;
}

/** One field of a resource form: one property of the body, at one JSON Pointer. */
export interface FormField {
  /** Into the request body. `/properties/sizing/preset`. */
  readonly jsonPointer: string;
  /** The last segment of the pointer, which is the member name. */
  readonly name: string;
  readonly label: string;
  readonly control: FieldControl;
  /** ⚠ Absent on a scope form's fields, which are all strings. */
  readonly type?: FieldType;
  readonly required: boolean;
  /** The group this field belongs to. `general` for the top level. */
  readonly section?: string;
  /** Helper text. ⚠ A scope form's fields carry it as `help` instead. */
  readonly hint?: string;
  readonly help?: string;
  readonly default?: unknown;
  readonly placeholder?: unknown;
  /** `x-cybercloud-immutable`: editable at create, disabled afterwards with `disabledReason`. */
  readonly disabledAfterCreate?: boolean;
  readonly disabledReason?: string;
  readonly pattern?: string;
  readonly minimum?: number;
  readonly maximum?: number;
  readonly minLength?: number;
  readonly maxLength?: number;
  /** For a tag bag: the cap on the merged set — docs/plan/06 § Tags, locks. */
  readonly maxProperties?: number;
  readonly format?: string;
  readonly choices?: readonly FieldChoice[];
  readonly nullable?: boolean;
}

/** One POST action a type offers beside the CRUD verbs. Rendered by the blade, not by the form. */
export interface FormAction {
  readonly name: string;
  readonly label: string;
  readonly permission: string;
  readonly longRunning: boolean;
  /** ⚠ The response carries secret material — never log or persist it. */
  readonly secret: boolean;
  readonly fields: readonly FormField[];
}

/** The form for one resource type at one api-version. */
export interface ResourceForm {
  readonly resourceType: string;
  readonly apiVersion: string;
  /** The display name. ⚠ Also what names the generated client's verbs — see `resource-verbs.ts`. */
  readonly title: string;
  readonly plural: string;
  readonly summary: string;
  /** `{resourceType}@{apiVersion}` — the key `libs/resource-forms-overrides` is looked up by. */
  readonly overrideKey: string;
  /** Every pointer the fields cover, so a test can prove the form renders the whole body. */
  readonly coveredPointers: readonly string[];
  readonly supportsTags: boolean;
  readonly requiresCluster: boolean;
  readonly clusterIdPointer: string;
  readonly softDeleteDays: number;
  readonly fields: readonly FormField[];
  readonly actions: readonly FormAction[];
}

/** The scopes the document carries a create form for — the tenant has none, because it has no PUT. */
export type ScopeFormKind = 'managementGroup' | 'subscription' | 'resourceGroup';

/** The form for a management group, a subscription or a resource group — the second, non-registry source of issue #63, and the fourth scope of #39. */
export interface ScopeForm {
  readonly scope: ScopeFormKind;
  readonly scopeType: string;
  readonly apiVersion: string;
  readonly title: string;
  readonly plural: string;
  readonly summary: string;
  readonly overrideKey: string;
  readonly coveredPointers: readonly string[];
  readonly fields: readonly FormField[];
}

/** The whole document. */
export interface FormsDocument {
  /** The document's own shape version, as a string — `"1"`. */
  readonly format: string;
  readonly apiVersion: string;
  readonly forms: Readonly<Record<string, ResourceForm>>;
  readonly scopeForms: Readonly<Record<ScopeFormKind, ScopeForm>>;
}

/** What the renderer accepts: either kind, seen through the members both have. */
export type AnyForm = ResourceForm | ScopeForm;

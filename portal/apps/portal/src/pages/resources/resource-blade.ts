import { ChangeDetectionStrategy, Component, input } from '@angular/core';

/**
 * The resource blade placeholder.
 *
 * ⚠ This is the route that must never grow an eager import of the form renderer. docs/plan/20
 * § Performance budget: "with 100 resource types the generated form renderer must not pull every
 * schema into the main bundle. Schemas are fetched per type, cached, and versioned by the
 * api-version — which is also what lets the portal support an old api-version without shipping two
 * apps."
 *
 * When `libs/resource-forms` lands, this component fetches the schema for
 * `(provider, type, apiVersion)` at runtime and hands it to the renderer. The schema is data over
 * the wire, not a module in the graph — which is the difference between one renderer chunk and a
 * hundred.
 *
 * The left rail docs/plan/20 § Information architecture specifies for a resource blade — Overview ·
 * Activity · Access (ReBAC) · Tags · Locks · Metrics · Logs · Diagnose · Settings · type-specific —
 * is the drill-down that `@xui/panel-stack` in `ShellLayout` renders, pushed from here.
 */
@Component({
  selector: 'cc-resource-blade',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'block p-6' },
  template: `
    <h1 class="text-lg font-semibold">{{ name() }}</h1>
    <p class="text-foreground-muted mt-1 text-sm">{{ provider() }}/{{ type() }}</p>
    <p class="text-foreground-muted mt-4 text-sm" i18n="@@resourceBlade.pending">
      The generated form renderer is not part of the M1 shell.
    </p>
  `,
})
export class ResourceBlade {
  // Bound from the route parameters by `withComponentInputBinding()`.
  readonly provider = input.required<string>();
  readonly type = input.required<string>();
  readonly name = input.required<string>();
}

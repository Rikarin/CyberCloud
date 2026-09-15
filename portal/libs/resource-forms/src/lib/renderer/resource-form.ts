import { ChangeDetectionStrategy, Component, computed, effect, input, output, signal, untracked } from '@angular/core';
import { FormGroup, ReactiveFormsModule } from '@angular/forms';
import { XuiButton } from '@xui/button';
import { FormMode, buildForm, toBody, treeOf } from '../form-model';
import { AnyForm } from '../schema';
import { FormValidationMessage, PlatformError, applyServerErrors, messagesOf } from '../validation';
import { FormFieldNode } from './form-field-node';

/**
 * The schema → xUI form renderer — docs/plan/20 § The shape that makes 100 resource types
 * affordable: "A resource type contributes a JSON Schema; the portal renders it."
 *
 * Give it a form from `generated/forms/{apiVersion}.json` and a mode, and it renders every field
 * the emitter listed, validates against every constraint the emitter stated, and emits the request
 * body on submit. The page around it owns the address, the verb and the operation that follows —
 * this component never talks to the API, which is what lets one renderer serve a create blade, an
 * edit blade and a scope form alike.
 *
 * ⚠ **`initial` is the body as the API returned it, not a form value.** In `edit` the page hands
 * over the resource's body and the renderer maps it into controls (`buildForm`); the reverse
 * mapping is `toBody`, and the two are tested as a round trip over every type in the document.
 *
 * ⚠ **Server errors land on fields.** `reject()` takes the platform's error and puts each detail
 * on the control its `target` names, so a `SchemaInvalid` on `/properties/sizing/cpu` shows under
 * that field and nowhere else. What no field can claim is shown above the form, so no complaint is
 * lost — docs/plan/20's "one message shape".
 */
@Component({
  selector: 'cc-resource-form',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [ReactiveFormsModule, FormFieldNode, XuiButton],
  host: { class: 'block' },
  template: `
    <form [formGroup]="group()" (ngSubmit)="onSubmit()" novalidate>
      @if (banner().length > 0) {
        <div
          class="bg-error-muted text-error-foreground rounded-md px-3 py-2 text-sm"
          role="alert"
          aria-live="assertive"
        >
          @for (message of banner(); track $index) {
            <p>{{ message.message }}</p>
          }
        </div>
      }

      @for (node of nodes(); track node.field.jsonPointer) {
        <cc-form-field-node [node]="node" [group]="group()" [mode]="mode()" />
      }

      <div class="mt-6 flex items-center gap-3">
        <button xuiButton type="submit" color="primary" [disabled]="busy()" [loading]="busy()">
          {{ submitLabel() }}
        </button>
        <ng-content select="[actions]" />
        @if (group().invalid && group().touched) {
          <span class="text-error text-sm" i18n="@@forms.fixErrors">Fix the fields marked above.</span>
        }
      </div>
    </form>
  `
})
export class ResourceFormRenderer {
  readonly form = input.required<AnyForm>();
  readonly mode = input<FormMode>('create');
  /** The existing body, for `edit`. Ignored in `create`. */
  readonly initial = input<unknown>(undefined);
  /** True while the page is sending; the submit button shows it and refuses a second click. */
  readonly busy = input(false);
  readonly submitLabel = input<string>($localize`:@@forms.submit:Create`);

  /** The request body, once every schema validator is satisfied. */
  readonly submitted = output<Record<string, unknown>>();

  /**
   * Rebuilt whenever the schema, the mode or the initial body changes — a new resource type is a
   * new form, and a control tree cannot be patched into a different shape. `linkedSignal` would
   * express the same thing; a signal set from an effect keeps the group's identity readable.
   */
  protected readonly group = signal<FormGroup>(new FormGroup({}));
  protected readonly nodes = computed(() => treeOf(this.form()));
  protected readonly banner = signal<readonly FormValidationMessage[]>([]);

  constructor() {
    effect(() => {
      const form = this.form();
      const mode = this.mode();
      const initial = this.initial();

      untracked(() => {
        this.group.set(buildForm(form, mode, initial));
        this.banner.set([]);
      });
    });
  }

  /**
   * Shows the platform's refusal on the fields it names.
   *
   * @param error The `error` member of the response body, as `ApiCallError` carries it.
   */
  reject(error: PlatformError): void {
    const unclaimed = applyServerErrors(this.group(), messagesOf(error));
    this.banner.set(unclaimed);
  }

  protected onSubmit(): void {
    const group = this.group();
    group.markAllAsTouched();

    if (group.invalid || this.busy()) return;

    this.banner.set([]);
    this.submitted.emit(toBody(this.form(), group));
  }
}

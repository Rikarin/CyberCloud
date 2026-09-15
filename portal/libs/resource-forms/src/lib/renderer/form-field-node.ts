import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { FormControl, FormGroup, ReactiveFormsModule } from '@angular/forms';
import { XuiInput } from '@xui/input';
import { XuiMultiSelect } from '@xui/multi-select';
import { XuiNumericInput } from '@xui/numeric-input';
import { XuiSelect } from '@xui/select';
import { XuiSwitch } from '@xui/switch';
import { XuiTagInput } from '@xui/tag-input';
import { FieldNode, FormMode, hintOf, isGroup, messageFor, typeOf } from '../form-model';
import { FormField } from '../schema';

/**
 * One field of a generated form, or one group of them.
 *
 * The schema → widget mapping docs/plan/20 § The shape that makes 100 resource types affordable
 * decides once, applied here once. The `control` the emitter chose is the switch; the schema
 * `type` decides the value shape. Where the two disagree the type wins, because the type is what
 * the write path validates.
 *
 * | Emitted control                                                       | Rendered as                              |
 * | --------------------------------------------------------------------- | ---------------------------------------- |
 * | `xui-input`, `xui-region`, `xui-cluster`, `xui-storageclass`, `xui-cidr`, `xui-subnet` (string) | `<input xuiInput>` |
 * | `xui-select`, `xui-sku`, `xui-cozy-preset` (string, closed set)       | `xui-select`                             |
 * | `xui-select` (array, closed set)                                      | `xui-multi-select`                       |
 * | `xui-chip-input`, `xui-cidr` (array)                                  | `xui-tag-input`                          |
 * | `xui-slider` (integer)                                                | `xui-numeric-input` with `min`/`max`     |
 * | `xui-switch` (boolean)                                                | `xui-switch`                             |
 * | `xui-tag-input` (object)                                              | `xui-tag-input` of `key=value` lines     |
 * | `xui-group` (object)                                                  | `<fieldset>` holding its members         |
 *
 * ⚠ **The pickers are plain inputs at M1, and the schema says which.** `region`, `cluster`,
 * `storageclass` and `subnet` are "picker with hints" widgets in docs/plan/20, and the hints come
 * from endpoints that do not exist yet — there is no region list, no cluster list, no storage-class
 * list on the API. Rendering a picker over an invented list would be a form that offers values the
 * platform refuses. So each is a text input carrying the schema's own `placeholder`, `pattern` and
 * `format`, which is what the platform validates against, and the emitted `control` name is kept
 * on the element as `data-control` so the upgrade to a real picker is a switch case, not a search.
 *
 * ⚠ **`x-secret` is not in this api-version's document**, and the renderer refuses rather than
 * guesses: a field whose `format` is `password` renders as `type="password"` and is never echoed.
 * None exists today; the branch is there so the first one is handled the day it is emitted.
 *
 * ⚠ **Every message goes through `messageFor`**, the schema validators' and the server's alike —
 * docs/plan/20's "one message shape". A field shows one message, below it, wired by
 * `aria-describedby`, and `aria-invalid` follows the control's state so the failure is announced
 * rather than only coloured.
 */
@Component({
  selector: 'cc-form-field-node',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [ReactiveFormsModule, XuiInput, XuiSelect, XuiMultiSelect, XuiNumericInput, XuiSwitch, XuiTagInput],
  host: { class: 'block' },
  template: `
    @if (isGroup()) {
      <fieldset class="border-border mt-4 rounded-md border p-4" [formGroup]="groupControl()">
        <legend class="px-1 text-sm font-semibold">{{ field().label }}</legend>
        @if (hint()) {
          <p class="text-foreground-muted mb-2 text-xs">{{ hint() }}</p>
        }
        @for (child of node().children; track child.field.jsonPointer) {
          <cc-form-field-node [node]="child" [group]="groupControl()" [mode]="mode()" />
        }
      </fieldset>
    } @else {
      <div
        class="mt-3 flex flex-col gap-1.5"
        [attr.data-control]="field().control"
        [attr.data-pointer]="field().jsonPointer"
      >
        <label class="text-sm font-medium" [attr.for]="id()">
          {{ field().label }}
          @if (field().required) {
            <span class="text-error" aria-hidden="true">*</span>
            <span class="sr-only" i18n="@@forms.requiredMark">required</span>
          }
          @if (locked()) {
            <span class="text-foreground-muted ms-1 text-xs font-normal" i18n="@@forms.locked"
              >(cannot change after create)</span
            >
          }
        </label>

        @switch (kind()) {
          @case ('boolean') {
            <xui-switch
              [id]="id()"
              [formControl]="leaf()"
              [aria-label]="field().label"
              [attr.aria-describedby]="describedBy()"
              [attr.title]="lockReason()"
            />
          }
          @case ('integer') {
            <xui-numeric-input
              [id]="id()"
              [formControl]="leaf()"
              [min]="field().minimum"
              [max]="field().maximum"
              [aria-label]="field().label"
              [placeholder]="placeholder()"
              [attr.aria-describedby]="describedBy()"
              [attr.aria-invalid]="invalid()"
              [attr.title]="lockReason()"
            />
          }
          @case ('choice') {
            <xui-select
              [id]="id()"
              [formControl]="leaf()"
              [items]="choiceValues()"
              [itemText]="choiceLabel"
              [filterable]="choiceValues().length > 8"
              [aria-label]="field().label"
              [placeholder]="placeholder()"
              [attr.aria-describedby]="describedBy()"
              [attr.aria-invalid]="invalid()"
              [attr.title]="lockReason()"
            />
          }
          @case ('choices') {
            <xui-multi-select
              [id]="id()"
              [formControl]="leaf()"
              [items]="choiceValues()"
              [itemText]="choiceLabel"
              [placeholder]="placeholder()"
              [attr.aria-label]="field().label"
              [attr.aria-describedby]="describedBy()"
              [attr.aria-invalid]="invalid()"
              [attr.title]="lockReason()"
            />
          }
          @case ('list') {
            <xui-tag-input
              [id]="id()"
              [formControl]="leaf()"
              [placeholder]="placeholder()"
              [attr.aria-label]="field().label"
              [attr.aria-describedby]="describedBy()"
              [attr.aria-invalid]="invalid()"
              [attr.title]="lockReason()"
            />
          }
          @case ('tags') {
            <xui-tag-input
              [id]="id()"
              [formControl]="leaf()"
              [placeholder]="tagPlaceholder"
              [attr.aria-label]="field().label"
              [attr.aria-describedby]="describedBy()"
              [attr.aria-invalid]="invalid()"
              [attr.title]="lockReason()"
            />
          }
          @default {
            <input
              xuiInput
              [id]="id()"
              [formControl]="leaf()"
              [type]="inputType()"
              [placeholder]="placeholder()"
              [attr.maxlength]="field().maxLength ?? null"
              [attr.aria-describedby]="describedBy()"
              [attr.aria-invalid]="invalid()"
              [attr.title]="lockReason()"
              autocomplete="off"
            />
          }
        }

        @if (message(); as text) {
          <p class="text-error text-xs" [id]="id() + '-error'" role="alert">{{ text }}</p>
        } @else if (hint()) {
          <p class="text-foreground-muted text-xs" [id]="id() + '-hint'">{{ hint() }}</p>
        }
      </div>
    }
  `
})
export class FormFieldNode {
  readonly node = input.required<FieldNode>();
  readonly group = input.required<FormGroup>();
  readonly mode = input.required<FormMode>();

  protected readonly field = computed<FormField>(() => this.node().field);
  protected readonly isGroup = computed(() => isGroup(this.field()));
  protected readonly hint = computed(() => hintOf(this.field()));

  /** Stable per pointer, so a label's `for` and the messages' ids line up across re-renders. */
  protected readonly id = computed(() => 'cc-f' + this.field().jsonPointer.replaceAll('/', '-'));

  protected readonly groupControl = computed(() => this.group().get(this.field().name) as FormGroup);
  protected readonly leaf = computed(() => this.group().get(this.field().name) as FormControl);

  protected readonly locked = computed(() => this.mode() === 'edit' && this.field().disabledAfterCreate === true);
  protected readonly lockReason = computed(() => (this.locked() ? (this.field().disabledReason ?? null) : null));

  /** Which template branch renders the field. */
  protected readonly kind = computed<'boolean' | 'integer' | 'choice' | 'choices' | 'list' | 'tags' | 'text'>(() => {
    const field = this.field();
    const type = typeOf(field);
    const closed = field.choices !== undefined && field.choices.length > 0;

    if (type === 'boolean') return 'boolean';
    if (type === 'integer') return 'integer';
    if (type === 'array') return closed ? 'choices' : 'list';
    if (type === 'object') return 'tags';
    return closed ? 'choice' : 'text';
  });

  protected readonly inputType = computed(() => {
    const format = this.field().format;
    if (format === 'password') return 'password';
    if (format === 'date-time') return 'datetime-local';
    return 'text';
  });

  protected readonly choiceValues = computed(() => (this.field().choices ?? []).map(c => c.value));
  protected readonly choiceLabel = (value: string): string =>
    this.field().choices?.find(c => c.value === value)?.label ?? value;

  /**
   * ⚠ Never empty for a list. `xui-tag-input` and `xui-multi-select` at 2.2.4 expose no
   * `aria-label` input and their inner `<input>` takes its accessible name from the placeholder
   * alone, so an empty placeholder is an unlabelled field under axe. That is xUI's to fix
   * (docs/plan/02 § ADR-017); until it is, the placeholder carries the name.
   */
  protected readonly placeholder = computed(() => {
    const example = this.field().placeholder;
    const kind = this.kind();

    if (example === undefined || example === null || example === '') {
      return kind === 'list' || kind === 'choices' ? $localize`:@@forms.listPlaceholder:Add a value` : '';
    }

    return Array.isArray(example) ? example.map(String).join(', ') : String(example);
  });

  protected readonly tagPlaceholder = $localize`:@@forms.tagPlaceholder:key=value`;

  /**
   * Signals do not see a reactive control's status, so the message is read from the control on
   * each check; `OnPush` re-renders on the events the control's own directives raise.
   */
  protected message(): string | null {
    const control = this.leaf();
    return control.touched || control.dirty ? messageFor(this.field(), control.errors) : null;
  }

  protected invalid(): 'true' | null {
    return this.message() === null ? null : 'true';
  }

  protected describedBy(): string | null {
    if (this.message() !== null) return this.id() + '-error';
    return this.hint() ? this.id() + '-hint' : null;
  }
}

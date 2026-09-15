import {
  ChangeDetectionStrategy,
  Component,
  computed,
  effect,
  inject,
  input,
  signal,
  untracked,
  viewChild
} from '@angular/core';
import { FormControl, ReactiveFormsModule, Validators } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { apiVersion } from '@cybercloud/api';
import { ResourceForm, ResourceFormRenderer, ResourceFormSource } from '@cybercloud/resource-forms';
import { BladeStackStore } from '@cybercloud/shell';
import { XuiButton } from '@xui/button';
import { XuiCallout } from '@xui/callout';
import { XuiInput } from '@xui/input';
import { ApiCallError, operationIdOf } from '../../app/api/http-transport';
import { PlatformApi } from '../../app/api/platform-api';
import { ResourceAddress, parentCountOf, putResource, verbsFor } from '../../app/api/resource-verbs';
import { joinType, links } from '../../app/routes/portal-links';
import { NeedsTenant, PageStatus, activeTenantId, load, pageState } from '../shared/page-state';

/**
 * The name rule every resource shares — `ResourceNaming` in `CyberCloud.Core`: 1–63 characters
 * of `[a-z0-9]([-a-z0-9]*[a-z0-9])?`, the Kubernetes DNS-1123 label rule, because the name
 * becomes an object name and a label value. Checked here so the message arrives before the
 * round trip, and checked again by the write path, which is the one that counts.
 */
const RESOURCE_NAME = /^[a-z0-9]([-a-z0-9]*[a-z0-9])?$/;

/**
 * The create blade: one route for every resource type.
 *
 * The form is `generated/forms/{apiVersion}.json`'s entry for the type, fetched when the blade
 * opens and rendered by `libs/resource-forms`. What this page adds is the address — the name, and
 * a parent name per level for a child type — and the call: `PUT` through the generated client,
 * then the operation view for the 202 that comes back.
 *
 * ⚠ **Nothing is optimistic here.** docs/plan/20 § Live updates: "anything that creates, deletes or
 * costs money does not — it shows the operation's real progress." The blade does not paint a
 * resource; it hands the operation id to `OperationView`, which polls until the platform says
 * `Succeeded` and only then links to the blade.
 *
 * ⚠ **A refusal lands on the field.** A `400` with `target` pointers goes to the renderer's
 * `reject()`, so `SchemaInvalid` on `/properties/sizing/cpu` shows under that field. A `409`
 * (`ResourceAlreadyExists`) is about the name, which is this page's field, and is shown there.
 */
@Component({
  selector: 'cc-resource-create',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    ReactiveFormsModule,
    RouterLink,
    ResourceFormRenderer,
    XuiInput,
    XuiButton,
    XuiCallout,
    NeedsTenant,
    PageStatus
  ],
  host: { class: 'block p-6' },
  template: `
    @if (tenantId() === null) {
      <cc-needs-tenant />
    } @else {
      <h1 class="text-lg font-semibold">{{ heading() }}</h1>
      <p class="text-foreground-muted mt-1 text-sm">{{ resourceType() }} · {{ resourceGroup() }}</p>

      <cc-page-status [state]="form()" [retryLink]="backLink()" />

      @if (form().kind === 'ready' && schema(); as schema) {
        <p class="text-foreground-muted mt-3 max-w-prose text-sm">{{ schema.summary }}</p>

        @if (sendError(); as message) {
          <xui-callout class="mt-4" color="error" [title]="refusedTitle">
            <p>{{ message }}</p>
          </xui-callout>
        }

        <div class="mt-4 flex flex-col gap-1.5">
          <label class="text-sm font-medium" for="cc-resource-name">
            <ng-container i18n="@@resourceCreate.name">Name</ng-container>
            <span class="text-error" aria-hidden="true">*</span>
          </label>
          <input
            xuiInput
            id="cc-resource-name"
            [formControl]="name"
            autocomplete="off"
            [attr.aria-describedby]="nameMessage() === null ? 'cc-resource-name-hint' : 'cc-resource-name-error'"
            [attr.aria-invalid]="nameMessage() === null ? null : 'true'"
          />
          @if (nameMessage(); as message) {
            <p class="text-error text-xs" id="cc-resource-name-error" role="alert">{{ message }}</p>
          } @else {
            <p class="text-foreground-muted text-xs" id="cc-resource-name-hint" i18n="@@resourceCreate.nameHint">
              1–63 lowercase letters, digits and hyphens, starting and ending with a letter or digit. It cannot change
              later.
            </p>
          }
        </div>

        @for (parent of parents; track $index) {
          <div class="mt-3 flex flex-col gap-1.5">
            <label class="text-sm font-medium" [attr.for]="'cc-parent-' + $index">
              {{ parentLabel($index) }}
              <span class="text-error" aria-hidden="true">*</span>
            </label>
            <input xuiInput [id]="'cc-parent-' + $index" [formControl]="parent" autocomplete="off" />
            <p class="text-foreground-muted text-xs" i18n="@@resourceCreate.parentHint">
              The existing parent this one is created under.
            </p>
          </div>
        }

        <cc-resource-form
          class="mt-2"
          [form]="schema"
          mode="create"
          [busy]="sending()"
          [submitLabel]="submitLabel"
          (submitted)="onSubmit($event)"
        >
          <a actions xuiButton variant="ghost" [routerLink]="backLink()" i18n="@@resourceCreate.cancel">Cancel</a>
        </cc-resource-form>
      }
    }
  `
})
export class ResourceCreate {
  // Bound from the route by `withComponentInputBinding()`.
  readonly subscriptionId = input.required<string>();
  readonly resourceGroup = input.required<string>();
  readonly provider = input.required<string>();
  readonly type = input.required<string>();
  readonly childType = input<string>();

  private readonly api = inject(PlatformApi);
  private readonly forms = inject(ResourceFormSource);
  private readonly router = inject(Router);
  private readonly blades = inject(BladeStackStore);

  protected readonly tenantId = activeTenantId();
  protected readonly resourceType = computed(() =>
    joinType(
      this.provider(),
      this.childType() === undefined ? [this.type()] : [this.type(), this.childType() as string]
    )
  );

  protected readonly form = pageState<ResourceForm | undefined>();
  protected readonly schema = computed(() => {
    const state = this.form();
    return state.kind === 'ready' ? state.value : undefined;
  });

  protected readonly sending = signal(false);
  protected readonly sendError = signal<string | null>(null);
  protected readonly refusedTitle = $localize`:@@resourceCreate.refused:The platform refused the create`;
  protected readonly submitLabel = $localize`:@@resourceCreate.submit:Create`;

  protected readonly name = new FormControl('', {
    nonNullable: true,
    validators: [Validators.required, Validators.maxLength(63), Validators.pattern(RESOURCE_NAME)]
  });

  /** One control per parent level, in path order. */
  protected readonly parents: FormControl<string>[] = [];

  protected readonly heading = computed(() => {
    const schema = this.schema();
    return schema === undefined
      ? $localize`:@@resourceCreate.headingPending:Create`
      : $localize`:@@resourceCreate.heading:Create ${schema.title}:type:`;
  });

  protected readonly backLink = computed(() => links.resourceGroup(this.subscriptionId(), this.resourceGroup()));

  private readonly renderer = viewChild(ResourceFormRenderer);

  constructor() {
    effect(() => {
      const resourceType = this.resourceType();
      const subscriptionId = this.subscriptionId();
      const resourceGroup = this.resourceGroup();

      untracked(() => {
        this.parents.length = 0;
        for (let i = 0; i < parentCountOf(resourceType); i++) {
          this.parents.push(
            new FormControl('', {
              nonNullable: true,
              validators: [Validators.required, Validators.pattern(RESOURCE_NAME)]
            })
          );
        }

        this.blades.open({
          id: links.create(subscriptionId, resourceGroup, resourceType),
          title: $localize`:@@resourceCreate.blade:Create`,
          route: links.create(subscriptionId, resourceGroup, resourceType)
        });

        void load(
          this.form,
          async () => {
            const form = await this.forms.form({ resourceType, apiVersion });
            if (form === undefined) {
              throw new Error(
                $localize`:@@resourceCreate.unknownType:${resourceType}:type: is not a resource type at api-version ${apiVersion}:version:.`
              );
            }
            return form;
          },
          () => this.resourceType() !== resourceType
        );
      });
    });
  }

  protected parentLabel(index: number): string {
    const segment = this.resourceType().split('/')[index + 1] ?? '';
    return $localize`:@@resourceCreate.parent:${segment}:segment: name`;
  }

  protected nameMessage(): string | null {
    if (!this.name.touched && !this.name.dirty) return null;
    const errors = this.name.errors;
    if (errors === null) return null;
    if (errors['server'] !== undefined) return String(errors['server']);
    if (errors['required'] !== undefined) return $localize`:@@resourceCreate.nameRequired:A name is required.`;
    return $localize`:@@resourceCreate.nameInvalid:Use 1–63 lowercase letters, digits and hyphens, starting and ending with a letter or digit.`;
  }

  protected async onSubmit(body: Record<string, unknown>): Promise<void> {
    const tenantId = this.tenantId();
    const schema = this.schema();
    this.name.markAsTouched();
    for (const parent of this.parents) parent.markAsTouched();

    if (
      tenantId === null ||
      schema === undefined ||
      this.name.invalid ||
      this.parents.some(p => p.invalid) ||
      this.sending()
    )
      return;

    const address: ResourceAddress = {
      tenantId,
      subscriptionId: this.subscriptionId(),
      resourceGroup: this.resourceGroup(),
      parents: this.parents.map(p => p.value),
      name: this.name.value
    };

    this.sending.set(true);
    this.sendError.set(null);

    try {
      const response = await putResource(this.api, verbsFor(schema.title), address, body);
      const blade = links.resource(address, schema.resourceType);
      const operationId = response.operationUrl === undefined ? null : operationIdOf(response.operationUrl);

      // A 202 is the contract; a 200/201 without an operation would be a scope-like type, and the
      // blade is still the right place to land.
      await this.router.navigateByUrl(operationId === null ? blade : links.operation(operationId, blade));
    } catch (error) {
      this.sending.set(false);

      if (!(error instanceof ApiCallError)) {
        this.sendError.set(error instanceof Error ? error.message : String(error));
        return;
      }

      if (
        error.status === 409 ||
        error.error.code === 'ResourceAlreadyExists' ||
        error.error.code === 'InvalidResourceName'
      ) {
        this.name.setErrors({ server: error.error.message });
        return;
      }

      if (error.error.target !== undefined || (error.error.details?.length ?? 0) > 0) {
        this.renderer()?.reject(error.error);
        return;
      }

      this.sendError.set(error.error.message);
    }
  }
}

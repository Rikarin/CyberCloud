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
import { ResourceGroupCreateContent, apiVersion } from '@cybercloud/api';
import { ResourceFormRenderer, ResourceFormSource, ScopeForm } from '@cybercloud/resource-forms';
import { BladeStackStore } from '@cybercloud/shell';
import { XuiButton } from '@xui/button';
import { XuiCallout } from '@xui/callout';
import { XuiInput } from '@xui/input';
import { XuiNonIdealState } from '@xui/non-ideal-state';
import { ApiCallError } from '../../app/api/http-transport';
import { PlatformApi } from '../../app/api/platform-api';
import { links } from '../../app/routes/portal-links';
import { NeedsTenant, PageStatus, activeTenantId, load, pageState } from '../shared/page-state';

/** The `ResourceGroupName` parameter's own pattern: a DNS-1123 label, 1–63 characters. */
const GROUP_NAME = /^[a-z0-9]([-a-z0-9]*[a-z0-9])?$/;

/**
 * The resource groups page: create one, or open one by name.
 *
 * ⚠ **There is no list endpoint for resource groups, and the empty state says so.** Issue #10
 * added collection routes for resources at resource-group scope; the scope API (#63) has a `GET`
 * and a `PUT` by name and nothing that enumerates a subscription's groups. This page therefore
 * offers the two things the API can do — create, and open by name — and an empty state that
 * names the gap rather than a list that is always empty. The day a
 * `/subscriptions/{s}/resourceGroups` collection lands, the list goes where the empty state is.
 *
 * The create form is the generated `scopeForms.resourceGroup` (`location`, required, no default —
 * "a group whose region were guessed would place a tenant's data somewhere nobody chose"), and the
 * name is this page's field because it is the address. ⚠ No operation follows a scope `PUT`.
 */
@Component({
  selector: 'cc-resource-groups',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    ReactiveFormsModule,
    RouterLink,
    ResourceFormRenderer,
    XuiButton,
    XuiCallout,
    XuiInput,
    XuiNonIdealState,
    NeedsTenant,
    PageStatus
  ],
  host: { class: 'block p-6' },
  template: `
    @if (tenantId() === null) {
      <cc-needs-tenant />
    } @else {
      <div class="flex flex-wrap items-start justify-between gap-3">
        <div>
          <h1 class="text-lg font-semibold" i18n="@@resourceGroups.heading">Resource groups</h1>
          <p class="text-foreground-muted mt-1 text-sm">
            <a class="underline" [routerLink]="subscriptionLink()">{{ subscriptionId() }}</a>
          </p>
        </div>
        <button
          xuiButton
          color="primary"
          size="sm"
          type="button"
          (click)="creating.set(!creating())"
          i18n="@@resourceGroups.new"
        >
          New resource group
        </button>
      </div>

      @if (creating()) {
        <section class="border-border mt-4 rounded-md border p-4" aria-labelledby="cc-rg-create-heading">
          <h2 class="text-sm font-semibold" id="cc-rg-create-heading" i18n="@@resourceGroups.createHeading">
            Create a resource group
          </h2>
          <cc-page-status [state]="form()" />

          @if (schema(); as schema) {
            @if (sendError(); as message) {
              <xui-callout class="mt-3" color="error" [title]="refusedTitle"
                ><p>{{ message }}</p></xui-callout
              >
            }

            <div class="mt-3 flex flex-col gap-1.5">
              <label class="text-sm font-medium" for="cc-rg-name">
                <ng-container i18n="@@resourceGroups.name">Name</ng-container>
                <span class="text-error" aria-hidden="true">*</span>
              </label>
              <input
                xuiInput
                id="cc-rg-name"
                [formControl]="name"
                autocomplete="off"
                [attr.aria-describedby]="nameMessage() === null ? 'cc-rg-name-hint' : 'cc-rg-name-error'"
                [attr.aria-invalid]="nameMessage() === null ? null : 'true'"
              />
              @if (nameMessage(); as message) {
                <p class="text-error text-xs" id="cc-rg-name-error" role="alert">{{ message }}</p>
              } @else {
                <p class="text-foreground-muted text-xs" id="cc-rg-name-hint" i18n="@@resourceGroups.nameHint">
                  1–63 lowercase letters, digits and hyphens. It is the group's address and cannot change.
                </p>
              }
            </div>

            <cc-resource-form
              [form]="schema"
              mode="create"
              [busy]="sending()"
              [submitLabel]="submitLabel"
              (submitted)="onSubmit($event)"
            >
              <button
                actions
                xuiButton
                variant="ghost"
                type="button"
                (click)="creating.set(false)"
                i18n="@@resourceGroups.cancel"
              >
                Cancel
              </button>
            </cc-resource-form>
          }
        </section>
      }

      <form class="mt-6 flex items-end gap-2" (ngSubmit)="onOpen()">
        <div class="flex flex-col gap-1.5">
          <label class="text-sm font-medium" for="cc-rg-open" i18n="@@resourceGroups.openLabel">Open by name</label>
          <input xuiInput id="cc-rg-open" [formControl]="open" autocomplete="off" />
        </div>
        <button xuiButton variant="outline" type="submit" [disabled]="open.invalid" i18n="@@resourceGroups.open">
          Open
        </button>
      </form>

      <xui-non-ideal-state class="mt-10" [title]="emptyTitle" [description]="emptyDescription" />
    }
  `
})
export class ResourceGroups {
  readonly subscriptionId = input.required<string>();

  private readonly api = inject(PlatformApi);
  private readonly forms = inject(ResourceFormSource);
  private readonly router = inject(Router);
  private readonly blades = inject(BladeStackStore);

  protected readonly tenantId = activeTenantId();
  protected readonly subscriptionLink = computed(() => links.subscription(this.subscriptionId()));

  protected readonly creating = signal(false);
  protected readonly form = pageState<ScopeForm>();
  protected readonly schema = computed(() => {
    const state = this.form();
    return state.kind === 'ready' ? state.value : null;
  });

  protected readonly sending = signal(false);
  protected readonly sendError = signal<string | null>(null);
  protected readonly name = new FormControl('', {
    nonNullable: true,
    validators: [Validators.required, Validators.maxLength(63), Validators.pattern(GROUP_NAME)]
  });
  protected readonly open = new FormControl('', {
    nonNullable: true,
    validators: [Validators.required, Validators.pattern(GROUP_NAME)]
  });

  protected readonly refusedTitle = $localize`:@@resourceGroups.refused:The platform refused the create`;
  protected readonly submitLabel = $localize`:@@resourceGroups.submit:Create resource group`;
  protected readonly emptyTitle = $localize`:@@resourceGroups.emptyTitle:No list endpoint yet`;
  protected readonly emptyDescription = $localize`:@@resourceGroups.emptyDescription:The API can create a resource group and read one by name, and cannot yet enumerate a subscription's groups. Open one by name above.`;

  private readonly renderer = viewChild(ResourceFormRenderer);

  constructor() {
    effect(() => {
      const subscriptionId = this.subscriptionId();
      untracked(() => {
        const route = links.resourceGroups(subscriptionId);
        this.blades.open({ id: route, title: $localize`:@@resourceGroups.blade:Resource groups`, route });
      });
    });

    effect(() => {
      if (!this.creating()) return;
      untracked(() => {
        if (this.form().kind === 'idle') void load(this.form, () => this.forms.scopeForm('resourceGroup', apiVersion));
      });
    });
  }

  protected nameMessage(): string | null {
    if (!this.name.touched && !this.name.dirty) return null;
    if (this.name.errors === null) return null;
    if (this.name.errors['server'] !== undefined) return String(this.name.errors['server']);
    if (this.name.errors['required'] !== undefined)
      return $localize`:@@resourceGroups.nameRequired:A name is required.`;
    return $localize`:@@resourceGroups.nameInvalid:Use 1–63 lowercase letters, digits and hyphens, starting and ending with a letter or digit.`;
  }

  protected onOpen(): void {
    if (this.open.invalid) return;
    void this.router.navigateByUrl(links.resourceGroup(this.subscriptionId(), this.open.value));
  }

  protected async onSubmit(body: Record<string, unknown>): Promise<void> {
    const tenantId = this.tenantId();
    this.name.markAsTouched();
    if (tenantId === null || this.name.invalid || this.sending()) return;

    this.sending.set(true);
    this.sendError.set(null);

    try {
      await this.api.createResourceGroup(
        tenantId,
        this.subscriptionId(),
        this.name.value,
        body as unknown as ResourceGroupCreateContent
      );
      await this.router.navigateByUrl(links.resourceGroup(this.subscriptionId(), this.name.value));
    } catch (error) {
      this.sending.set(false);

      if (
        error instanceof ApiCallError &&
        (error.error.target !== undefined || (error.error.details?.length ?? 0) > 0)
      ) {
        this.renderer()?.reject(error.error);
        return;
      }

      this.sendError.set(
        error instanceof ApiCallError ? error.error.message : error instanceof Error ? error.message : String(error)
      );
    }
  }
}

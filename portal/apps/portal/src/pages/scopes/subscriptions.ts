import {
  ChangeDetectionStrategy,
  Component,
  computed,
  effect,
  inject,
  signal,
  untracked,
  viewChild
} from '@angular/core';
import { FormControl, ReactiveFormsModule, Validators } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { ScopeResource, SubscriptionCreateContent, apiVersion } from '@cybercloud/api';
import { ResourceFormRenderer, ResourceFormSource, ScopeForm } from '@cybercloud/resource-forms';
import { BladeStackStore, TenantContextStore } from '@cybercloud/shell';
import { XuiButton } from '@xui/button';
import { XuiCallout } from '@xui/callout';
import { XuiInput } from '@xui/input';
import { XuiNonIdealState } from '@xui/non-ideal-state';
import { ApiCallError } from '../../app/api/http-transport';
import { PlatformApi } from '../../app/api/platform-api';
import { links } from '../../app/routes/portal-links';
import { NeedsTenant, PageStatus, activeTenantId, load, pageState } from '../shared/page-state';

/** Hyphenated `D` form only — the `SubscriptionId` parameter's own description. */
const SUBSCRIPTION_ID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/;

/**
 * The subscriptions page: the ones this sign-in can act in, and a create form.
 *
 * ⚠ **There is no list endpoint for subscriptions, and this page says so rather than faking one.**
 * Issue #63 gave the scope API a `GET` and a `PUT` by id, and the tenant a `GET`. What a person
 * may see is what their token grants, which is docs/plan/11's M2 token exchange — until it lands,
 * `TenantContextStore.subscriptions()` is what the context bar shows and what is listed here, and
 * it is empty on a fresh portal. A list invented from somewhere else would be the enumeration
 * oracle docs/plan/07 closes.
 *
 * The create form is `generated/forms/{apiVersion}.json`'s `scopeForms.subscription`, rendered by
 * the same renderer as every resource form; the id is this page's field because it is the address,
 * not the body. ⚠ No operation follows: a scope converges before the `PUT` returns — `201` the
 * first time, `200` on a repeat — so the answer is the blade, not the operation view.
 */
@Component({
  selector: 'cc-subscriptions',
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
          <h1 class="text-lg font-semibold" i18n="@@subscriptions.heading">Subscriptions</h1>
          <p class="text-foreground-muted mt-1 text-sm" i18n="@@subscriptions.subheading">
            The subscriptions your sign-in grants. The API has no endpoint that lists them.
          </p>
        </div>
        <button
          xuiButton
          color="primary"
          size="sm"
          type="button"
          (click)="creating.set(!creating())"
          i18n="@@subscriptions.new"
        >
          New subscription
        </button>
      </div>

      @if (creating()) {
        <section class="border-border mt-4 rounded-md border p-4" aria-labelledby="cc-sub-create-heading">
          <h2 class="text-sm font-semibold" id="cc-sub-create-heading" i18n="@@subscriptions.createHeading">
            Create a subscription
          </h2>
          <cc-page-status [state]="form()" />

          @if (schema(); as schema) {
            @if (sendError(); as message) {
              <xui-callout class="mt-3" color="error" [title]="refusedTitle"
                ><p>{{ message }}</p></xui-callout
              >
            }

            <div class="mt-3 flex flex-col gap-1.5">
              <label class="text-sm font-medium" for="cc-sub-id">
                <ng-container i18n="@@subscriptions.id">Subscription id</ng-container>
                <span class="text-error" aria-hidden="true">*</span>
              </label>
              <input
                xuiInput
                id="cc-sub-id"
                [formControl]="id"
                autocomplete="off"
                placeholder="00000000-0000-0000-0000-000000000000"
                [attr.aria-describedby]="idMessage() === null ? 'cc-sub-id-hint' : 'cc-sub-id-error'"
                [attr.aria-invalid]="idMessage() === null ? null : 'true'"
              />
              @if (idMessage(); as message) {
                <p class="text-error text-xs" id="cc-sub-id-error" role="alert">{{ message }}</p>
              } @else {
                <p class="text-foreground-muted text-xs" id="cc-sub-id-hint" i18n="@@subscriptions.idHint">
                  A UUID in hyphenated form. It is the subscription's address and cannot change.
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
                i18n="@@subscriptions.cancel"
              >
                Cancel
              </button>
            </cc-resource-form>
          }
        </section>
      }

      @if (known().length === 0) {
        <xui-non-ideal-state class="mt-10" [title]="emptyTitle" [description]="emptyDescription" />
      } @else {
        <ul class="divide-border mt-4 divide-y">
          @for (subscription of known(); track subscription.id) {
            <li class="flex items-center justify-between py-2">
              <a class="underline" [routerLink]="link(subscription.id)">{{ subscription.displayName }}</a>
              <code class="text-foreground-muted text-xs">{{ subscription.id }}</code>
            </li>
          }
        </ul>
      }
    }
  `
})
export class Subscriptions {
  private readonly api = inject(PlatformApi);
  private readonly forms = inject(ResourceFormSource);
  private readonly context = inject(TenantContextStore);
  private readonly router = inject(Router);
  private readonly blades = inject(BladeStackStore);

  protected readonly tenantId = activeTenantId();
  protected readonly known = computed(() => this.context.subscriptions());

  protected readonly creating = signal(false);
  protected readonly form = pageState<ScopeForm>();
  protected readonly schema = computed(() => {
    const state = this.form();
    return state.kind === 'ready' ? state.value : null;
  });

  protected readonly sending = signal(false);
  protected readonly sendError = signal<string | null>(null);
  protected readonly id = new FormControl('', {
    nonNullable: true,
    validators: [Validators.required, Validators.pattern(SUBSCRIPTION_ID)]
  });

  protected readonly refusedTitle = $localize`:@@subscriptions.refused:The platform refused the create`;
  protected readonly submitLabel = $localize`:@@subscriptions.submit:Create subscription`;
  protected readonly emptyTitle = $localize`:@@subscriptions.emptyTitle:No subscriptions known`;
  protected readonly emptyDescription = $localize`:@@subscriptions.emptyDescription:The context bar fills in once the portal holds an access token. Create one above, or open one by id from a link.`;

  private readonly renderer = viewChild(ResourceFormRenderer);

  constructor() {
    this.blades.open({
      id: links.subscriptions(),
      title: $localize`:@@subscriptions.blade:Subscriptions`,
      route: links.subscriptions()
    });

    effect(() => {
      if (!this.creating()) return;
      untracked(() => {
        if (this.form().kind === 'idle') void load(this.form, () => this.forms.scopeForm('subscription', apiVersion));
      });
    });
  }

  protected link(subscriptionId: string): string {
    return links.subscription(subscriptionId);
  }

  protected idMessage(): string | null {
    if (!this.id.touched && !this.id.dirty) return null;
    if (this.id.errors === null) return null;
    if (this.id.errors['server'] !== undefined) return String(this.id.errors['server']);
    if (this.id.errors['required'] !== undefined) return $localize`:@@subscriptions.idRequired:An id is required.`;
    return $localize`:@@subscriptions.idInvalid:Use the hyphenated UUID form, lowercase.`;
  }

  protected async onSubmit(body: Record<string, unknown>): Promise<void> {
    const tenantId = this.tenantId();
    this.id.markAsTouched();
    if (tenantId === null || this.id.invalid || this.sending()) return;

    this.sending.set(true);
    this.sendError.set(null);

    try {
      const response = await this.api.createSubscription(
        tenantId,
        this.id.value,
        body as unknown as SubscriptionCreateContent
      );
      const created: ScopeResource = response.value;

      // Known from here on, so the context bar and the list agree with what the API just said.
      this.context.load(this.context.tenants(), [
        ...this.context.subscriptions().filter(s => s.id !== this.id.value),
        { id: this.id.value, tenantId, displayName: created.name }
      ]);

      await this.router.navigateByUrl(links.subscription(this.id.value));
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

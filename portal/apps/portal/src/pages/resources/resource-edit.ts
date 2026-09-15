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
import { Router, RouterLink } from '@angular/router';
import { apiVersion } from '@cybercloud/api';
import { ResourceForm, ResourceFormRenderer, ResourceFormSource } from '@cybercloud/resource-forms';
import { BladeStackStore } from '@cybercloud/shell';
import { XuiButton } from '@xui/button';
import { XuiCallout } from '@xui/callout';
import { ApiCallError, operationIdOf } from '../../app/api/http-transport';
import { PlatformApi } from '../../app/api/platform-api';
import { ResourceAddress, ResourceEnvelope, putResource, readResource, verbsFor } from '../../app/api/resource-verbs';
import { joinType, links } from '../../app/routes/portal-links';
import { NeedsTenant, PageStatus, activeTenantId, load, pageState } from '../shared/page-state';

/**
 * The edit blade: the same generated form as the create blade, in `edit` mode over the resource's
 * current body.
 *
 * ⚠ **It sends a `PUT`, not a `PATCH`, and that is a choice.** The generated client has both. A
 * merge patch would be the lighter call, but a form that shows every field and sends only the
 * ones that changed has to diff the body it loaded against the body it holds — and a field that
 * was absent, shown at its default, then left alone would be sent or not depending on how the
 * diff treats defaults. A full replacement sends what the form shows, which is what the person
 * pressing Save is looking at. `PUT` is also the verb the write path's twelve steps are specified
 * against (docs/plan/08 § The write path, end to end).
 *
 * ⚠ **The body is read once, when the blade opens, and the immutable fields are locked from it.**
 * The generated `disabledAfterCreate` fields are disabled and still sent, per `buildForm`'s
 * header: a replacement without `location` is a `400`, not "unchanged". Issue #72's double-nested
 * body is unwrapped the same way the blade does it, so the form loads the inner object.
 */
@Component({
  selector: 'cc-resource-edit',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, ResourceFormRenderer, XuiButton, XuiCallout, NeedsTenant, PageStatus],
  host: { class: 'block p-6' },
  template: `
    @if (tenantId() === null) {
      <cc-needs-tenant />
    } @else {
      <h1 class="text-lg font-semibold">{{ heading() }}</h1>
      <p class="text-foreground-muted mt-1 text-sm">{{ resourceType() }} · {{ resourceGroup() }}</p>

      <cc-page-status [state]="state()" [retryLink]="bladeLink()" />

      @if (ready(); as ready) {
        @if (sendError(); as message) {
          <xui-callout class="mt-4" color="error" [title]="refusedTitle">
            <p>{{ message }}</p>
          </xui-callout>
        }

        <cc-resource-form
          class="mt-2"
          [form]="ready.form"
          mode="edit"
          [initial]="ready.body"
          [busy]="sending()"
          [submitLabel]="submitLabel"
          (submitted)="onSubmit($event)"
        >
          <a actions xuiButton variant="ghost" [routerLink]="bladeLink()" i18n="@@resourceEdit.cancel">Cancel</a>
        </cc-resource-form>
      }
    }
  `
})
export class ResourceEdit {
  readonly subscriptionId = input.required<string>();
  readonly resourceGroup = input.required<string>();
  readonly provider = input.required<string>();
  readonly type = input.required<string>();
  readonly name = input.required<string>();
  readonly parent = input<string>();
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

  protected readonly address = computed<ResourceAddress | null>(() => {
    const tenantId = this.tenantId();
    if (tenantId === null) return null;

    return {
      tenantId,
      subscriptionId: this.subscriptionId(),
      resourceGroup: this.resourceGroup(),
      parents: this.parent() === undefined ? [] : [this.parent() as string],
      name: this.name()
    };
  });

  protected readonly state = pageState<{ form: ResourceForm; body: Record<string, unknown> }>();
  protected readonly ready = computed(() => {
    const state = this.state();
    return state.kind === 'ready' ? state.value : null;
  });

  protected readonly sending = signal(false);
  protected readonly sendError = signal<string | null>(null);
  protected readonly refusedTitle = $localize`:@@resourceEdit.refused:The platform refused the change`;
  protected readonly submitLabel = $localize`:@@resourceEdit.submit:Save`;

  protected readonly heading = computed(() => $localize`:@@resourceEdit.heading:Edit ${this.name()}:name:`);
  protected readonly bladeLink = computed(() => {
    const address = this.address();
    return address === null
      ? links.resourceGroup(this.subscriptionId(), this.resourceGroup())
      : links.resource(address, this.resourceType());
  });

  private readonly renderer = viewChild(ResourceFormRenderer);

  constructor() {
    effect(() => {
      const address = this.address();
      const resourceType = this.resourceType();

      untracked(() => {
        if (address === null) return;

        const route = links.edit(address, resourceType);
        this.blades.open({
          id: route,
          title: $localize`:@@resourceEdit.blade:Edit`,
          route,
          resourceId: links.resource(address, resourceType)
        });

        void load(
          this.state,
          async () => {
            const form = await this.forms.form({ resourceType, apiVersion });
            if (form === undefined)
              throw new Error(
                $localize`:@@resourceEdit.unknownType:${resourceType}:type: is not a resource type at api-version ${apiVersion}:version:.`
              );

            const response = await readResource(this.api, verbsFor(form.title), address);
            return { form, body: bodyOf(response.value) };
          },
          () => this.address() !== address
        );
      });
    });
  }

  protected async onSubmit(body: Record<string, unknown>): Promise<void> {
    const address = this.address();
    const ready = this.ready();
    if (address === null || ready === null || this.sending()) return;

    this.sending.set(true);
    this.sendError.set(null);

    try {
      const response = await putResource(this.api, verbsFor(ready.form.title), address, body);
      const blade = links.resource(address, ready.form.resourceType);
      const operationId = response.operationUrl === undefined ? null : operationIdOf(response.operationUrl);

      await this.router.navigateByUrl(operationId === null ? blade : links.operation(operationId, blade));
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

/**
 * The request body a resource's response maps back to: `location`, `properties` and `tags`, with
 * issue #72's wrapper removed when it is there. The envelope's own members — `id`, `etag`,
 * `provisioningState` — are read-only and a `PUT` carrying them is refused.
 */
function bodyOf(resource: ResourceEnvelope): Record<string, unknown> {
  const properties = resource.properties as Record<string, unknown> | undefined;
  const inner = properties?.['properties'];
  const unwrapped = inner !== undefined && typeof inner === 'object' && inner !== null ? inner : properties;

  return {
    ...(resource.location === undefined ? {} : { location: resource.location }),
    ...(unwrapped === undefined ? {} : { properties: unwrapped }),
    ...(resource.tags === undefined ? {} : { tags: resource.tags })
  };
}

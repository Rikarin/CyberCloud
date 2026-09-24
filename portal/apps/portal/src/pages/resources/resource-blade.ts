import { ChangeDetectionStrategy, Component, computed, effect, inject, input, signal, untracked } from '@angular/core';
import { Router, RouterLink } from '@angular/router';
import { apiVersion } from '@cybercloud/api';
import { FormField, ResourceForm, ResourceFormSource, hintOf, isGroup, isTagBag } from '@cybercloud/resource-forms';
import { BladeStackStore } from '@cybercloud/shell';
import { XuiButton } from '@xui/button';
import { XuiCallout } from '@xui/callout';
import { XuiDescriptions, XuiDescriptionsItem } from '@xui/descriptions';
import { XuiTag } from '@xui/tag';
import { operationIdOf } from '../../app/api/http-transport';
import { PlatformApi } from '../../app/api/platform-api';
import {
  ResourceAddress,
  ResourceEnvelope,
  deleteResource,
  readResource,
  verbsFor
} from '../../app/api/resource-verbs';
import { joinType, links } from '../../app/routes/portal-links';
import { NeedsTenant, PageStatus, activeTenantId, failure, load, pageState } from '../shared/page-state';

/** One property of the resource, labelled by the form schema rather than by its JSON key. */
interface PropertyRow {
  readonly pointer: string;
  readonly label: string;
  readonly hint: string;
  readonly value: string;
  readonly depth: number;
}

/**
 * The resource blade: one resource, read through the generated client.
 *
 * docs/plan/20 § Information architecture names the left rail — Overview · Activity · Access ·
 * Tags · Locks · Metrics · Logs · Diagnose · Settings — and this is the Overview, with Tags and
 * the Settings entry point (Edit) on it. Activity is `OperationView`, reached from a create or a
 * delete; Access is `AccessBlade` at `…/{name}/access`, over the role-assignment address issue
 * #70 gave every resource; the rest wait on endpoints that do not exist yet (metrics on
 * docs/plan/16) and are not stubbed here, because a rail item that opens an empty pane is worse
 * than no rail item.
 *
 * ⚠ **The body is shown as served, and the served body is wrong today.** Issue #72: the gateway
 * nests `properties` inside `properties` and writes `location` twice. The blade reads the schema's
 * pointers against whatever the platform returns, so it renders the inner object when the outer
 * one is a wrapper and the raw body panel below always shows the truth. When #72 lands nothing
 * here changes; the wrapper case simply stops occurring.
 *
 * ⚠ **Delete is not optimistic and not one click.** Eighteen of the twenty-three types declare
 * no soft-delete window (`softDeleteDays: 0`), so for them a `DELETE` is permanent; the other
 * five are recoverable for the days the schema says, and the prompt says which. Either way the
 * button asks for the name to be typed back, and the answer is an operation the view polls —
 * docs/plan/20 § Live updates: "anything that creates, deletes or costs money … shows the
 * operation's real progress."
 *
 * ⚠ **The envelope is typed from the document since issue #85.** `Resource` in `@cybercloud/api`
 * is the generated `id`, `name`, `type`, `provisioningState` and `etag`, and every per-type
 * `{Type}Resource` extends it and its body; `ResourceEnvelope` in `resource-verbs.ts` extends
 * the same `Resource` for a page that only learns the type at run time. Until that issue the
 * generated interface carried four of the eight members the gateway writes and this file typed
 * the rest by hand.
 */
@Component({
  selector: 'cc-resource-blade',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, XuiButton, XuiCallout, XuiDescriptions, XuiDescriptionsItem, XuiTag, NeedsTenant, PageStatus],
  host: { class: 'block p-6' },
  template: `
    @if (tenantId() === null) {
      <cc-needs-tenant />
    } @else {
      <div class="flex flex-wrap items-start justify-between gap-3">
        <div>
          <h1 class="text-lg font-semibold">{{ name() }}</h1>
          <p class="text-foreground-muted mt-1 text-sm">{{ typeTitle() }} · {{ resourceType() }}</p>
        </div>
        @if (resource(); as resource) {
          <div class="flex gap-2">
            @if (explorers(); as explorers) {
              <a xuiButton variant="outline" size="sm" [routerLink]="explorers.metrics" i18n="@@resourceBlade.metrics"
                >Metrics</a
              >
              <a xuiButton variant="outline" size="sm" [routerLink]="explorers.logs" i18n="@@resourceBlade.logs"
                >Logs</a
              >
            }
            <a xuiButton variant="outline" size="sm" [routerLink]="accessLink()" i18n="@@resourceBlade.access"
              >Access</a
            >
            <a xuiButton variant="outline" size="sm" [routerLink]="editLink()" i18n="@@resourceBlade.edit">Edit</a>
            <button
              xuiButton
              variant="outline"
              color="error"
              size="sm"
              type="button"
              (click)="askDelete()"
              i18n="@@resourceBlade.delete"
            >
              Delete
            </button>
          </div>
        }
      </div>

      <cc-page-status [state]="state()" [retryLink]="groupLink()" />

      @if (resource(); as resource) {
        <xui-descriptions class="mt-4" [column]="2" bordered [title]="overviewTitle">
          <xui-descriptions-item [label]="labels.state">
            <span [attr.data-provisioning-state]="resource.provisioningState">{{
              resource.provisioningState ?? '—'
            }}</span>
          </xui-descriptions-item>
          <xui-descriptions-item [label]="labels.location">{{ resource.location ?? '—' }}</xui-descriptions-item>
          <xui-descriptions-item [label]="labels.resourceGroup">
            <a class="underline" [routerLink]="groupLink()">{{ resourceGroup() }}</a>
          </xui-descriptions-item>
          <xui-descriptions-item [label]="labels.etag"
            ><code class="text-xs">{{ resource.etag ?? '—' }}</code></xui-descriptions-item
          >
          <xui-descriptions-item [label]="labels.id" [span]="2"
            ><code class="text-xs break-all">{{ resource.id }}</code></xui-descriptions-item
          >
        </xui-descriptions>

        <h2 class="mt-6 text-sm font-semibold" i18n="@@resourceBlade.tags">Tags</h2>
        @if (tagEntries().length === 0) {
          <p class="text-foreground-muted mt-1 text-sm" i18n="@@resourceBlade.noTags">No tags.</p>
        } @else {
          <ul class="mt-1 flex flex-wrap gap-1.5">
            @for (tag of tagEntries(); track tag[0]) {
              <li>
                <xui-tag minimal>{{ tag[0] }}={{ tag[1] }}</xui-tag>
              </li>
            }
          </ul>
        }

        <h2 class="mt-6 text-sm font-semibold" i18n="@@resourceBlade.properties">Properties</h2>
        @if (rows().length === 0) {
          <p class="text-foreground-muted mt-1 text-sm" i18n="@@resourceBlade.noProperties">
            The body carries no properties the schema names.
          </p>
        } @else {
          <dl class="mt-1 grid grid-cols-[max-content_1fr] gap-x-6 gap-y-1 text-sm">
            @for (row of rows(); track row.pointer) {
              <dt class="text-foreground-muted" [style.padding-inline-start.rem]="row.depth" [attr.title]="row.hint">
                {{ row.label }}
              </dt>
              <dd class="break-all">
                <code class="text-xs">{{ row.value }}</code>
              </dd>
            }
          </dl>
        }

        <details class="mt-6">
          <summary class="cursor-pointer text-sm font-semibold" i18n="@@resourceBlade.raw">Raw body</summary>
          <pre class="bg-surface-sunken mt-2 overflow-x-auto rounded-md p-3 text-xs">{{ raw() }}</pre>
        </details>

        @if (deleting(); as prompt) {
          <xui-callout class="mt-6" color="error" [title]="deleteTitle">
            @if (softDeleteDays() > 0) {
              <p i18n="@@resourceBlade.deleteBodyRecoverable">
                The resource can be restored for {{ softDeleteDays() }} days, and is purged after that. Type the
                resource name to confirm.
              </p>
            } @else {
              <p i18n="@@resourceBlade.deleteBody">
                This is permanent: the type declares no soft-delete window. Type the resource name to confirm.
              </p>
            }
            <label
              class="mt-2 block text-sm font-medium"
              for="cc-delete-confirm"
              i18n="@@resourceBlade.deleteConfirmLabel"
              >Resource name</label
            >
            <input
              id="cc-delete-confirm"
              class="border-border bg-surface mt-1 block rounded-md border px-2 py-1 text-sm"
              type="text"
              autocomplete="off"
              [value]="prompt.typed"
              (input)="onTyped($event)"
            />
            <div class="mt-3 flex gap-2">
              <button
                xuiButton
                color="error"
                size="sm"
                type="button"
                [disabled]="prompt.typed !== name() || sending()"
                [loading]="sending()"
                (click)="confirmDelete()"
                i18n="@@resourceBlade.deleteConfirm"
              >
                Delete
              </button>
              <button
                xuiButton
                variant="ghost"
                size="sm"
                type="button"
                (click)="deleting.set(null)"
                i18n="@@resourceBlade.deleteCancel"
              >
                Keep it
              </button>
            </div>
            @if (deleteError(); as message) {
              <p class="text-error mt-2 text-sm" role="alert">{{ message }}</p>
            }
          </xui-callout>
        }
      }
    }
  `
})
export class ResourceBlade {
  readonly subscriptionId = input.required<string>();
  readonly resourceGroup = input.required<string>();
  readonly provider = input.required<string>();
  readonly type = input.required<string>();
  readonly name = input.required<string>();
  /** Present for a child type: the parent's name and the child's type segment. */
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

  protected readonly state = pageState<{ resource: ResourceEnvelope; form: ResourceForm | undefined }>();
  private readonly ready = computed(() => {
    const state = this.state();
    return state.kind === 'ready' ? state.value : null;
  });
  protected readonly resource = computed(() => this.ready()?.resource ?? null);
  private readonly form = computed(() => this.ready()?.form);

  protected readonly typeTitle = computed(() => this.form()?.title ?? '');
  protected readonly softDeleteDays = computed(() => this.form()?.softDeleteDays ?? 0);
  protected readonly groupLink = computed(() => links.resourceGroup(this.subscriptionId(), this.resourceGroup()));
  protected readonly editLink = computed(() => {
    const address = this.address();
    return address === null ? this.groupLink() : links.edit(address, this.resourceType());
  });
  protected readonly accessLink = computed(() => {
    const address = this.address();
    return address === null ? this.groupLink() : links.resourceAccess(address, this.resourceType());
  });

  /**
   * The Metrics and Logs rail items of docs/plan/20 § Information architecture, on the one type that
   * has data to explore: a Monitor workspace (#41). Every other type's telemetry lives in a
   * workspace, and reaching it from the resource is owed with the resource-id enrichment docs/plan/16
   * § Ingest describes.
   */
  protected readonly explorers = computed(() =>
    this.resourceType() === 'CyberCloud.Monitor/workspaces' && this.parent() === undefined
      ? {
          metrics: links.workspaceMetrics(this.subscriptionId(), this.resourceGroup(), this.name()),
          logs: links.workspaceLogs(this.subscriptionId(), this.resourceGroup(), this.name())
        }
      : null
  );

  protected readonly tagEntries = computed(() => Object.entries(this.resource()?.tags ?? {}));
  protected readonly raw = computed(() => JSON.stringify(this.resource(), null, 2));

  /**
   * The properties, labelled by the schema. Issue #72's double nesting is handled by looking one
   * level deeper when the body's `properties` is a wrapper around another `properties`.
   */
  protected readonly rows = computed<readonly PropertyRow[]>(() => {
    const resource = this.resource();
    const form = this.form();
    if (resource === null || form === undefined) return [];

    const body = unwrap(resource);
    const rows: PropertyRow[] = [];

    for (const field of form.fields) {
      if (field.jsonPointer === '/location' || field.jsonPointer === '/tags') continue;
      if (isGroup(field)) continue;

      const value = field.jsonPointer
        .split('/')
        .slice(1)
        .reduce<unknown>((v, k) => (v as Record<string, unknown> | undefined)?.[k], body);
      if (value === undefined) continue;

      rows.push({
        pointer: field.jsonPointer,
        label: field.label,
        hint: hintOf(field),
        value: renderValue(field, value),
        depth: Math.max(0, field.jsonPointer.split('/').length - 3)
      });
    }

    return rows;
  });

  protected readonly deleting = signal<{ typed: string } | null>(null);
  protected readonly sending = signal(false);
  protected readonly deleteError = signal<string | null>(null);

  protected readonly overviewTitle = $localize`:@@resourceBlade.overview:Overview`;
  protected readonly deleteTitle = $localize`:@@resourceBlade.deleteTitle:Delete this resource?`;
  protected readonly labels = {
    state: $localize`:@@resourceBlade.state:Provisioning state`,
    location: $localize`:@@resourceBlade.location:Location`,
    resourceGroup: $localize`:@@resourceBlade.resourceGroup:Resource group`,
    etag: $localize`:@@resourceBlade.etag:ETag`,
    id: $localize`:@@resourceBlade.id:Id`
  };

  constructor() {
    effect(() => {
      const address = this.address();
      const resourceType = this.resourceType();

      untracked(() => {
        this.deleting.set(null);

        if (address === null) return;

        const route = links.resource(address, resourceType);
        this.blades.open({ id: route, title: address.name, route, resourceId: route });

        void load(
          this.state,
          async () => {
            const form = await this.forms.form({ resourceType, apiVersion });
            const verbs = verbsFor(form?.title ?? '');
            if (form === undefined)
              throw new Error(
                $localize`:@@resourceBlade.unknownType:${resourceType}:type: is not a resource type at api-version ${apiVersion}:version:.`
              );

            const response = await readResource(this.api, verbs, address);
            return { resource: response.value, form };
          },
          () => this.address() !== address
        );
      });
    });
  }

  protected onTyped(event: Event): void {
    this.deleting.set({ typed: (event.target as HTMLInputElement).value });
  }

  protected askDelete(): void {
    this.deleteError.set(null);
    this.deleting.set({ typed: '' });
  }

  protected async confirmDelete(): Promise<void> {
    const address = this.address();
    const form = this.form();
    if (address === null || form === undefined || this.sending()) return;

    this.sending.set(true);

    try {
      const response = await deleteResource(this.api, verbsFor(form.title), address);
      const operationId = response.operationUrl === undefined ? null : operationIdOf(response.operationUrl);

      await this.router.navigateByUrl(
        operationId === null ? this.groupLink() : links.operation(operationId, this.groupLink())
      );
    } catch (error) {
      this.sending.set(false);
      const failed = failure(error);
      this.deleteError.set(failed.kind === 'failed' ? failed.message : String(error));
    }
  }
}

/** The body the schema's pointers apply to — the resource, or its inner wrapper under issue #72. */
function unwrap(resource: ResourceEnvelope): Record<string, unknown> {
  const properties = resource.properties as Record<string, unknown> | undefined;
  const inner = properties?.['properties'];

  if (inner !== undefined && typeof inner === 'object' && inner !== null) {
    return { ...resource, properties: inner } as unknown as Record<string, unknown>;
  }

  return resource as unknown as Record<string, unknown>;
}

function renderValue(field: FormField, value: unknown): string {
  if (isTagBag(field) && typeof value === 'object' && value !== null) {
    return Object.entries(value as Record<string, unknown>)
      .map(([k, v]) => `${k}=${String(v)}`)
      .join(', ');
  }

  if (Array.isArray(value)) return value.map(String).join(', ');
  if (typeof value === 'object' && value !== null) return JSON.stringify(value);
  if (field.choices !== undefined) return field.choices.find(c => c.value === value)?.label ?? String(value);

  return String(value);
}

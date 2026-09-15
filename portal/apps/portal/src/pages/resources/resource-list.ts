import { ChangeDetectionStrategy, Component, computed, effect, inject, input, signal, untracked } from '@angular/core';
import { Router, RouterLink } from '@angular/router';
import { apiVersion } from '@cybercloud/api';
import { ResourceForm, ResourceFormSource } from '@cybercloud/resource-forms';
import { BladeStackStore } from '@cybercloud/shell';
import { XuiButton } from '@xui/button';
import { XuiNonIdealState } from '@xui/non-ideal-state';
import { XuiSelect } from '@xui/select';
import { XuiTable, XuiTd, XuiTh, XuiTr } from '@xui/table';
import { PlatformApi } from '../../app/api/platform-api';
import { ResourceEnvelope, listResources, parentCountOf, verbsFor } from '../../app/api/resource-verbs';
import { links } from '../../app/routes/portal-links';
import { NeedsTenant, PageStatus, activeTenantId, load, pageState } from '../shared/page-state';

/** One fetched page plus everything before it. */
interface Listing {
  readonly form: ResourceForm;
  readonly items: readonly ResourceEnvelope[];
  /** Absent on the last page — the only signal that there is no more, per `Page<T>`. */
  readonly nextLink?: string;
}

/**
 * The resource list: one type's resources in one resource group, through the generated `list*`
 * verbs issue #10 added.
 *
 * ⚠ **What this is and is not.** docs/plan/20 § Information architecture wants "`@xui/data-table`
 * over the resource-graph projection — virtual scroll, server-side filter/sort, column chooser,
 * saved views, CSV export". The resource graph is docs/plan/08's projection and does not exist
 * (issue #54 is its ClickHouse form); what exists is a collection route per type, at resource-group
 * scope, filtered per member. So the list is per type, per group, and paged with `nextLink` —
 * which is what the API can answer today, stated rather than approximated.
 *
 * ⚠ **A short page never means "that is all there is."** `Page<T>`'s own doc: the filter runs a
 * permission check per member, the page is clamped, and the envelope carries no count. The only
 * stop condition is an absent `nextLink`, and "Load more" shows for as long as there is one. There
 * is no total anywhere on this page because the API deliberately does not give one.
 *
 * ⚠ **The next page is fetched by `skipToken`, never by following `nextLink`.** The link is an
 * absolute URL the server chose; sending it through the transport would put a server-supplied
 * origin in front of the bearer token. The token is read out of its query string and sent through
 * the same generated verb, on the portal's own origin.
 */
@Component({
  selector: 'cc-resource-list',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, XuiButton, XuiSelect, XuiTable, XuiTr, XuiTh, XuiTd, XuiNonIdealState, NeedsTenant, PageStatus],
  host: { class: 'block p-6' },
  template: `
    @if (tenantId() === null) {
      <cc-needs-tenant />
    } @else {
      <div class="flex flex-wrap items-end justify-between gap-3">
        <div>
          <h1 class="text-lg font-semibold" i18n="@@resources.heading">Resources</h1>
          <p class="text-foreground-muted mt-1 text-sm">
            <a class="underline" [routerLink]="groupLink()">{{ resourceGroup() }}</a>
          </p>
        </div>

        <div class="flex items-end gap-2">
          <div class="flex flex-col gap-1">
            <span class="text-foreground-muted text-xs font-medium" id="cc-type-label" i18n="@@resources.typeLabel"
              >Resource type</span
            >
            <xui-select
              class="min-w-72"
              [items]="types()"
              [itemText]="typeText"
              [value]="selected()"
              [filterable]="true"
              [placeholder]="typePlaceholder"
              aria-labelledby="cc-type-label"
              (valueChange)="onType($event)"
            />
          </div>
          @if (selected(); as form) {
            <a xuiButton color="primary" size="sm" [routerLink]="createLink()">{{ createLabel(form) }}</a>
          }
        </div>
      </div>

      @if (!type()) {
        <xui-non-ideal-state class="mt-10" [title]="pickTitle" [description]="pickDescription" />
      } @else {
        <cc-page-status [state]="state()" [retryLink]="groupLink()" />

        @if (listing(); as listing) {
          @if (listing.items.length === 0 && !listing.nextLink) {
            <xui-non-ideal-state class="mt-10" [title]="emptyTitle(listing.form)" [description]="emptyDescription" />
          } @else {
            <xui-table class="mt-4" striped aria-labelledby="cc-type-label">
              <xui-tr>
                <xui-th class="w-64" i18n="@@resources.col.name">Name</xui-th>
                <xui-th class="w-40" i18n="@@resources.col.state">Provisioning state</xui-th>
                <xui-th class="w-32" i18n="@@resources.col.location">Location</xui-th>
                <xui-th class="flex-1" i18n="@@resources.col.tags">Tags</xui-th>
              </xui-tr>
              @for (item of listing.items; track item.id) {
                <xui-tr>
                  <xui-td class="w-64" truncate
                    ><a class="underline" [routerLink]="itemLink(item)">{{ item.name }}</a></xui-td
                  >
                  <xui-td class="w-40">{{ item.provisioningState ?? '—' }}</xui-td>
                  <xui-td class="w-32">{{ item.location ?? '—' }}</xui-td>
                  <xui-td class="flex-1" truncate>{{ tagsOf(item) }}</xui-td>
                </xui-tr>
              }
            </xui-table>

            @if (listing.nextLink) {
              <button
                xuiButton
                variant="outline"
                size="sm"
                class="mt-3"
                type="button"
                [loading]="more()"
                (click)="loadMore()"
                i18n="@@resources.more"
              >
                Load more
              </button>
            } @else if (listing.items.length > 0) {
              <p class="text-foreground-muted mt-3 text-xs" i18n="@@resources.end">No further pages.</p>
            }
          }
        }
      }
    }
  `
})
export class ResourceList {
  readonly subscriptionId = input.required<string>();
  readonly resourceGroup = input.required<string>();
  /** Bound from `?type=`. */
  readonly type = input<string>();

  private readonly api = inject(PlatformApi);
  private readonly forms = inject(ResourceFormSource);
  private readonly router = inject(Router);
  private readonly blades = inject(BladeStackStore);

  protected readonly tenantId = activeTenantId();
  protected readonly types = signal<readonly ResourceForm[]>([]);
  protected readonly selected = computed(() => this.types().find(f => f.resourceType === this.type()) ?? null);

  protected readonly state = pageState<Listing>();
  protected readonly listing = computed(() => {
    const state = this.state();
    return state.kind === 'ready' ? state.value : null;
  });
  protected readonly more = signal(false);

  protected readonly groupLink = computed(() => links.resourceGroup(this.subscriptionId(), this.resourceGroup()));
  protected readonly createLink = computed(() =>
    links.create(this.subscriptionId(), this.resourceGroup(), this.type() ?? '')
  );

  protected readonly typePlaceholder = $localize`:@@resources.typePlaceholder:Choose a type`;
  protected readonly pickTitle = $localize`:@@resources.pickTitle:Choose a resource type`;
  protected readonly pickDescription = $localize`:@@resources.pickDescription:The API lists one type per resource group at a time. Pick one to list its resources here.`;
  protected readonly emptyDescription = $localize`:@@resources.emptyDescription:Nothing of this type is visible to you in this resource group.`;

  protected readonly typeText = (form: ResourceForm): string => form.plural;

  constructor() {
    // ⚠ Top-level types only. A child type — a node pool, a subnet, a bucket — is listed under its
    // parent (`…/managedClusters/{name}/agentPools`), which is the parent blade's rail item to
    // build; offered here it would need a parent name this page has nowhere to ask for.
    void this.forms
      .types(apiVersion)
      .then(types => this.types.set(types.filter(t => parentCountOf(t.resourceType) === 0)));

    effect(() => {
      const tenantId = this.tenantId();
      const subscriptionId = this.subscriptionId();
      const resourceGroup = this.resourceGroup();
      const type = this.type();

      untracked(() => {
        const route = links.resources(subscriptionId, resourceGroup);
        this.blades.open({ id: route, title: $localize`:@@resources.blade:Resources`, route });

        if (tenantId === null || type === undefined || type.length === 0) {
          this.state.set({ kind: 'idle' });
          return;
        }

        void load(
          this.state,
          async () => {
            const form = await this.forms.form({ resourceType: type, apiVersion });
            if (form === undefined)
              throw new Error(
                $localize`:@@resources.unknownType:${type}:type: is not a resource type at api-version ${apiVersion}:version:.`
              );

            const page = await listResources(this.api, verbsFor(form.title), {
              tenantId,
              subscriptionId,
              resourceGroup,
              parents: []
            });
            return {
              form,
              items: page.value.value,
              ...(page.value.nextLink === undefined ? {} : { nextLink: page.value.nextLink })
            };
          },
          () => this.type() !== type || this.tenantId() !== tenantId
        );
      });
    });
  }

  protected onType(form: ResourceForm | null): void {
    void this.router.navigate([], { queryParams: { type: form?.resourceType ?? null }, queryParamsHandling: 'merge' });
  }

  protected async loadMore(): Promise<void> {
    const listing = this.listing();
    const tenantId = this.tenantId();
    if (listing === null || listing.nextLink === undefined || tenantId === null || this.more()) return;

    const skipToken = skipTokenOf(listing.nextLink);
    if (skipToken === null) return;

    this.more.set(true);

    try {
      const page = await listResources(
        this.api,
        verbsFor(listing.form.title),
        { tenantId, subscriptionId: this.subscriptionId(), resourceGroup: this.resourceGroup(), parents: [] },
        { skipToken }
      );

      this.state.set({
        kind: 'ready',
        value: {
          form: listing.form,
          items: [...listing.items, ...page.value.value],
          ...(page.value.nextLink === undefined ? {} : { nextLink: page.value.nextLink })
        }
      });
    } finally {
      this.more.set(false);
    }
  }

  protected itemLink(item: ResourceEnvelope): string {
    return links.resource(
      {
        tenantId: this.tenantId() ?? '',
        subscriptionId: this.subscriptionId(),
        resourceGroup: this.resourceGroup(),
        parents: [],
        name: item.name
      },
      this.type() ?? ''
    );
  }

  protected tagsOf(item: ResourceEnvelope): string {
    return Object.entries(item.tags ?? {})
      .map(([k, v]) => `${k}=${v}`)
      .join(', ');
  }

  protected createLabel(form: ResourceForm): string {
    return $localize`:@@resources.create:Create ${form.title}:type:`;
  }

  protected emptyTitle(form: ResourceForm): string {
    return $localize`:@@resources.emptyTitle:No ${form.plural}:plural: here`;
  }
}

/** `$skipToken` out of a `nextLink`, or `null` when the link carries none. */
export function skipTokenOf(nextLink: string): string | null {
  try {
    return new URL(nextLink, 'https://placeholder.invalid').searchParams.get('$skipToken');
  } catch {
    return null;
  }
}

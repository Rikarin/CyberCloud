import { ChangeDetectionStrategy, Component, computed, effect, inject, input, signal, untracked } from '@angular/core';
import { RouterLink } from '@angular/router';
import { ScopeResource, apiVersion } from '@cybercloud/api';
import { ResourceForm, ResourceFormSource } from '@cybercloud/resource-forms';
import { BladeStackStore } from '@cybercloud/shell';
import { XuiButton } from '@xui/button';
import { XuiDescriptions, XuiDescriptionsItem } from '@xui/descriptions';
import { PlatformApi } from '../../app/api/platform-api';
import { parentCountOf } from '../../app/api/resource-verbs';
import { links } from '../../app/routes/portal-links';
import { NeedsTenant, PageStatus, activeTenantId, load, pageState } from '../shared/page-state';

/**
 * The resource group blade: the group itself, and the way into everything created in it.
 *
 * docs/plan/06 § The hierarchy calls the group "the lifecycle boundary — what a resource is
 * created in", so this is where creating starts: one link per top-level type into the create
 * blade, and one per type into the list. The types come from the form document, so a new
 * provider's type appears here the day its form is generated, with no change to this file.
 *
 * ⚠ Child types (a node pool, a subnet, a bucket) are not offered here: they are created under
 * a parent, and the parent blade is where that belongs. Offering them from the group would need
 * a parent name this blade has nowhere to ask for.
 */
@Component({
  selector: 'cc-resource-group-blade',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, XuiButton, XuiDescriptions, XuiDescriptionsItem, NeedsTenant, PageStatus],
  host: { class: 'block p-6' },
  template: `
    @if (tenantId() === null) {
      <cc-needs-tenant />
    } @else {
      <div class="flex flex-wrap items-start justify-between gap-3">
        <div>
          <h1 class="text-lg font-semibold">{{ resourceGroup() }}</h1>
          <p class="text-foreground-muted mt-1 text-sm">
            <ng-container i18n="@@resourceGroupBlade.subheading">Resource group in</ng-container>
            <a class="underline" [routerLink]="subscriptionLink()">{{ subscriptionId() }}</a>
          </p>
        </div>
        <div class="flex gap-2">
          <a xuiButton variant="outline" size="sm" [routerLink]="accessLink()" i18n="@@resourceGroupBlade.access"
            >Access</a
          >
          <a xuiButton variant="outline" size="sm" [routerLink]="listLink()" i18n="@@resourceGroupBlade.list"
            >Resources</a
          >
        </div>
      </div>

      <cc-page-status [state]="state()" [retryLink]="groupsLink()" />

      @if (scope(); as scope) {
        <xui-descriptions class="mt-4" [column]="2" bordered [title]="overviewTitle">
          <xui-descriptions-item [label]="labels.location">{{ scope.location ?? '—' }}</xui-descriptions-item>
          <xui-descriptions-item [label]="labels.type">{{ scope.type }}</xui-descriptions-item>
          <xui-descriptions-item [label]="labels.id" [span]="2"
            ><code class="text-xs break-all">{{ scope.id }}</code></xui-descriptions-item
          >
        </xui-descriptions>

        <h2 class="mt-6 text-sm font-semibold" i18n="@@resourceGroupBlade.create">Create in this group</h2>
        <ul class="mt-2 grid gap-1 sm:grid-cols-2 lg:grid-cols-3">
          @for (type of types(); track type.resourceType) {
            <li class="border-border flex items-center justify-between gap-2 rounded-md border px-3 py-2">
              <div class="min-w-0">
                <a
                  class="block truncate font-medium underline"
                  [routerLink]="listLink()"
                  [queryParams]="{ type: type.resourceType }"
                  >{{ type.plural }}</a
                >
                <span class="text-foreground-muted block truncate text-xs">{{ type.resourceType }}</span>
              </div>
              <a
                xuiButton
                size="sm"
                variant="ghost"
                [routerLink]="createLinkFor(type)"
                [attr.aria-label]="createLabel(type)"
                i18n="@@resourceGroupBlade.createOne"
              >
                Create
              </a>
            </li>
          }
        </ul>
      }
    }
  `
})
export class ResourceGroupBlade {
  readonly subscriptionId = input.required<string>();
  readonly resourceGroup = input.required<string>();

  private readonly api = inject(PlatformApi);
  private readonly forms = inject(ResourceFormSource);
  private readonly blades = inject(BladeStackStore);

  protected readonly tenantId = activeTenantId();
  protected readonly state = pageState<ScopeResource>();
  protected readonly scope = computed(() => {
    const state = this.state();
    return state.kind === 'ready' ? state.value : null;
  });
  protected readonly types = signal<readonly ResourceForm[]>([]);

  protected readonly subscriptionLink = computed(() => links.subscription(this.subscriptionId()));
  protected readonly groupsLink = computed(() => links.resourceGroups(this.subscriptionId()));
  protected readonly listLink = computed(() => links.resources(this.subscriptionId(), this.resourceGroup()));
  protected readonly accessLink = computed(() =>
    links.resourceGroupAccess(this.subscriptionId(), this.resourceGroup())
  );

  protected readonly overviewTitle = $localize`:@@resourceGroupBlade.overview:Overview`;
  protected readonly labels = {
    location: $localize`:@@resourceGroupBlade.location:Default region`,
    type: $localize`:@@resourceGroupBlade.type:Type`,
    id: $localize`:@@resourceGroupBlade.id:Id`
  };

  constructor() {
    void this.forms
      .types(apiVersion)
      .then(types => this.types.set(types.filter(t => parentCountOf(t.resourceType) === 0)));

    effect(() => {
      const tenantId = this.tenantId();
      const subscriptionId = this.subscriptionId();
      const resourceGroup = this.resourceGroup();

      untracked(() => {
        const route = links.resourceGroup(subscriptionId, resourceGroup);
        this.blades.open({ id: route, title: resourceGroup, route });

        if (tenantId === null) return;

        void load(
          this.state,
          async () => (await this.api.getResourceGroup(tenantId, subscriptionId, resourceGroup)).value,
          () => this.resourceGroup() !== resourceGroup || this.subscriptionId() !== subscriptionId
        );
      });
    });
  }

  protected createLinkFor(type: ResourceForm): string {
    return links.create(this.subscriptionId(), this.resourceGroup(), type.resourceType);
  }

  protected createLabel(type: ResourceForm): string {
    return $localize`:@@resourceGroupBlade.createLabel:Create ${type.title}:type:`;
  }
}

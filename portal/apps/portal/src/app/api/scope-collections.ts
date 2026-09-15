import { Injectable, inject } from '@angular/core';
import { ApiResponse, Page, PageRequest, ScopeResource } from '@cybercloud/api';
import { SubscriptionRef } from '@cybercloud/shell';
import { skipTokenOf } from './paging';
import { PlatformApi } from './platform-api';

/**
 * A subscription or a resource group as the collection renders it: the scope resource, with the
 * display name under `properties` where the item's `GET` carries it.
 */
export interface ScopeListItem extends ScopeResource {
  readonly properties?: { readonly displayName?: string };
}

/** The Azure-shaped type strings the two collections carry. */
export const SUBSCRIPTION_TYPE = 'CyberCloud.Resources/subscriptions';
export const RESOURCE_GROUP_TYPE = 'CyberCloud.Resources/subscriptions/resourceGroups';

/**
 * The two scope collections, as the pages and the sign-in flow call them.
 *
 * `GET /tenants/{tid}/subscriptions` and `GET /tenants/{tid}/subscriptions/{sid}/resourceGroups`
 * are the third scope grammar — `RouteKind.ScopeCollection` on the gateway, served by
 * `IScopeManager.ListAsync` as index → page → `ListObjects` read filter, in the same
 * `{ value, nextLink }` envelope and `$top`/`$skipToken` paging as every resource collection
 * (#37). They are emitted through `OpenApiEmitter.ScopePathItems`, so the generated client
 * carries them as `listSubscriptions` and `listResourceGroups`, and the two methods below are
 * delegations to those — the seam stays so that `allSubscriptions` and the two helpers have one
 * home, and so a page that lists a collection names the collection rather than the client.
 *
 * ⚠ **The next page is fetched by `$skipToken`, never by following `nextLink`**, for the reason
 * `resource-list.ts` gives: the link is a URL the server chose, and a URL the server chose must
 * not be put in front of the user's bearer token.
 */
@Injectable({ providedIn: 'root' })
export class ScopeCollections {
  private readonly api = inject(PlatformApi);

  listSubscriptions(tenantId: string, page: PageRequest = {}): Promise<ApiResponse<Page<ScopeListItem>>> {
    return this.api.listSubscriptions(tenantId, page);
  }

  listResourceGroups(
    tenantId: string,
    subscriptionId: string,
    page: PageRequest = {}
  ): Promise<ApiResponse<Page<ScopeListItem>>> {
    return this.api.listResourceGroups(tenantId, subscriptionId, page);
  }

  /**
   * Every subscription the token may read, across pages, as the context bar wants them. The
   * context bar lists them all — a switcher that showed page one of two hundred would hide the
   * one a person is looking for — and the cap is the same `MaxPageSize` the gateway clamps to.
   */
  async allSubscriptions(tenantId: string): Promise<SubscriptionRef[]> {
    const items: SubscriptionRef[] = [];
    let skipToken: string | undefined;

    do {
      const page = await this.listSubscriptions(tenantId, {
        top: 1000,
        ...(skipToken === undefined ? {} : { skipToken })
      });
      for (const item of page.value.value) {
        items.push({ id: lastSegmentOf(item), tenantId, displayName: displayNameOf(item) });
      }

      const next = page.value.nextLink === undefined ? null : skipTokenOf(page.value.nextLink);
      skipToken = next === null ? undefined : next;
    } while (skipToken !== undefined);

    return items;
  }
}

/**
 * What a person reads for a scope: the display name it declares, or its `name` when it declares
 * none — `ResponseBodies.Scope` renders a subscription's display name as `name` today, and the
 * collection's `properties.displayName` wins where it is present.
 */
export function displayNameOf(item: ScopeListItem): string {
  const declared = item.properties?.displayName;
  return declared !== undefined && declared.length > 0 ? declared : item.name;
}

/**
 * The scope's own id: the last segment of its path. ⚠ Not `name`, which for a subscription is
 * the display name — docs/plan/06 § Identifiers puts the id in the address and nowhere else.
 */
export function lastSegmentOf(item: ScopeListItem): string {
  const segments = item.id.split('/').filter(segment => segment.length > 0);
  const last = segments.at(-1);
  return last === undefined ? item.name : decodeURIComponent(last);
}

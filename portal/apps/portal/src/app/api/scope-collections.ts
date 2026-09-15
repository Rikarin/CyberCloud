import { Injectable, inject } from '@angular/core';
import { ApiResponse, Page, PageRequest, ScopeResource } from '@cybercloud/api';
import { SubscriptionRef } from '@cybercloud/shell';
import { HttpApiTransport } from './http-transport';
import { skipTokenOf } from './paging';

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
 * The two scope collections, by hand, until the generated client learns them.
 *
 * `GET /tenants/{tid}/subscriptions` and `GET /tenants/{tid}/subscriptions/{sid}/resourceGroups`
 * are the third scope grammar — `RouteKind.ScopeCollection` on the gateway, served by
 * `IScopeManager.ListAsync` as index → page → `ListObjects` read filter, in the same
 * `{ value, nextLink }` envelope and `$top`/`$skipToken` paging as every resource collection
 * (#37). They are emitted through `OpenApiEmitter.ScopePathItems`, so `./build.sh Generate`
 * regenerates `libs/api` with them — and until that regeneration lands beside this branch, this
 * is the seam the pages and the sign-in flow call, built over the same `HttpApiTransport` as the
 * generated client so the token, the `api-version` and the error mapping are owned once.
 *
 * ⚠ **Coded to the contract's shape, not to a running gateway.** The address, the envelope and
 * the `$skipToken` continuation are the contract's § 6; `pages.spec.ts` plays the gateway. The
 * day `libs/api` carries `listSubscriptions`/`listResourceGroups`, the two methods below become
 * delegations and nothing that calls them moves — the same arrangement `role-assignments.ts` has
 * with the address the emitters cannot see.
 *
 * ⚠ **The next page is fetched by `$skipToken`, never by following `nextLink`**, for the reason
 * `resource-list.ts` gives: the link is a URL the server chose, and a URL the server chose must
 * not be put in front of the user's bearer token.
 */
@Injectable({ providedIn: 'root' })
export class ScopeCollections {
  private readonly transport = inject(HttpApiTransport);

  listSubscriptions(tenantId: string, page: PageRequest = {}): Promise<ApiResponse<Page<ScopeListItem>>> {
    return this.transport.send<Page<ScopeListItem>>({
      method: 'GET',
      path: `/tenants/${encodeURIComponent(tenantId)}/subscriptions`,
      query: pageQuery(page)
    });
  }

  listResourceGroups(
    tenantId: string,
    subscriptionId: string,
    page: PageRequest = {}
  ): Promise<ApiResponse<Page<ScopeListItem>>> {
    return this.transport.send<Page<ScopeListItem>>({
      method: 'GET',
      path: `/tenants/${encodeURIComponent(tenantId)}/subscriptions/${encodeURIComponent(subscriptionId)}/resourceGroups`,
      query: pageQuery(page)
    });
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

/** `$top`/`$skipToken`, spelled as `CyberCloudApi.pageQuery` spells them — the query the gateway parses. */
function pageQuery(page: PageRequest): Record<string, string> {
  const query: Record<string, string> = {};
  if (page.top !== undefined) query['$top'] = String(page.top);
  if (page.skipToken !== undefined) query['$skipToken'] = page.skipToken;
  return query;
}

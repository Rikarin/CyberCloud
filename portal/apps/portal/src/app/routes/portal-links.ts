import { ResourceAddress } from '../api/resource-verbs';

/**
 * The portal's URLs, built in one place.
 *
 * docs/plan/20 § Information architecture: "Blades — Stacked, deep-linkable panels." Deep-linkable
 * means the address is in the URL, so the route mirrors docs/plan/06 § Identifiers rather than
 * hiding the subscription in a store — a link from an alert email has to open the right resource
 * in the right subscription with nothing but the URL to go on.
 *
 * ⚠ The tenant is deliberately not in the URL. A request's tenant comes from its token
 * (`ResolveTenantStage` refuses any path that disagrees), so a tenant segment would be a value
 * the portal could only ever echo, and one that made a pasted link claim a tenant the reader is
 * not in. It stays in `TenantContextStore`.
 *
 * Every segment is encoded here and decoded by the router; a name with a `/` in it cannot forge a
 * different route. The same rule `CyberCloudApi.segment` applies on the API side.
 */
const seg = encodeURIComponent;

/** `{provider, typePath}` from `CyberCloud.Sample/widgets` — the route splits the type where the API does. */
export function splitType(resourceType: string): { provider: string; segments: string[] } {
  const [provider, ...segments] = resourceType.split('/');
  return { provider, segments };
}

export function joinType(provider: string, segments: readonly string[]): string {
  return [provider, ...segments].join('/');
}

export const links = {
  subscriptions: () => '/subscriptions',
  subscription: (subscriptionId: string) => `/subscriptions/${seg(subscriptionId)}`,
  resourceGroups: (subscriptionId: string) => `${links.subscription(subscriptionId)}/resourceGroups`,
  resourceGroup: (subscriptionId: string, resourceGroup: string) =>
    `${links.resourceGroups(subscriptionId)}/${seg(resourceGroup)}`,

  /**
   * The list page. The type goes in `?type=` — bind it through `[queryParams]`, never into the
   * path string, because `routerLink` does not parse a query string out of a command.
   */
  resources: (subscriptionId: string, resourceGroup: string) =>
    `${links.resourceGroup(subscriptionId, resourceGroup)}/resources`,

  /**
   * The create blade. A child type's parent names are typed into the form, not carried here — the
   * blade knows how many it needs from the type path.
   */
  create: (subscriptionId: string, resourceGroup: string, resourceType: string) => {
    const { provider, segments } = splitType(resourceType);
    return `${links.resourceGroup(subscriptionId, resourceGroup)}/create/${seg(provider)}/${segments.map(seg).join('/')}`;
  },

  /** The resource blade: `…/providers/{ns}/{type}/{name}`, with parent names interleaved for a child. */
  resource: (address: ResourceAddress, resourceType: string) => {
    const { provider, segments } = splitType(resourceType);
    const path: string[] = [];

    segments.forEach((segment, i) => {
      path.push(seg(segment));
      const name = i < segments.length - 1 ? address.parents[i] : address.name;
      path.push(seg(name ?? ''));
    });

    return `${links.resourceGroup(address.subscriptionId, address.resourceGroup)}/providers/${seg(provider)}/${path.join('/')}`;
  },

  edit: (address: ResourceAddress, resourceType: string) => `${links.resource(address, resourceType)}/edit`,

  /**
   * The access page — the "Access (ReBAC)" rail item of docs/plan/20 § Information architecture —
   * on each of the three scopes the portal has a blade for. `/access` is a literal suffix like
   * `/edit`, so a resource named `access` is still `…/{type}/access` and its access page is
   * `…/{type}/access/access`.
   */
  subscriptionAccess: (subscriptionId: string) => `${links.subscription(subscriptionId)}/access`,
  resourceGroupAccess: (subscriptionId: string, resourceGroup: string) =>
    `${links.resourceGroup(subscriptionId, resourceGroup)}/access`,
  resourceAccess: (address: ResourceAddress, resourceType: string) => `${links.resource(address, resourceType)}/access`,

  /**
   * The cloud shell for a resource group — docs/plan/20 § The pages that are not generated. A
   * console to open goes in `?console=`, the way the list page carries its type; absent, the page
   * opens the group's first.
   */
  terminal: (subscriptionId: string, resourceGroup: string, console?: string) =>
    `${links.resourceGroup(subscriptionId, resourceGroup)}/terminal` +
    (console === undefined ? '' : `?console=${seg(console)}`),

  /**
   * Cost analysis on a subscription, or on one of its groups — docs/plan/20 § The pages that are not
   * generated. The period and the grouping ride in the query (`?period=`, `?groupBy=`), so a link
   * can name them; absent, the page shows this month by service.
   */
  cost: (subscriptionId: string, resourceGroup?: string) =>
    `${resourceGroup === undefined ? links.subscription(subscriptionId) : links.resourceGroup(subscriptionId, resourceGroup)}/cost`,

  /** The tenant's invoices, and one by its number. */
  invoices: () => '/invoices',
  invoice: (number: string) => `/invoices/${seg(number)}`,

  /** The operation view, and where to go once it succeeds. */
  operation: (operationId: string, then?: string) =>
    `/operations/${seg(operationId)}` + (then === undefined ? '' : `?then=${seg(then)}`)
};

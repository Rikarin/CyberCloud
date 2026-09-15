import { ApiResponse, CyberCloudApi, Page, PageRequest, Resource } from '@cybercloud/api';

/**
 * Where a resource lives, as the pieces a route carries.
 *
 * docs/plan/06 § Identifiers: `/tenants/{t}/subscriptions/{s}/resourceGroups/{g}/providers/{ns}/
 * {type}/{name}`, with one extra name per parent for a child type — a node pool is
 * `…/managedClusters/{cluster}/agentPools/{pool}`. `parents` holds those in path order and is
 * empty for a top-level type.
 */
export interface ResourceAddress {
  readonly tenantId: string;
  readonly subscriptionId: string;
  readonly resourceGroup: string;
  readonly parents: readonly string[];
  readonly name: string;
}

/**
 * A resource as the gateway renders one, for a page that does not know the type: the generated
 * `Resource` envelope — `id`, `name`, `type`, `provisioningState`, `etag`, typed from the document
 * since issue #85 — plus the body members every type declares, untyped because the type is only
 * known at run time.
 */
export interface ResourceEnvelope extends Resource {
  readonly location?: string;
  readonly properties?: unknown;
  readonly tags?: Readonly<Record<string, string>>;
}

/**
 * The generated client's method names for one resource type, derived rather than tabulated.
 *
 * `TypeScriptEmitter` names every verb from the type's display name — `Pascal(type.DisplayName)`,
 * so `CyberCloud.Sample/widgets` ("Widget") is `createOrUpdateWidget` — and `FormsEmitter` writes
 * that same display name into the form as `title`. Both read the one document, so deriving the
 * method from the title is reading the convention the generator already has, and
 * `resource-verbs.spec.ts` pins it: for every form in `generated/forms/{version}.json` the four
 * methods must exist on `CyberCloudApi`, so a title the two emitters spell differently fails a
 * test rather than a click.
 *
 * ⚠ **Why not a hand-written table.** A table keyed by resource type is a third copy of what the
 * document says, maintained by hand next to two that are generated, and it is the copy nobody
 * regenerates. The 23 rows it would hold today would be 100 by M2.
 */
export interface ResourceVerbs {
  readonly get: string;
  readonly createOrUpdate: string;
  readonly update: string;
  readonly delete: string;
  readonly list: string;
}

/** `SdkEmitter.Pascal`, in TypeScript: letters and digits kept, every other character a word break. */
export function pascal(value: string): string {
  let built = '';
  let upper = true;

  for (const current of value) {
    if (!/[\p{L}\p{N}]/u.test(current)) {
      upper = true;
      continue;
    }

    built += upper ? current.toUpperCase() : current;
    upper = false;
  }

  if (built.length === 0) return 'Value';

  return /[0-9]/.test(built[0]) ? 'N' + built : built;
}

export function verbsFor(title: string): ResourceVerbs {
  const verb = pascal(title);

  return {
    get: 'get' + verb,
    createOrUpdate: 'createOrUpdate' + verb,
    update: 'update' + verb,
    delete: 'delete' + verb,
    list: 'list' + verb
  };
}

/** How many parent names a type path needs: `ns/type` has none, `ns/type/child` has one. */
export function parentCountOf(resourceType: string): number {
  return Math.max(0, resourceType.split('/').length - 2);
}

type Method = (...args: unknown[]) => Promise<ApiResponse<unknown>>;

function method(api: CyberCloudApi, name: string): Method {
  const candidate = (api as unknown as Record<string, unknown>)[name];

  if (typeof candidate !== 'function') {
    throw new Error(
      `The generated client has no '${name}'. The form schema and the client disagree about this ` +
        'type — regenerate both with ./build.sh Generate before shipping a portal that cannot call it.'
    );
  }

  return (candidate as Method).bind(api);
}

function segments(address: ResourceAddress): string[] {
  return [address.tenantId, address.subscriptionId, address.resourceGroup, ...address.parents];
}

/** `GET` one resource. */
export function readResource(api: CyberCloudApi, verbs: ResourceVerbs, address: ResourceAddress) {
  return method(api, verbs.get)(...segments(address), address.name) as Promise<ApiResponse<ResourceEnvelope>>;
}

/** `PUT` one resource. ⚠ Long-running: the response is a 202 carrying `operationUrl`. */
export function putResource(api: CyberCloudApi, verbs: ResourceVerbs, address: ResourceAddress, body: unknown) {
  return method(api, verbs.createOrUpdate)(...segments(address), address.name, body) as Promise<
    ApiResponse<ResourceEnvelope>
  >;
}

/** `DELETE` one resource. ⚠ Long-running, and permanent for a type with no soft-delete window. */
export function deleteResource(api: CyberCloudApi, verbs: ResourceVerbs, address: ResourceAddress) {
  return method(api, verbs.delete)(...segments(address), address.name) as Promise<ApiResponse<void>>;
}

/** One page of a type's resources in a resource group. ⚠ A short page never means "that is all". */
export function listResources(
  api: CyberCloudApi,
  verbs: ResourceVerbs,
  scope: Omit<ResourceAddress, 'name'>,
  page: PageRequest = {}
) {
  return method(api, verbs.list)(...segments({ ...scope, name: '' }), page) as Promise<
    ApiResponse<Page<ResourceEnvelope>>
  >;
}

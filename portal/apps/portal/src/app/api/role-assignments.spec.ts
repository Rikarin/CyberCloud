import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { apiVersion } from '@cybercloud/api';
import { ApiCallError } from './http-transport';
import {
  AccessScope,
  ROLE_ASSIGNMENTS_SUFFIX,
  RoleAssignmentName,
  RoleAssignmentsApi,
  assignmentPath,
  parseName,
  principalTypes,
  renderName,
  roles,
  scopePath
} from './role-assignments';

/**
 * The one hand-written client, pinned where the generated one would have pinned itself: the
 * address for every scope kind, the derived name, and the three verbs' statuses.
 *
 * The scope paths are docs/plan/06 § Identifiers and the suffix is `RoleAssignmentId.Suffix`;
 * `RoleAssignmentRoutingTests` on the gateway side accepts exactly these shapes, so a path this
 * spec pins is a path that routes.
 */
const TENANT = 't-acme';
const SUBSCRIPTION = '0f9a1c2e-4b7d-4e3a-9c1d-2b6f8a7e5d43';
const GROUP = 'example-rg';
const RITA = '7f3c2a1e0b4d4f6a8c9d1e2f3a4b5c6d';

const subscription: AccessScope = { kind: 'subscription', tenantId: TENANT, subscriptionId: SUBSCRIPTION };
const group: AccessScope = {
  kind: 'resourceGroup',
  tenantId: TENANT,
  subscriptionId: SUBSCRIPTION,
  resourceGroup: GROUP
};
const widget: AccessScope = {
  kind: 'resource',
  resourceType: 'CyberCloud.Sample/widgets',
  address: { tenantId: TENANT, subscriptionId: SUBSCRIPTION, resourceGroup: GROUP, parents: [], name: 'w1' }
};
const pool: AccessScope = {
  kind: 'resource',
  resourceType: 'CyberCloud.ContainerService/managedClusters/agentPools',
  address: { tenantId: TENANT, subscriptionId: SUBSCRIPTION, resourceGroup: GROUP, parents: ['c1'], name: 'p1' }
};
const reader: RoleAssignmentName = { role: 'reader', principalType: 'user', principalId: RITA };

describe('the role-assignment address', () => {
  it('is the scope path, the reserved suffix, and the derived name — on every scope kind', () => {
    const base = `/tenants/${TENANT}/subscriptions/${SUBSCRIPTION}`;

    expect(scopePath({ kind: 'tenant', tenantId: TENANT })).toBe(`/tenants/${TENANT}`);
    expect(scopePath(subscription)).toBe(base);
    expect(scopePath(group)).toBe(`${base}/resourceGroups/${GROUP}`);
    expect(scopePath(widget)).toBe(`${base}/resourceGroups/${GROUP}/providers/CyberCloud.Sample/widgets/w1`);
    // A child type interleaves the parent's name with the type path, as `links.resource` does.
    expect(scopePath(pool)).toBe(
      `${base}/resourceGroups/${GROUP}/providers/CyberCloud.ContainerService/managedClusters/c1/agentPools/p1`
    );

    expect(ROLE_ASSIGNMENTS_SUFFIX).toBe('/providers/CyberCloud.Authorization/roleAssignments/');
    expect(assignmentPath(group, reader)).toBe(
      `${base}/resourceGroups/${GROUP}/providers/CyberCloud.Authorization/roleAssignments/reader-user-${RITA}`
    );
  });

  it('encodes every segment, so a name with a slash cannot reach a different scope', () => {
    const odd: AccessScope = {
      kind: 'resourceGroup',
      tenantId: TENANT,
      subscriptionId: SUBSCRIPTION,
      resourceGroup: 'a/b'
    };
    expect(scopePath(odd)).toBe(`/tenants/${TENANT}/subscriptions/${SUBSCRIPTION}/resourceGroups/a%2Fb`);
  });
});

describe('the derived name — RoleAssignmentName, in TypeScript', () => {
  it('renders `{role}-{principalType}-{principalId}` and parses it back, for every role and type', () => {
    for (const role of roles) {
      for (const principalType of principalTypes) {
        const name: RoleAssignmentName = { role, principalType, principalId: RITA };
        expect(renderName(name)).toBe(`${role}-${principalType}-${RITA}`);
        expect(parseName(renderName(name))).toEqual(name);
      }
    }
  });

  it('splits at the first two hyphens, never the last: the id may contain `-`', () => {
    expect(parseName('contributor-group-eng-platform')).toEqual({
      role: 'contributor',
      principalType: 'group',
      principalId: 'eng-platform'
    });
  });

  it('refuses what the page cannot offer — a fourth role, a spelling the store would not match, a bad id', () => {
    expect(parseName('purger-user-' + RITA)).toBeNull();
    expect(parseName('reader-ServicePrincipal-' + RITA)).toBeNull();
    expect(parseName('reader-user-')).toBeNull();
    expect(parseName('reader-user--x')).toBeNull();
    expect(parseName('reader-user-Rita')).toBeNull();
    expect(parseName('reader-user-' + 'a'.repeat(64))).toBeNull();
    expect(parseName('reader')).toBeNull();
    expect(parseName('')).toBeNull();
  });
});

describe('RoleAssignmentsApi — the three verbs, through the portal transport', () => {
  let api: RoleAssignmentsApi;
  let http: HttpTestingController;

  const PATH = `/api${assignmentPath(group, reader)}`;
  const served = {
    id: assignmentPath(group, reader),
    name: renderName(reader),
    type: 'CyberCloud.Authorization/roleAssignments',
    properties: { scope: scopePath(group), principalId: RITA, principalType: 'user', roleDefinitionId: 'reader' }
  };

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting()]
    });

    api = TestBed.inject(RoleAssignmentsApi);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  it('PUTs the three properties the address already spells, with the api-version, and reports 201 on a grant', async () => {
    const pending = api.assign(group, reader);

    const request = http.expectOne(r => r.method === 'PUT' && r.url === PATH);
    expect(request.request.params.get('api-version')).toBe(apiVersion);
    expect(request.request.body).toEqual({ principalId: RITA, principalType: 'user', roleDefinitionId: 'reader' });
    request.flush(served, { status: 201, statusText: 'Created' });

    const response = await pending;
    expect(response.status).toBe(201);
    expect(response.value).toEqual(served);
    // ⚠ Never a 202: there is no operation to poll after a tuple write.
    expect(response.operationUrl).toBeUndefined();
  });

  it('reports 200 on a repeated grant — the same name is the same tuple', async () => {
    const pending = api.assign(group, reader);
    http.expectOne(r => r.method === 'PUT' && r.url === PATH).flush(served, { status: 200, statusText: 'OK' });

    expect((await pending).status).toBe(200);
  });

  it('GETs one assignment, and surfaces a 404 as ApiCallError rather than an empty value', async () => {
    const read = api.read(group, reader);
    http.expectOne(r => r.method === 'GET' && r.url === PATH).flush(served);
    expect((await read).value.properties.roleDefinitionId).toBe('reader');

    const missing = api.read(group, reader);
    http
      .expectOne(r => r.method === 'GET' && r.url === PATH)
      .flush(
        { error: { code: 'ResourceNotFound', message: `'${assignmentPath(group, reader)}' does not exist.` } },
        { status: 404, statusText: 'Not Found' }
      );

    await expect(missing).rejects.toBeInstanceOf(ApiCallError);
    await expect(missing).rejects.toMatchObject({ status: 404, error: { code: 'ResourceNotFound' } });
  });

  it('DELETEs with no body and reports the 204', async () => {
    const pending = api.revoke(widget, reader);

    const request = http.expectOne(r => r.method === 'DELETE' && r.url === `/api${assignmentPath(widget, reader)}`);
    expect(request.request.body).toBeNull();
    request.flush(null, { status: 204, statusText: 'No Content' });

    expect((await pending).status).toBe(204);
  });
});

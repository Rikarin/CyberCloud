import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { apiVersion } from '@cybercloud/api';
import { ApiCallError } from './http-transport';
import { IdentityAdminApi, identityPath, registrableScopes } from './identity-admin';

/**
 * The identity administration client, pinned where a generated one would pin itself: the address
 * of every call, its verb, its body, and the api-version the transport adds.
 *
 * The paths are `IdentityAddress` on the platform — `IdentityRoutingTests` on the gateway side
 * accepts exactly these shapes and refuses every other one under the namespace, so a path this
 * spec pins is a path that routes.
 */
const TENANT = 't-acme';
const ID = '0a1b2c3d4e5f40718293a4b5c6d7e8f9';
const BASE = `/api/tenants/${TENANT}/providers/CyberCloud.Identity`;

describe('the identity address', () => {
  it('is the tenant, the reserved namespace, the collection, the id, and the verb', () => {
    expect(identityPath(TENANT, 'members')).toBe(`/tenants/${TENANT}/providers/CyberCloud.Identity/members`);
    expect(identityPath(TENANT, 'invitations', ID, 'resend')).toBe(
      `/tenants/${TENANT}/providers/CyberCloud.Identity/invitations/${ID}/resend`
    );
    expect(identityPath(TENANT, 'applications', ID, 'rotateSecret')).toBe(
      `/tenants/${TENANT}/providers/CyberCloud.Identity/applications/${ID}/rotateSecret`
    );
    // Encoded like every segment the portal builds: a tenant id with a slash can't forge a path.
    expect(identityPath('a/b', 'sessions')).toBe('/tenants/a%2Fb/providers/CyberCloud.Identity/sessions');
  });

  it('offers exactly the four scopes the identity host registers', () => {
    expect([...registrableScopes]).toEqual(['openid', 'profile', 'offline_access', 'cyc.api']);
  });
});

describe('IdentityAdminApi', () => {
  let api: IdentityAdminApi;
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting()]
    });
    api = TestBed.inject(IdentityAdminApi);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  /** Each call, the request it must make, and what the platform answers it with. */
  const calls: readonly {
    name: string;
    call: (api: IdentityAdminApi) => Promise<unknown>;
    method: string;
    url: string;
    body?: unknown;
    status: number;
  }[] = [
    { name: 'listMembers', call: a => a.listMembers(TENANT), method: 'GET', url: `${BASE}/members`, status: 200 },
    {
      name: 'removeMember',
      call: a => a.removeMember(TENANT, ID),
      method: 'DELETE',
      url: `${BASE}/members/${ID}`,
      status: 200
    },
    {
      name: 'listInvitations',
      call: a => a.listInvitations(TENANT),
      method: 'GET',
      url: `${BASE}/invitations`,
      status: 200
    },
    {
      name: 'invite',
      call: a => a.invite(TENANT, 'colleague@acme.example'),
      method: 'POST',
      url: `${BASE}/invitations`,
      body: { email: 'colleague@acme.example' },
      status: 201
    },
    {
      name: 'resendInvitation',
      call: a => a.resendInvitation(TENANT, ID),
      method: 'POST',
      url: `${BASE}/invitations/${ID}/resend`,
      status: 200
    },
    {
      name: 'revokeInvitation',
      call: a => a.revokeInvitation(TENANT, ID),
      method: 'DELETE',
      url: `${BASE}/invitations/${ID}`,
      status: 200
    },
    {
      name: 'listApplications',
      call: a => a.listApplications(TENANT),
      method: 'GET',
      url: `${BASE}/applications`,
      status: 200
    },
    {
      name: 'createApplication',
      call: a =>
        a.createApplication(TENANT, {
          displayName: 'Acme',
          redirectUris: ['https://acme.example/cb'],
          scopes: ['openid'],
          publicClient: false
        }),
      method: 'POST',
      url: `${BASE}/applications`,
      body: { displayName: 'Acme', redirectUris: ['https://acme.example/cb'], scopes: ['openid'], publicClient: false },
      status: 201
    },
    {
      name: 'rotateApplicationSecret',
      call: a => a.rotateApplicationSecret(TENANT, ID),
      method: 'POST',
      url: `${BASE}/applications/${ID}/rotateSecret`,
      status: 200
    },
    {
      name: 'deleteApplication',
      call: a => a.deleteApplication(TENANT, ID),
      method: 'DELETE',
      url: `${BASE}/applications/${ID}`,
      status: 204
    },
    { name: 'listSessions', call: a => a.listSessions(TENANT), method: 'GET', url: `${BASE}/sessions`, status: 200 },
    {
      name: 'revokeSession',
      call: a => a.revokeSession(TENANT, ID),
      method: 'DELETE',
      url: `${BASE}/sessions/${ID}`,
      status: 204
    }
  ];

  it.each(calls.map(c => [c.name, c] as const))('%s sends its one request', async (_, c) => {
    const pending = c.call(api);
    const request = http.expectOne(r => r.url === c.url);

    expect(request.request.method).toBe(c.method);
    expect(request.request.params.get('api-version')).toBe(apiVersion);
    expect(request.request.body ?? undefined).toEqual(c.body);

    request.flush(c.status === 204 ? null : { value: [] }, { status: c.status, statusText: 'OK' });
    await expect(pending).resolves.toMatchObject({ status: c.status });
  });

  it("hands the platform's refusal back as the one error shape", async () => {
    const pending = api.listMembers(TENANT);

    http
      .expectOne(`${BASE}/members?api-version=${apiVersion}`)
      .flush(
        { error: { code: 'AuthorizationFailed', message: 'Not an owner.' } },
        { status: 403, statusText: 'Forbidden' }
      );

    const failure = await pending.catch((e: unknown) => e);
    expect(failure).toBeInstanceOf(ApiCallError);
    expect((failure as ApiCallError).status).toBe(403);
    expect((failure as ApiCallError).error.code).toBe('AuthorizationFailed');
  });
});

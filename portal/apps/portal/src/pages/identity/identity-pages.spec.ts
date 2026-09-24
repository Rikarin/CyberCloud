import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, TestRequest, provideHttpClientTesting } from '@angular/common/http/testing';
import { Component, provideZonelessChangeDetection } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Router, RouterOutlet, provideRouter, withComponentInputBinding } from '@angular/router';
import { AccessTokenStore, TenantContextStore } from '@cybercloud/shell';
import axe from 'axe-core';
import { appRoutes } from '../../app/app.routes';

/**
 * The identity pages (#41), driven against a recorded platform — `pages.spec.ts`'s arrangement:
 * a tenant in `TenantContextStore`, `HttpTestingController` playing the gateway, and every
 * request asserted by method, path and body, because the identity client is hand-written and a
 * page that sends the right verb to the wrong address is the failure nothing generated catches.
 */
const TENANT = 't-acme';
const BASE = `/api/tenants/${TENANT}/providers/CyberCloud.Identity`;
const OWNER = '0a1b2c3d4e5f40718293a4b5c6d7e8f9';
const RITA = '7f3c2a1e0b4d4f6a8c9d1e2f3a4b5c6d';
const INVITE = '11112222333344445555666677778888';
const APP = 'aaaabbbbccccddddeeeeffff00001111';
const LAPTOP = '9999888877776666555544443333aaaa';
const PHONE = '9999888877776666555544443333bbbb';

const WCAG_22_AA = {
  runOnly: { type: 'tag' as const, values: ['wcag2a', 'wcag2aa', 'wcag21aa', 'wcag22aa'] },
  rules: { 'color-contrast': { enabled: false } }
};

@Component({
  selector: 'cc-identity-host',
  imports: [RouterOutlet],
  template: '<main><router-outlet /></main>'
})
class Host {}

function member(id: string, email: string, status: string, displayName = ''): object {
  return {
    id: `/tenants/${TENANT}/providers/CyberCloud.Identity/members/${id}`,
    name: id,
    type: 'CyberCloud.Identity/members',
    properties: { email, displayName, status, createdAt: '2026-09-01T10:00:00Z' }
  };
}

function invitation(status: string): object {
  return {
    id: `/tenants/${TENANT}/providers/CyberCloud.Identity/invitations/${INVITE}`,
    name: INVITE,
    type: 'CyberCloud.Identity/invitations',
    properties: {
      email: 'rita@acme.example',
      userId: RITA,
      status,
      expiresAt: '2026-10-01T10:00:00Z',
      sentAt: '2026-09-24T10:00:00Z',
      sendings: 1
    }
  };
}

function application(clientSecret?: string): object {
  return {
    id: `/tenants/${TENANT}/providers/CyberCloud.Identity/applications/${APP}`,
    name: APP,
    type: 'CyberCloud.Identity/applications',
    properties: {
      clientId: '4f1e2d3c-0000-4000-8000-000000000001',
      displayName: 'Acme server',
      redirectUris: ['https://acme.example/cb'],
      scopes: ['openid', 'profile'],
      publicClient: false,
      createdAt: '2026-09-24T10:00:00Z',
      clientSecretIssuedAt: '2026-09-24T10:00:00Z',
      ...(clientSecret === undefined ? {} : { clientSecret })
    }
  };
}

function session(id: string, device: string, current: boolean): object {
  return {
    id: `/tenants/${TENANT}/providers/CyberCloud.Identity/sessions/${id}`,
    name: id,
    type: 'CyberCloud.Identity/sessions',
    properties: {
      clientId: 'cyc-portal',
      deviceLabel: device,
      createdAt: '2026-09-24T09:00:00Z',
      lastUsedAt: '2026-09-24T10:00:00Z',
      methods: ['password', 'emailOtp'],
      current
    }
  };
}

describe('the identity pages, signed in', () => {
  let fixture: ComponentFixture<Host>;
  let router: Router;
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        provideRouter(appRoutes, withComponentInputBinding()),
        provideHttpClient(),
        provideHttpClientTesting()
      ]
    });

    router = TestBed.inject(Router);
    http = TestBed.inject(HttpTestingController);
    const context = TestBed.inject(TenantContextStore);
    context.load([{ id: TENANT, displayName: 'Acme' }], []);
    context.selectTenant(TENANT);
    TestBed.inject(AccessTokenStore).set('signed-in', Date.now() + 600_000);

    fixture = TestBed.createComponent(Host);
  });

  afterEach(() => http.verify());

  const host = (): HTMLElement => fixture.nativeElement as HTMLElement;

  async function settle(): Promise<void> {
    await new Promise(resolve => setTimeout(resolve, 0));
    await fixture.whenStable();
  }

  async function open(url: string): Promise<void> {
    await router.navigateByUrl(url);
    await settle();
  }

  function expectOne(method: string, path: string): TestRequest {
    const request = http.expectOne(r => r.method === method && r.url === BASE + path);
    return request;
  }

  async function answer(method: string, path: string, body: object | null, status = 200): Promise<TestRequest> {
    const request = expectOne(method, path);
    request.flush(body, { status, statusText: 'OK' });
    await settle();
    return request;
  }

  function click(text: string, within: ParentNode = host()): void {
    const button = [...within.querySelectorAll<HTMLElement>('button, a')].find(b => b.textContent?.trim() === text);
    if (button === undefined) throw new Error(`no button "${text}"`);
    button.click();
  }

  function row(attribute: string, id: string): HTMLElement {
    const found = host().querySelector<HTMLElement>(`[${attribute}="${id}"]`);
    if (found === null) throw new Error(`no row [${attribute}="${id}"]`);
    return found;
  }

  async function axeClean(): Promise<void> {
    const results = await axe.run(host(), WCAG_22_AA);
    expect(results.violations.map(v => `${v.id}: ${v.help} ${v.nodes.map(n => n.html).join(' | ')}`)).toEqual([]);
  }

  describe('members', () => {
    async function openMembers(invitations: object[] = [invitation('pending')]): Promise<void> {
      await open('/identity/members');
      await answer('GET', '/members', {
        value: [
          member(OWNER, 'owner@acme.example', 'active', 'The Owner'),
          member(RITA, 'rita@acme.example', 'invited')
        ]
      });
      await answer('GET', '/invitations', { value: invitations });
    }

    it('lists the members and the pending invitations, and passes the accessibility gate', async () => {
      await openMembers([invitation('pending'), { ...(invitation('accepted') as object), name: 'accepted-one' }]);

      expect(row('data-member', OWNER).textContent).toContain('The Owner');
      expect(row('data-member', RITA).textContent).toContain('invited');
      expect(row('data-invitation', INVITE).textContent).toContain('rita@acme.example');
      // Accepted invitations are members now, not pending.
      expect(host().querySelector('[data-invitation="accepted-one"]')).toBeNull();

      await axeClean();
    });

    it('invites by POST with the address and nothing else, then reads both lists again', async () => {
      await openMembers();

      const input = host().querySelector<HTMLInputElement>('#cc-invite-email')!;
      input.value = 'new@acme.example';
      input.dispatchEvent(new Event('input'));
      host().querySelector('form')!.dispatchEvent(new Event('submit'));
      await settle();

      const post = await answer('POST', '/invitations', invitation('pending'), 201);
      expect(post.request.body).toEqual({ email: 'new@acme.example' });

      await answer('GET', '/members', { value: [] });
      await answer('GET', '/invitations', { value: [] });

      expect(host().querySelector('[data-outcome="invited"]')?.textContent).toContain('new@acme.example');
    });

    it('resends in one click and revokes only after asking', async () => {
      await openMembers();

      click('Resend', row('data-invitation', INVITE));
      await settle();
      await answer('POST', `/invitations/${INVITE}/resend`, invitation('pending'));
      await answer('GET', '/members', { value: [] });
      await answer('GET', '/invitations', { value: [invitation('pending')] });
      expect(host().querySelector('[data-outcome="resent"]')).not.toBeNull();

      click('Revoke', row('data-invitation', INVITE));
      await settle();
      http.expectNone(r => r.method === 'DELETE');

      click('Withdraw it', row('data-invitation', INVITE));
      await settle();
      await answer('DELETE', `/invitations/${INVITE}`, invitation('revoked'));
      await answer('GET', '/members', { value: [] });
      await answer('GET', '/invitations', { value: [invitation('revoked')] });

      expect(host().querySelector('[data-outcome="revoked"]')).not.toBeNull();
      expect(host().querySelector(`[data-invitation="${INVITE}"]`)).toBeNull();
    });

    it("removes a member after asking, and shows the platform's refusal to remove yourself", async () => {
      await openMembers();

      click('Remove…', row('data-member', OWNER));
      await settle();
      click('Remove', row('data-member', OWNER));
      await settle();

      await answer(
        'DELETE',
        `/members/${OWNER}`,
        { error: { code: 'Conflict', message: "You can't remove yourself from the organisation." } },
        409
      );
      await answer('GET', '/members', { value: [member(OWNER, 'owner@acme.example', 'active')] });
      await answer('GET', '/invitations', { value: [] });

      expect(host().querySelector('[data-outcome="failed"]')?.textContent).toContain("can't remove yourself");
    });

    it('renders a 403 for a member who is not an owner, the way every page renders one', async () => {
      await open('/identity/members');
      await answer('GET', '/members', { error: { code: 'AuthorizationFailed', message: 'Not an owner.' } }, 403);
      await answer('GET', '/invitations', { error: { code: 'AuthorizationFailed', message: 'Not an owner.' } }, 403);

      expect(host().textContent).toContain('Not allowed');
    });
  });

  describe('applications', () => {
    async function openApplications(): Promise<void> {
      await open('/identity/applications');
      await answer('GET', '/applications', { value: [application()] });
    }

    it('lists the registrations without a secret, and passes the accessibility gate', async () => {
      await openApplications();

      expect(row('data-application', APP).textContent).toContain('Acme server');
      expect(host().querySelector('[data-issued-secret]')).toBeNull();

      click('Register an application');
      await settle();
      await axeClean();
    });

    it('registers a confidential client and shows its secret once, until dismissed', async () => {
      await openApplications();
      click('Register an application');
      await settle();

      const name = host().querySelector<HTMLInputElement>('#cc-app-name')!;
      name.value = 'Acme server';
      name.dispatchEvent(new Event('input'));
      const redirects = host().querySelector<HTMLTextAreaElement>('#cc-app-redirects')!;
      redirects.value = 'https://acme.example/cb\n\n https://acme.example/cb2 ';
      redirects.dispatchEvent(new Event('input'));

      // No kind chosen: refused on the page, nothing sent — public or confidential is never assumed.
      host().querySelector('form')!.dispatchEvent(new Event('submit'));
      await settle();
      http.expectNone(r => r.method === 'POST');
      expect(host().querySelector('[role="alert"]')?.textContent).toContain('server or a browser');

      host().querySelectorAll<HTMLInputElement>('input[name="cc-app-kind"]')[0].dispatchEvent(new Event('change'));
      await settle();
      host().querySelector('form')!.dispatchEvent(new Event('submit'));
      await settle();

      const post = await answer('POST', '/applications', application('the-secret-shown-once'), 201);
      expect(post.request.body).toEqual({
        displayName: 'Acme server',
        redirectUris: ['https://acme.example/cb', 'https://acme.example/cb2'],
        scopes: ['openid', 'profile'],
        publicClient: false
      });
      await answer('GET', '/applications', { value: [application()] });

      expect(host().querySelector('[data-secret]')?.textContent).toBe('the-secret-shown-once');

      click("I've stored it");
      await settle();
      expect(host().querySelector('[data-issued-secret]')).toBeNull();
      expect(host().textContent).not.toContain('the-secret-shown-once');
    });

    it('rotates after asking and shows the new secret; deletes after asking', async () => {
      await openApplications();

      click('Rotate secret', row('data-application', APP));
      await settle();
      http.expectNone(r => r.method === 'POST');
      click('Issue a new secret', row('data-application', APP));
      await settle();

      await answer('POST', `/applications/${APP}/rotateSecret`, application('the-rotated-secret'));
      await answer('GET', '/applications', { value: [application()] });
      expect(host().querySelector('[data-secret]')?.textContent).toBe('the-rotated-secret');

      click('Delete', row('data-application', APP));
      await settle();
      click('Delete it', row('data-application', APP));
      await settle();

      await answer('DELETE', `/applications/${APP}`, null, 204);
      await answer('GET', '/applications', { value: [] });

      // The secret of an application that no longer exists is taken off the screen.
      expect(host().querySelector('[data-issued-secret]')).toBeNull();
      expect(host().querySelector(`[data-application="${APP}"]`)).toBeNull();
    });

    it("drops a secret it's showing when the tenant switches, and reads the other tenant's list", async () => {
      // docs/plan/20 says the secret is dropped on a tenant switch; #41's review found no spec
      // that held the page to it.
      const context = TestBed.inject(TenantContextStore);
      context.load(
        [
          { id: TENANT, displayName: 'Acme' },
          { id: 't-other', displayName: 'Other' }
        ],
        []
      );
      await openApplications();

      click('Rotate secret', row('data-application', APP));
      await settle();
      click('Issue a new secret', row('data-application', APP));
      await settle();
      await answer('POST', `/applications/${APP}/rotateSecret`, application('acme-secret-on-screen'));
      await answer('GET', '/applications', { value: [application()] });
      expect(host().querySelector('[data-secret]')?.textContent).toBe('acme-secret-on-screen');

      context.selectTenant('t-other');
      await settle();

      // Other's list is asked for at Other's address, and Acme's secret is nowhere on the page.
      const other = http.expectOne(
        r => r.method === 'GET' && r.url === '/api/tenants/t-other/providers/CyberCloud.Identity/applications'
      );
      expect(host().querySelector('[data-issued-secret]')).toBeNull();
      expect(host().textContent).not.toContain('acme-secret-on-screen');
      other.flush({ value: [] });
      await settle();
      expect(host().textContent).not.toContain('acme-secret-on-screen');
    });
  });

  describe('sessions', () => {
    it('lists the caller’s sessions, marks this one, and signs one out after asking', async () => {
      await open('/identity/sessions');
      await answer('GET', '/sessions', {
        value: [session(LAPTOP, 'Firefox on Windows', true), session(PHONE, 'Safari on iOS', false)]
      });

      expect(row('data-session', LAPTOP).querySelector('[data-current]')).not.toBeNull();
      expect(row('data-session', PHONE).querySelector('[data-current]')).toBeNull();
      await axeClean();

      click('Sign out', row('data-session', PHONE));
      await settle();
      http.expectNone(r => r.method === 'DELETE');
      click('Sign it out', row('data-session', PHONE));
      await settle();

      await answer('DELETE', `/sessions/${PHONE}`, null, 204);
      await answer('GET', '/sessions', { value: [session(LAPTOP, 'Firefox on Windows', true)] });

      expect(host().querySelector(`[data-session="${PHONE}"]`)).toBeNull();
    });
  });
});

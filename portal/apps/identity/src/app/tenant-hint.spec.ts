import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { ActivatedRoute, convertToParamMap } from '@angular/router';
import { NAVIGATE } from './navigate';
import { SignInPage, tenantOf } from './pages/sign-in';

/**
 * Which tenant the sign-in page posts, and when it asks for one.
 *
 * ⚠ **An address alone names nothing on this platform.** docs/plan/11 § Sign-up and tenant creation
 * refuses a global email index, so the same address may exist in two tenants; the `/authorize`
 * request that sent the person here says which (the portal remembers it, a sign-up just created
 * it), and when it does not, the person types the organisation. Either way the value goes with
 * every first-factor request, unchanged between them — a page that posted one tenant on `begin`
 * and another on `password` would be trying the address against a tenant nobody chose.
 */
describe('the tenant hint', () => {
  const ADDRESS = 'someone@example.com';
  const AUTHORIZE = '/authorize?response_type=code&client_id=cyc-portal&state=s&tenant=contoso';

  let fixture: ComponentFixture<SignInPage>;
  let page: SignInPage;
  let http: HttpTestingController;

  const configure = (returnUrl: string) => {
    TestBed.configureTestingModule({
      imports: [SignInPage],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: ActivatedRoute, useValue: { snapshot: { queryParamMap: convertToParamMap({ returnUrl }) } } },
        { provide: NAVIGATE, useValue: () => undefined }
      ]
    });

    fixture = TestBed.createComponent(SignInPage);
    page = fixture.componentInstance;
    http = TestBed.inject(HttpTestingController);
  };

  afterEach(() => http.verify());

  it('reads the tenant off the request being resumed and posts it on every first-factor call', () => {
    configure(AUTHORIZE);

    expect(page.tenantFromRequest()).toBe('contoso');
    expect(page.tenant()).toBe('contoso');

    page.email.set(ADDRESS);
    page.onSubmit();

    const begin = http.expectOne('/api/signin/begin');
    expect(begin.request.body).toEqual({ email: ADDRESS, tenant: 'contoso' });
    begin.flush({ offered: ['passkey', 'password'] });

    page.password.set('hunter2');
    page.onSubmit();

    const password = http.expectOne('/api/signin/password');
    expect(password.request.body).toMatchObject({ email: ADDRESS, tenant: 'contoso', returnUrl: AUTHORIZE });
    password.flush({ succeeded: false, secondFactorRequired: false, returnUrl: AUTHORIZE, message: 'no' });
  });

  it('asks for the organisation when the request names no tenant, and posts what was typed', () => {
    configure('/authorize?response_type=code&client_id=cyc-portal&state=s');

    expect(page.tenantFromRequest()).toBeNull();
    expect(page.tenant()).toBeUndefined();

    // The field is on the page; typing into it decides the tenant.
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('#cc-organisation')).not.toBeNull();

    page.organisation.set('  contoso ');
    expect(page.tenant()).toBe('contoso');

    page.email.set(ADDRESS);
    page.onSubmit();

    http.expectOne('/api/signin/begin').flush({ offered: ['passkey', 'password'] });

    page.onUsePasskey();

    const passkey = http.expectOne('/api/signin/passkey/begin');
    expect(passkey.request.body).toEqual({ email: ADDRESS, tenant: 'contoso' });
    passkey.flush({ optionsJson: '' });
  });

  it('leaves the tenant to the server when nothing names one', () => {
    configure('/authorize?response_type=code&client_id=cyc-portal');

    page.email.set(ADDRESS);
    page.onSubmit();

    // `undefined` serializes to no field at all — the server's fallback applies, which on a
    // development run is the platform tenant and in production is a refusal.
    const begin = http.expectOne('/api/signin/begin');
    expect(begin.request.body).toEqual({ email: ADDRESS, tenant: undefined });
    expect(JSON.parse(JSON.stringify(begin.request.body))).toEqual({ email: ADDRESS });
    begin.flush({ offered: ['password'] });
  });

  it('does not show the organisation field when the request already names the tenant', () => {
    configure(AUTHORIZE);
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelector('#cc-organisation')).toBeNull();
  });

  describe('tenantOf', () => {
    it('parses the tenant out of a same-origin path', () => {
      expect(tenantOf('/authorize?tenant=contoso&state=s')).toBe('contoso');
      expect(tenantOf('/authorize?tenant=6f2b7c14-9a3d-4e58-b061-7c2d5e8f9a10')).toBe(
        '6f2b7c14-9a3d-4e58-b061-7c2d5e8f9a10'
      );
      expect(tenantOf('/authorize?tenant=%20contoso%20')).toBe('contoso');
    });

    it('answers null for a missing, empty or unparseable value', () => {
      expect(tenantOf('/authorize?state=s')).toBeNull();
      expect(tenantOf('/authorize?tenant=')).toBeNull();
      expect(tenantOf('/authorize?tenant=%20')).toBeNull();
      expect(tenantOf('/')).toBeNull();
    });

    it('reads the tenant off an /authorize request only', () => {
      // ⚠ The invitation page's link names the organisation being joined, not the one the person is
      // about to sign into with the account they already have (#43).
      expect(tenantOf('/invitation?tenant=contoso&invitation=i&token=t')).toBeNull();
      expect(tenantOf('/device-code?user_code=BCDF-GHJK&tenant=contoso')).toBeNull();
      expect(tenantOf('/authorize/else?tenant=contoso')).toBeNull();
    });
  });
});

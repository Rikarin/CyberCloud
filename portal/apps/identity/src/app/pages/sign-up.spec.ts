import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { ActivatedRoute, convertToParamMap } from '@angular/router';
import { NAVIGATE } from '../navigate';
import { sanitizeReturnUrl } from '../return-url';
import { SignUpPage } from './sign-up';

/**
 * The three steps of a self-serve sign-up, and the two things step three must get right: the
 * passkey is the primary action, and the page leaves through `NAVIGATE` for the return URL the
 * server rewrote with the new tenant.
 *
 * ⚠ **The passkey rows fake the authenticator, not the server.** jsdom has no WebAuthn, so the
 * suite installs a `navigator.credentials.create` that answers a fixed credential and a
 * `PublicKeyCredential` global so `passkeyUnavailableReason` offers the button at all. What is
 * asserted is the shape of what reaches the server — an attestation and never a challenge — which
 * is the property the ceremony is built around.
 */
describe('the sign-up page', () => {
  const ADDRESS = 'rene@example.com';
  const RETURN_URL = '/authorize?response_type=code&client_id=cyc-portal';
  const TENANT = '3b5f0e0e-8a19-4f7b-9d5c-1c2d3e4f5a6b';

  let fixture: ComponentFixture<SignUpPage>;
  let page: SignUpPage;
  let http: HttpTestingController;
  let assigned: string[];

  beforeEach(() => {
    assigned = [];

    TestBed.configureTestingModule({
      imports: [SignUpPage],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        {
          provide: ActivatedRoute,
          useValue: { snapshot: { queryParamMap: convertToParamMap({ returnUrl: RETURN_URL }) } }
        },
        // ⚠ The stub sanitizes exactly as the real NAVIGATE does, for the reason
        // second-factor.spec.ts gives: otherwise the hostile-returnUrl row would assert the stub.
        { provide: NAVIGATE, useValue: (url: string) => assigned.push(sanitizeReturnUrl(url)) }
      ]
    });

    fixture = TestBed.createComponent(SignUpPage);
    page = fixture.componentInstance;
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  /** Drives the page through steps one and two. */
  const reachDetails = () => {
    page.email.set(ADDRESS);
    page.onSubmit();
    http.expectOne('/api/signup/begin').flush({ sent: true, returnUrl: RETURN_URL });

    page.code.set('482913');
    page.onSubmit();
    http.expectOne('/api/signup/verify').flush({ verified: true });

    page.displayName.set('Rene');
    page.organizationName.set('Contoso');
  };

  const success = () => ({
    succeeded: true,
    tenantId: TENANT,
    returnUrl: `${RETURN_URL}&tenant=${TENANT}`,
    message: ''
  });

  // ── Step one ───────────────────────────────────────────────────────────────────────────────

  it('posts the address and the return URL, and moves to the code step', () => {
    page.email.set(ADDRESS);
    page.onSubmit();

    const request = http.expectOne('/api/signup/begin');
    expect(request.request.body).toEqual({ email: ADDRESS, returnUrl: RETURN_URL });

    request.flush({ sent: true, returnUrl: RETURN_URL });

    expect(page.step()).toBe('code');
    expect(assigned).toEqual([]);
  });

  it('tells a developer where the code is', async () => {
    page.email.set(ADDRESS);
    page.onSubmit();
    http.expectOne('/api/signup/begin').flush({ sent: true, returnUrl: RETURN_URL });

    await fixture.whenStable();
    const text = (fixture.nativeElement as HTMLElement).textContent ?? '';

    // ⚠ On a development run the mail lands in Mailpit, not in the person's real inbox (#93), and
    // the code is on the silo's console as well. A person staring at their real inbox has no other
    // way to learn either.
    expect(text).toContain(ADDRESS);
    expect(text).toContain('localhost:8025');
    expect(text).toContain('Aspire dashboard');
  });

  it('renders the closed message verbatim and goes nowhere', () => {
    page.email.set(ADDRESS);
    page.onSubmit();
    http.expectOne('/api/signup/begin').flush({ succeeded: false, message: 'Sign-up is not open on this deployment.' });

    expect(page.error()).toBe('Sign-up is not open on this deployment.');
    expect(page.step()).toBe('address');
  });

  // ── Step two ───────────────────────────────────────────────────────────────────────────────

  it('stays on the code step with a wrong code and moves on with the right one', () => {
    page.email.set(ADDRESS);
    page.onSubmit();
    http.expectOne('/api/signup/begin').flush({ sent: true, returnUrl: RETURN_URL });

    page.code.set('000000');
    page.onSubmit();
    http.expectOne('/api/signup/verify').flush({ verified: false });

    expect(page.step()).toBe('code');
    expect(page.error()).not.toBeNull();
    expect(page.code()).toBe('');

    page.code.set('482913');
    page.onSubmit();
    const request = http.expectOne('/api/signup/verify');
    // ⚠ The code and nothing else. Which sign-up is answering comes from the ticket cookie.
    expect(request.request.body).toEqual({ code: '482913' });
    request.flush({ verified: true });

    expect(page.step()).toBe('details');
    expect(page.error()).toBeNull();
  });

  it('sends a new code on the same sign-up from the code step', () => {
    page.email.set(ADDRESS);
    page.onSubmit();
    http.expectOne('/api/signup/begin').flush({ sent: true, returnUrl: RETURN_URL });

    page.onResend();
    http.expectOne('/api/signup/begin').flush({ sent: true, returnUrl: RETURN_URL });

    expect(page.step()).toBe('code');
  });

  // ── Step three ─────────────────────────────────────────────────────────────────────────────

  describe('with an authenticator', () => {
    const originalSecure = Object.getOwnPropertyDescriptor(window, 'isSecureContext');
    const originalCredentials = Object.getOwnPropertyDescriptor(navigator, 'credentials');
    let createCalls: CredentialCreationOptions[];

    beforeEach(() => {
      createCalls = [];
      Object.defineProperty(window, 'isSecureContext', { value: true, configurable: true });
      Object.defineProperty(window, 'PublicKeyCredential', { value: class {}, configurable: true });
      Object.defineProperty(navigator, 'credentials', {
        configurable: true,
        value: {
          create: (options: CredentialCreationOptions) => {
            createCalls.push(options);
            const bytes = new Uint8Array([1, 2, 3]).buffer;
            return Promise.resolve({
              id: 'cred-1',
              rawId: bytes,
              type: 'public-key',
              getClientExtensionResults: () => ({}),
              response: { attestationObject: bytes, clientDataJSON: bytes, getTransports: () => ['internal'] }
            });
          }
        }
      });
    });

    afterEach(() => {
      if (originalSecure) {
        Object.defineProperty(window, 'isSecureContext', originalSecure);
      }
      if (originalCredentials) {
        Object.defineProperty(navigator, 'credentials', originalCredentials);
      } else {
        delete (navigator as { credentials?: unknown }).credentials;
      }
      delete (window as { PublicKeyCredential?: unknown }).PublicKeyCredential;
    });

    it('offers the passkey as the primary action and the password as a link', async () => {
      reachDetails();
      await fixture.whenStable();

      const element = fixture.nativeElement as HTMLElement;
      const primary = element.querySelector('button[type="submit"]');
      const link = element.querySelector('button[type="button"][variant="link"]');

      // docs/plan/11 § Credentials: the default offered credential at sign-up, not an upsell. The
      // passkey is the button with the weight; the password is the link beneath it.
      expect(primary?.textContent).toContain('Create with a passkey');
      expect(primary?.getAttribute('color')).toBe('primary');
      expect(link?.textContent).toContain('Use a password instead');
    });

    it('creates a passkey and completes with the attestation, never a challenge', async () => {
      reachDetails();
      page.onSubmit();

      const begin = http.expectOne('/api/signup/passkey/begin');
      expect(begin.request.body).toEqual({ displayName: 'Rene' });
      begin.flush({
        optionsJson: JSON.stringify({
          challenge: 'AQID',
          rp: { id: 'localhost', name: 'Cyber Cloud' },
          user: { id: 'BAUG', name: ADDRESS, displayName: 'Rene' },
          pubKeyCredParams: [{ type: 'public-key', alg: -7 }]
        })
      });

      // The ceremony is a promise; let it settle before looking for the completion.
      await new Promise(resolve => setTimeout(resolve, 0));

      expect(createCalls.length).toBe(1);

      const complete = http.expectOne('/api/signup/complete');
      const body = complete.request.body as {
        credential: { kind: string; attestationJson: string };
        displayName: string;
        organizationName: string;
        returnUrl: string;
      };

      expect(Object.keys(body).sort()).toEqual(['credential', 'displayName', 'organizationName', 'returnUrl']);
      expect(body.credential.kind).toBe('passkey');
      expect(JSON.parse(body.credential.attestationJson)).toMatchObject({ id: 'cred-1', type: 'public-key' });
      // ⚠ No challenge and no options anywhere in the body. The server verifies against the copy
      // in its own cookie; a body that carried one would let a caller choose what is signed.
      expect(JSON.stringify(body)).not.toContain('challenge');

      complete.flush(success());

      expect(assigned).toEqual([`${RETURN_URL}&tenant=${TENANT}`]);
    });
  });

  it('completes with a password after the link is chosen', () => {
    reachDetails();
    page.onUsePassword();
    page.password.set('correct-horse-battery-staple');
    page.onSubmit();

    const request = http.expectOne('/api/signup/complete');
    expect(request.request.body).toEqual({
      displayName: 'Rene',
      organizationName: 'Contoso',
      credential: { kind: 'password', password: 'correct-horse-battery-staple' },
      returnUrl: RETURN_URL
    });

    request.flush(success());

    // ⚠ Through NAVIGATE, with the tenant the server put in the query — a full-page navigation to
    // the /authorize request that started this, carrying the cookie the completion just issued.
    expect(assigned).toEqual([`${RETURN_URL}&tenant=${TENANT}`]);
    expect(page.password()).toBe('');
  });

  it('renders a step failure verbatim and stays on the details step', () => {
    reachDetails();
    page.onUsePassword();
    page.password.set('correct-horse-battery-staple');
    page.onSubmit();

    http.expectOne('/api/signup/complete').flush({
      succeeded: false,
      tenantId: '',
      returnUrl: RETURN_URL,
      message: 'That organisation name is taken.'
    });

    // The server chose the sentence, and it is about this person's own input. The page stays put
    // so they can change the name; complete is re-drivable on the server's side.
    expect(page.error()).toBe('That organisation name is taken.');
    expect(page.step()).toBe('details');
    expect(page.password()).toBe('');
    expect(assigned).toEqual([]);
  });

  it('never navigates to a hostile returnUrl the server sent back', () => {
    reachDetails();
    page.onUsePassword();
    page.password.set('correct-horse-battery-staple');
    page.onSubmit();

    http.expectOne('/api/signup/complete').flush({ ...success(), returnUrl: '//evil.example' });

    expect(assigned).toEqual(['/']);
  });

  it('never puts a credential in a query string', () => {
    reachDetails();
    page.onUsePassword();
    page.password.set('correct-horse-battery-staple');
    page.onSubmit();

    const request = http.expectOne('/api/signup/complete');
    expect(request.request.method).toBe('POST');
    expect(request.request.urlWithParams).toBe('/api/signup/complete');
    request.flush(success());
  });
});

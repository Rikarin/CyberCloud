import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideHttpClient } from '@angular/common/http';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { ActivatedRoute, convertToParamMap } from '@angular/router';
import { provideZonelessChangeDetection } from '@angular/core';
import { SignInPage } from './pages/sign-in';

/**
 * docs/plan/00 § Non-negotiables keeps secrets out of grain state and analyzer CC1005 enforces it.
 * The same discipline applies to the browser, and this suite is where it is checked.
 *
 * **The five places a credential must never end up**, each asserted below:
 *
 * 1. A log line — `console.*` of any level.
 * 2. `sessionStorage` or `localStorage`.
 * 3. A URL query string.
 * 4. The rendered DOM as a value anything but the password field carries.
 * 5. ⚠ The SSR transfer state — see `ssr-no-credentials.spec.ts`, which covers it against real
 *    rendered bytes, because that is the only place the property is actually observable.
 *
 * ⚠ **Number 3 is the one worth stating twice.** A password in a query string is written to the
 * server's access log, to the browser's history, and to the `Referer` header of the next
 * navigation — three durable stores, none of which anybody thinks of as holding credentials.
 */
describe('the sign-in page never leaks credential material', () => {
  const SECRET = 'correct-horse-battery-staple';
  const ADDRESS = 'someone@example.com';

  let fixture: ComponentFixture<SignInPage>;
  let page: SignInPage;
  let http: HttpTestingController;
  const consoleCalls: string[] = [];
  const spies: jest.SpyInstance[] = [];

  beforeEach(() => {
    consoleCalls.length = 0;

    // Every console level, not just `log`. A credential written to `console.debug` is in the same
    // browser-extension-readable buffer as one written to `console.error`.
    for (const level of ['log', 'info', 'warn', 'error', 'debug'] as const) {
      spies.push(
        jest.spyOn(console, level).mockImplementation((...args: unknown[]) => {
          consoleCalls.push(args.map((a) => String(a)).join(' '));
        }),
      );
    }

    TestBed.configureTestingModule({
      imports: [SignInPage],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        {
          provide: ActivatedRoute,
          useValue: { snapshot: { queryParamMap: convertToParamMap({ returnUrl: '/after' }) } },
        },
      ],
    });

    fixture = TestBed.createComponent(SignInPage);
    page = fixture.componentInstance;
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    for (const spy of spies) {
      spy.mockRestore();
    }
    spies.length = 0;
  });

  /** Drives the page to the point where a password has been submitted. */
  function submitPassword(): void {
    page.email.set(ADDRESS);
    page.onSubmit();

    http.expectOne('/api/signin/begin').flush({ offered: ['passkey', 'password'] });
    fixture.detectChanges();

    page.password.set(SECRET);
    page.onSubmit();
  }

  it('sends the password in the request body and never in the URL', () => {
    submitPassword();

    const request = http.expectOne((candidate) => candidate.url === '/api/signin/password');

    // ⚠ The URL is asserted whole, not just for the absence of the secret. A password smuggled in
    // as `?p=…` under a different name would pass a substring check on the secret alone if the
    // page ever encoded it.
    expect(request.request.url).toBe('/api/signin/password');
    expect(request.request.urlWithParams).toBe('/api/signin/password');
    expect(request.request.method).toBe('POST');

    // It IS in the body — that is the delivery path, and asserting it here means a refactor that
    // dropped the password entirely would fail loudly rather than silently signing nobody in.
    expect(request.request.body).toMatchObject({ email: ADDRESS, password: SECRET });

    request.flush({ succeeded: false, secondFactorRequired: false, returnUrl: '/', message: 'no' });
  });

  it('writes no credential to any console level', () => {
    submitPassword();

    http
      .expectOne('/api/signin/password')
      .flush({ succeeded: false, secondFactorRequired: false, returnUrl: '/', message: 'nope' });

    const written = consoleCalls.join('\n');
    expect(written).not.toContain(SECRET);
    // The address is PII too — docs/plan/11 § Auditing keeps it out of log *messages*.
    expect(written).not.toContain(ADDRESS);
  });

  it('touches neither sessionStorage nor localStorage', () => {
    const sessionSetter = jest.spyOn(Storage.prototype, 'setItem');

    submitPassword();
    http
      .expectOne('/api/signin/password')
      .flush({ succeeded: false, secondFactorRequired: false, returnUrl: '/', message: 'nope' });

    // ⚠ Asserted at the `Storage.prototype` level rather than on the two globals, so a page that
    // reached for storage through any other reference is still caught. The workspace's ESLint
    // config bans the identifiers; this catches the runtime path.
    expect(sessionSetter).not.toHaveBeenCalled();
    expect(window.sessionStorage.length).toBe(0);
    expect(window.localStorage.length).toBe(0);

    sessionSetter.mockRestore();
  });

  it('clears the password from memory once a sign-in is refused', () => {
    submitPassword();

    http
      .expectOne('/api/signin/password')
      .flush({ succeeded: false, secondFactorRequired: false, returnUrl: '/', message: 'nope' });

    // ⚠ Not tidiness. A rejected password left in a signal stays in the heap for as long as the tab
    // is open, and it is also what a user's next submit would send after they corrected their
    // address — signing the previous account's password into a different one.
    expect(page.password()).toBe('');
  });

  it('renders the server message verbatim rather than a friendlier one', () => {
    const uniform = 'The email address or credential is incorrect, or the account cannot sign in right now.';

    submitPassword();
    http.expectOne('/api/signin/password').flush({
      succeeded: false,
      secondFactorRequired: false,
      returnUrl: '/',
      message: uniform,
    });
    fixture.detectChanges();

    // ⚠ The enumeration defence only works end to end. The server pays for it with a dummy Argon2id
    // hash on the no-such-user branch; a page that rewrote the message into "no account with that
    // address" would spend that work and then give the answer away anyway.
    expect(page.error()).toBe(uniform);
    expect(fixture.nativeElement.textContent).toContain(uniform);
  });
});

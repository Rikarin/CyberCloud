import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideHttpClient } from '@angular/common/http';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { ActivatedRoute, convertToParamMap } from '@angular/router';
import { provideZonelessChangeDetection } from '@angular/core';
import { NAVIGATE } from './navigate';
import { sanitizeReturnUrl } from './return-url';
import { SignInPage } from './pages/sign-in';

/**
 * The step a password sign-in lands on, and the two ways it must not behave.
 *
 * ⚠ **`secondFactorRequired` is the whole subject.** `SignInService` opens the session as soon as
 * the password verifies and sets that flag for every credential but a passkey — so `succeeded` is
 * `true` on a response the user cannot yet act on. A page that navigated on `succeeded` alone would
 * send them to `/authorize` holding a cookie the server marks `2fa=pending`, which bounces them
 * straight back here. The server fails closed, so that is a loop rather than a hole; it is
 * indistinguishable from a broken sign-in to the person in front of it.
 */
describe('the second-factor step', () => {
  const ADDRESS = 'someone@example.com';
  const SECRET = 'correct-horse-battery-staple';

  let fixture: ComponentFixture<SignInPage>;
  let page: SignInPage;
  let http: HttpTestingController;
  let assigned: string[];

  beforeEach(() => {
    assigned = [];

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
        // ⚠ The stub sanitizes exactly as the real NAVIGATE does. Leaving that out would make the
        // hostile-returnUrl assertion below pass against a page that never sanitized anything —
        // the test would be asserting the stub's behaviour rather than the page's contract.
        { provide: NAVIGATE, useValue: (url: string) => assigned.push(sanitizeReturnUrl(url)) },
      ],
    });

    fixture = TestBed.createComponent(SignInPage);
    page = fixture.componentInstance;
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  /** Drives the page to the credential step and submits a password. */
  const signInWithPassword = () => {
    page.email.set(ADDRESS);
    page.onSubmit();
    http.expectOne('/api/signin/begin').flush({ offered: ['passkey', 'password'] });

    page.password.set(SECRET);
    page.onSubmit();

    return http.expectOne('/api/signin/password');
  };

  it('stops on the second-factor step instead of navigating', () => {
    signInWithPassword().flush({
      succeeded: true,
      secondFactorRequired: true,
      returnUrl: '/after',
      message: '',
    });

    expect(page.step()).toBe('second-factor');
    expect(assigned).toEqual([]);
  });

  it('clears the password on the way into the second-factor step', () => {
    signInWithPassword().flush({
      succeeded: true,
      secondFactorRequired: true,
      returnUrl: '/after',
      message: '',
    });

    // ⚠ The password is not needed again, and a signal still holding it while the user types a code
    // is a credential kept alive for no reason. `credentials-never-leak.spec.ts` covers where it
    // must never go; this covers how long it lives.
    expect(page.password()).toBe('');
  });

  it('navigates once the second factor is accepted', () => {
    signInWithPassword().flush({
      succeeded: true,
      secondFactorRequired: true,
      returnUrl: '/after',
      message: '',
    });

    page.code.set('123456');
    page.onSubmit();

    const request = http.expectOne('/api/signin/totp');

    // ⚠ The body carries the code and the destination and nothing that names the user. Who is
    // answering comes from the session cookie the first factor set — a user id here would let
    // anybody holding a pending session name somebody else's account.
    expect(Object.keys(request.request.body as object).sort()).toEqual(['code', 'returnUrl']);

    request.flush({
      succeeded: true,
      secondFactorRequired: false,
      returnUrl: '/after',
      message: '',
    });

    expect(assigned).toEqual(['/after']);
  });

  it('posts a recovery code to its own endpoint after switching', () => {
    signInWithPassword().flush({
      succeeded: true,
      secondFactorRequired: true,
      returnUrl: '/after',
      message: '',
    });

    page.onSwitchFactor();
    expect(page.factor()).toBe('recoveryCode');

    page.code.set('aaaa-bbbb-cc');
    page.onSubmit();

    http.expectOne('/api/signin/recovery-code').flush({
      succeeded: true,
      secondFactorRequired: false,
      returnUrl: '/after',
      message: '',
    });

    expect(assigned).toEqual(['/after']);
  });

  it('clears the code when the user switches factor', () => {
    signInWithPassword().flush({
      succeeded: true,
      secondFactorRequired: true,
      returnUrl: '/after',
      message: '',
    });

    page.code.set('123456');
    page.onSwitchFactor();

    // A six-digit authenticator code resubmitted as a recovery code is a failed attempt the user
    // did not make, and failed attempts drive the lockout ladder.
    expect(page.code()).toBe('');
  });

  it('renders the server message verbatim when the code is refused', () => {
    signInWithPassword().flush({
      succeeded: true,
      secondFactorRequired: true,
      returnUrl: '/after',
      message: '',
    });

    const uniform =
      'The email address or credential is incorrect, or the account cannot sign in right now.';

    page.code.set('000000');
    page.onSubmit();
    http.expectOne('/api/signin/totp').flush({
      succeeded: false,
      secondFactorRequired: false,
      returnUrl: '/after',
      message: uniform,
    });

    // ⚠ Verbatim, and the same string every other failure produces. A page that translated "that
    // code is wrong" would tell an attacker holding a stolen password that the account exists and
    // has TOTP enrolled.
    expect(page.error()).toBe(uniform);
    expect(page.code()).toBe('');
    expect(page.step()).toBe('second-factor');
    expect(assigned).toEqual([]);
  });

  it('never navigates to a hostile returnUrl the server sent back', () => {
    signInWithPassword().flush({
      succeeded: true,
      secondFactorRequired: true,
      returnUrl: '/after',
      message: '',
    });

    page.code.set('123456');
    page.onSubmit();

    // ⚠ The server sanitizes too. This is the case where that is not enough: a compromised, patched
    // or simply wrong server response is exactly when the client-side check is the only one left.
    http.expectOne('/api/signin/totp').flush({
      succeeded: true,
      secondFactorRequired: false,
      returnUrl: '//evil.example',
      message: '',
    });

    expect(assigned).toEqual(['/']);
  });
});

/**
 * The passkey button, which until now rendered "not available yet".
 *
 * ⚠ **The challenge never passes through this app.** `begin` returns the options to hand to the
 * authenticator and the server keeps its own copy in a protected HttpOnly cookie; `complete` posts
 * the assertion and nothing else. These tests assert the absence, because an endpoint that accepted
 * the options back would reduce the assertion to a signature over data the caller chose.
 */
describe('the passkey button', () => {
  let fixture: ComponentFixture<SignInPage>;
  let page: SignInPage;
  let http: HttpTestingController;

  beforeEach(() => {
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

  afterEach(() => http.verify());

  it('asks the server for a challenge rather than refusing', () => {
    page.email.set('someone@example.com');
    page.onUsePasskey();

    // The regression this guards: `onUsePasskey` used to set an error string and make no request at
    // all. A button that only ever explains itself is one nobody notices has been wired up.
    const request = http.expectOne('/api/signin/passkey/begin');
    expect(request.request.body).toEqual({ email: 'someone@example.com' });

    // jsdom has no authenticator, so the ceremony ends here. What matters is that the request was
    // made and that the response carries options rather than a verdict about the address.
    request.flush({ optionsJson: '' });
  });

  it('shows the uniform failure when the server cannot build a challenge', () => {
    page.email.set('someone@example.com');
    page.onUsePasskey();

    // ⚠ An empty options string is a relying-party misconfiguration, never an answer about the
    // address — so it must not get its own message either.
    http.expectOne('/api/signin/passkey/begin').flush({ optionsJson: '' });

    expect(page.error()).toBe(
      'The email address or credential is incorrect, or the account cannot sign in right now.',
    );
    expect(page.busy()).toBe(false);
  });
});

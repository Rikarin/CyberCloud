import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { ActivatedRoute, convertToParamMap } from '@angular/router';
import { ConsentPage } from './consent';

/**
 * The consent page: it renders what the server answered and nothing from the URL, and its form
 * is the request posted back with the answer.
 *
 * ⚠ **The property that matters is negative.** The `/authorize` request in the query carries a
 * `client_id`, and a phisher's link can carry any `client_id` — so the page must never print it,
 * or anything else out of the query, as a name. What it prints is `clientName` from `/api/consent`,
 * which the server resolved from the registration. The rows below feed a query whose client id is a
 * marker string and assert the marker appears in exactly one place: a hidden field's value.
 */
describe('the consent page', () => {
  const RETURN_URL =
    '/authorize?response_type=code&client_id=MARKER-acme&redirect_uri=https%3A%2F%2Facme.example%2Fcb&scope=openid%20profile&state=s1&code_challenge=c&code_challenge_method=S256&tenant=contoso';

  let fixture: ComponentFixture<ConsentPage>;
  let http: HttpTestingController;

  const configure = (returnUrl: string | null) => {
    TestBed.configureTestingModule({
      imports: [ConsentPage],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        {
          provide: ActivatedRoute,
          useValue: { snapshot: { queryParamMap: convertToParamMap(returnUrl === null ? {} : { returnUrl }) } }
        }
      ]
    });

    fixture = TestBed.createComponent(ConsentPage);
    http = TestBed.inject(HttpTestingController);
  };

  afterEach(() => http.verify());

  it('asks the server what to render, for the sanitized return URL', () => {
    configure(RETURN_URL);
    fixture.detectChanges();

    const request = http.expectOne(r => r.url === '/api/consent');

    expect(request.request.method).toBe('GET');
    expect(request.request.params.get('returnUrl')).toBe(RETURN_URL);
    request.flush({
      ready: true,
      clientName: 'Acme dashboard',
      scopes: ['openid', 'profile'],
      returnUrl: RETURN_URL,
      message: ''
    });
    fixture.detectChanges();

    const text = fixture.nativeElement.textContent as string;

    expect(text).toContain('Acme dashboard');
    expect(text).toContain('Sign you in and know who you are');
    expect(text).toContain('See your name and email address');
  });

  it('renders the registered name and never the client id from the query', () => {
    configure(RETURN_URL);
    fixture.detectChanges();
    http
      .expectOne(r => r.url === '/api/consent')
      .flush({
        ready: true,
        clientName: 'Acme dashboard',
        scopes: ['openid'],
        returnUrl: RETURN_URL,
        message: ''
      });
    fixture.detectChanges();

    const element = fixture.nativeElement as HTMLElement;

    // The marker is in the DOM exactly once: the hidden client_id field the form posts back. It is
    // in no text node.
    expect(element.textContent).not.toContain('MARKER-acme');

    const hidden = [...element.querySelectorAll<HTMLInputElement>('input[type="hidden"]')];

    expect(hidden.filter(input => input.value === 'MARKER-acme').map(input => input.name)).toEqual(['client_id']);
  });

  it('posts the request back to /authorize as a native form with allow and deny', () => {
    configure(RETURN_URL);
    fixture.detectChanges();
    http
      .expectOne(r => r.url === '/api/consent')
      .flush({
        ready: true,
        clientName: 'Acme dashboard',
        scopes: ['openid', 'profile'],
        returnUrl: RETURN_URL,
        message: ''
      });
    fixture.detectChanges();

    const form = (fixture.nativeElement as HTMLElement).querySelector('form') as HTMLFormElement;

    expect(form.getAttribute('method')).toBe('post');
    expect(form.getAttribute('action')).toBe(RETURN_URL);

    // Every pair of the request, by name and value, decoded — the server re-encodes them.
    const pairs = Object.fromEntries(
      [...form.querySelectorAll<HTMLInputElement>('input[type="hidden"]')].map(input => [input.name, input.value])
    );

    expect(pairs).toEqual({
      response_type: 'code',
      client_id: 'MARKER-acme',
      redirect_uri: 'https://acme.example/cb',
      scope: 'openid profile',
      state: 's1',
      code_challenge: 'c',
      code_challenge_method: 'S256',
      tenant: 'contoso'
    });

    // ⚠ The answer is the button, named `consent`, so a submit carries exactly one of the two.
    const buttons = [...form.querySelectorAll<HTMLButtonElement>('button[type="submit"]')];

    expect(buttons.map(button => [button.name, button.value])).toEqual([
      ['consent', 'allow'],
      ['consent', 'deny']
    ]);
  });

  it('renders the server sentence when there is nothing to consent to', () => {
    configure(RETURN_URL);
    fixture.detectChanges();
    http
      .expectOne(r => r.url === '/api/consent')
      .flush({
        ready: false,
        clientName: '',
        scopes: [],
        returnUrl: '/',
        message: 'Sign in first.'
      });
    fixture.detectChanges();

    const element = fixture.nativeElement as HTMLElement;

    expect(element.querySelector('[role="alert"]')?.textContent).toContain('Sign in first.');
    expect(element.querySelector('form')).toBeNull();
  });

  it.each(['//evil.example/authorize?x=1', 'https://evil.example/authorize', 'javascript:alert(1)', null])(
    'never posts to an unsafe return URL (%s)',
    returnUrl => {
      // The action is the SERVER's returnUrl, sanitized again here: a hostile one collapses to `/`,
      // and the route's own value never reaches the form at all.
      configure(returnUrl);
      fixture.detectChanges();

      const request = http.expectOne(r => r.url === '/api/consent');

      expect(request.request.params.get('returnUrl')).toBe('/');
      request.flush({
        ready: true,
        clientName: 'Acme dashboard',
        scopes: ['openid'],
        returnUrl: returnUrl ?? 'https://evil.example/authorize',
        message: ''
      });
      fixture.detectChanges();

      const form = (fixture.nativeElement as HTMLElement).querySelector('form') as HTMLFormElement;

      expect(form.getAttribute('action')).toBe('/');
      expect(form.querySelectorAll('input[type="hidden"]').length).toBe(0);
    }
  );

  it('shows a sentence rather than a form when the server cannot be reached', () => {
    configure(RETURN_URL);
    fixture.detectChanges();
    http.expectOne(r => r.url === '/api/consent').flush('down', { status: 503, statusText: 'Service Unavailable' });
    fixture.detectChanges();

    const element = fixture.nativeElement as HTMLElement;

    expect(element.querySelector('[role="alert"]')?.textContent).toContain('Something went wrong');
    expect(element.querySelector('form')).toBeNull();
  });
});

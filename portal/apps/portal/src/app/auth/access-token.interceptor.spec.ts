import { HttpClient, provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { AUTH_NAVIGATE, AuthSession, CookieJar, IDENTITY_ISSUER, TENANT_CONTEXT_SOURCE } from '@cybercloud/shell';
import { FakeTokenEndpoint, MemoryCookieJar, unsignedJwt } from '@cybercloud/shell/testing';
import { firstValueFrom } from 'rxjs';
import { accessTokenInterceptor } from './access-token.interceptor';

const TENANT = '7f3a1c2e4b7d4e3a9c1d2b6f8a7e5d43';
const token = (marker: string): string => unsignedJwt({ sub: 'u-1', tid: TENANT, marker });

/**
 * docs/plan/10 § Authentication inputs. The timer refreshes at `exp − 60 s`; a 401 is what the
 * clock drifting or a revocation looks like, and the answer to it is one refresh and one retry —
 * never a loop, and never a retry without a new token.
 */
describe('accessTokenInterceptor — a 401 from /api', () => {
  let http: HttpClient;
  let backend: HttpTestingController;
  let endpoint: FakeTokenEndpoint;
  let navigated: string[];

  beforeEach(() => {
    navigated = [];
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(withInterceptors([accessTokenInterceptor])),
        provideHttpClientTesting(),
        { provide: IDENTITY_ISSUER, useValue: 'http://localhost:5101' },
        { provide: CookieJar, useClass: MemoryCookieJar },
        { provide: AUTH_NAVIGATE, useValue: (url: string) => navigated.push(url) },
        { provide: TENANT_CONTEXT_SOURCE, useValue: { load: async () => ({ displayName: 'T', subscriptions: [] }) } }
      ]
    });
    http = TestBed.inject(HttpClient);
    backend = TestBed.inject(HttpTestingController);
    endpoint = new FakeTokenEndpoint({
      status: 200,
      body: { access_token: token('renewed'), token_type: 'Bearer', expires_in: 600 }
    });
  });

  afterEach(() => {
    endpoint.restore();
    backend.verify();
  });

  const signIn = (): void => {
    TestBed.inject(AuthSession).accept({ access_token: token('stale'), token_type: 'Bearer', expires_in: 600 });
  };

  /** One turn of the event loop, so the refresh's `await`s reach the retry. */
  const settle = (): Promise<void> => new Promise(resolve => setTimeout(resolve, 0));

  it('a401RefreshesOnceAndRetriesOnce', async () => {
    signIn();
    const reply = firstValueFrom(http.get('/api/tenants/t?api-version=2026-08-01'));

    const first = backend.expectOne('/api/tenants/t?api-version=2026-08-01');
    expect(first.request.headers.get('Authorization')).toBe(`Bearer ${token('stale')}`);
    first.flush({ error: { code: 'Unauthorized', message: 'expired' } }, { status: 401, statusText: 'Unauthorized' });
    await settle();

    expect(endpoint.calls.map(c => c.form.get('grant_type'))).toEqual(['refresh_token']);

    const retry = backend.expectOne('/api/tenants/t?api-version=2026-08-01');
    expect(retry.request.headers.get('Authorization')).toBe(`Bearer ${token('renewed')}`);
    retry.flush({ name: 'Contoso' });

    await expect(reply).resolves.toEqual({ name: 'Contoso' });
  });

  it('rethrows a 401 on the retry rather than refreshing again', async () => {
    signIn();
    const reply = firstValueFrom(http.get('/api/x'));

    backend.expectOne('/api/x').flush(null, { status: 401, statusText: 'Unauthorized' });
    await settle();
    backend.expectOne('/api/x').flush(null, { status: 401, statusText: 'Unauthorized' });

    await expect(reply).rejects.toMatchObject({ status: 401 });
    expect(endpoint.calls).toHaveLength(1);
  });

  it('rethrows the 401 and begins a sign-in when the refresh is refused', async () => {
    endpoint.restore();
    endpoint = new FakeTokenEndpoint({ status: 400, body: { error: 'invalid_grant' } });
    signIn();
    const rejected = expect(firstValueFrom(http.get('/api/x'))).rejects.toMatchObject({ status: 401 });

    backend.expectOne('/api/x').flush(null, { status: 401, statusText: 'Unauthorized' });
    await settle();

    await rejected;
    backend.expectNone('/api/x');
    expect(navigated).toHaveLength(1);
    expect(navigated[0]).toMatch(/\/authorize\?/);
  });

  it('does not refresh when no token was attached — a 401 there is not a stale token', async () => {
    const reply = firstValueFrom(http.get('/api/x'));

    backend.expectOne('/api/x').flush(null, { status: 401, statusText: 'Unauthorized' });

    await expect(reply).rejects.toMatchObject({ status: 401 });
    expect(endpoint.calls).toEqual([]);
  });

  it('never attaches the token to another origin', async () => {
    signIn();
    const reply = firstValueFrom(http.get('https://cdn.example/asset.json'));

    const request = backend.expectOne('https://cdn.example/asset.json');
    expect(request.request.headers.has('Authorization')).toBe(false);
    request.flush({});
    await reply;
  });
});

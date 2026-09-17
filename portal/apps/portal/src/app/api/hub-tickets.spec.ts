import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { apiVersion } from '@cybercloud/api';
import { AccessTokenStore } from '@cybercloud/shell';
import { accessTokenInterceptor } from '../auth/access-token.interceptor';
import { HUB_PATH, HubTicketsApi } from './hub-tickets';

/**
 * The ticket, and the one property it exists for: the bearer token reaches the gateway in a
 * header on the mint and nowhere in the socket address.
 */
const TOKEN = 'the-bearer-token';

describe('HubTicketsApi', () => {
  let api: HubTicketsApi;
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(withInterceptors([accessTokenInterceptor])),
        provideHttpClientTesting()
      ]
    });

    TestBed.inject(AccessTokenStore).set(TOKEN, Date.now() + 600_000);
    api = TestBed.inject(HubTicketsApi);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  it('mints with an authenticated POST on the hub path the platform named', async () => {
    const minted = api.mint('/hubs/terminal');

    const request = http.expectOne(r => r.method === 'POST' && r.url === '/api/hubs/terminal/ticket');
    expect(request.request.headers.get('Authorization')).toBe(`Bearer ${TOKEN}`);
    expect(request.request.params.get('api-version')).toBe(apiVersion);
    expect(request.request.body).toBeNull();

    request.flush({ ticket: 'abc', hub: '/hubs/terminal', expiresAt: '2026-08-11T12:00:30Z' });

    expect((await minted).ticket).toBe('abc');
  });

  it('builds the socket address on this origin with the ticket as the only query parameter', () => {
    // jsdom's location is http://localhost/, so the scheme is ws:.
    const url = api.socketUrl('/hubs/terminal', 'a+b/c');

    expect(url).toBe('ws://localhost/api/hubs/terminal?ticket=a%2Bb%2Fc');
    // ⚠ The sabotage this spec exists for: the token must never be in a URL.
    expect(url).not.toContain(TOKEN);
    expect(url).not.toContain('access_token');
  });

  it('refuses a hub path that is not /hubs/{name}, before sending anything', async () => {
    for (const bad of [
      '/tenants/t/subscriptions/s',
      '/hubs/terminal/ticket',
      'hubs/terminal',
      '/hubs/',
      '//evil/hubs/x'
    ]) {
      expect(HUB_PATH.test(bad)).toBe(false);
      await expect(api.mint(bad)).rejects.toThrow('is not a hub path');
      expect(() => api.socketUrl(bad, 't')).toThrow('is not a hub path');
    }

    http.expectNone(() => true);
  });
});

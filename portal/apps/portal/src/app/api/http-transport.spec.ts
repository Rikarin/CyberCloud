import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { apiVersion } from '@cybercloud/api';
import { API_BASE_PATH, ApiCallError, HttpApiTransport, operationIdOf } from './http-transport';

/**
 * The one transport the portal supplies to the generated client, and the three things
 * `libs/api/README.md` says it owns: the api-version, the operation header, and error mapping.
 */
describe('HttpApiTransport — the seam the generated client leaves open', () => {
  let transport: HttpApiTransport;
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: API_BASE_PATH, useValue: '/api' }
      ]
    });

    transport = TestBed.inject(HttpApiTransport);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  it('sends every request under the base path with the api-version the client was generated at', async () => {
    const pending = transport.send<{ ok: true }>({
      method: 'GET',
      path: '/tenants/t/subscriptions/s',
      query: { $top: '5' }
    });

    const request = http.expectOne(r => r.url === '/api/tenants/t/subscriptions/s');
    expect(request.request.method).toBe('GET');
    expect(request.request.params.get('api-version')).toBe(apiVersion);
    expect(request.request.params.get('$top')).toBe('5');
    request.flush({ ok: true });

    const response = await pending;
    expect(response.status).toBe(200);
    expect(response.value).toEqual({ ok: true });
    expect(response.operationUrl).toBeUndefined();
  });

  it('surfaces the Azure-AsyncOperation header of a 202 as operationUrl', async () => {
    const pending = transport.send<void>({ method: 'PUT', path: '/x', body: { location: 'eu-central' } });

    const request = http.expectOne('/api/x?api-version=' + apiVersion);
    expect(request.request.body).toEqual({ location: 'eu-central' });
    request.flush(null, {
      status: 202,
      statusText: 'Accepted',
      headers: {
        'Azure-AsyncOperation': 'https://api.example/operations/abc-123?api-version=' + apiVersion,
        'Retry-After': '2'
      }
    });

    const response = await pending;
    expect(response.status).toBe(202);
    expect(response.operationUrl).toBe('https://api.example/operations/abc-123?api-version=' + apiVersion);
  });

  it('maps the platform error body to ApiCallError, pointer and all', async () => {
    const pending = transport.send<void>({ method: 'PUT', path: '/x', body: {} });

    http.expectOne('/api/x?api-version=' + apiVersion).flush(
      {
        error: {
          code: 'SchemaInvalid',
          message: 'The body was refused.',
          details: [{ code: 'SchemaInvalid', message: 'location is required.', target: '/location' }]
        }
      },
      { status: 400, statusText: 'Bad Request' }
    );

    await expect(pending).rejects.toBeInstanceOf(ApiCallError);

    try {
      await pending;
    } catch (error) {
      const failure = error as ApiCallError;
      expect(failure.status).toBe(400);
      expect(failure.error.code).toBe('SchemaInvalid');
      expect(failure.error.details?.[0].target).toBe('/location');
    }
  });

  it('gives a body that is not the platform’s the one error shape anyway', async () => {
    const pending = transport.send<void>({ method: 'GET', path: '/x' });

    http
      .expectOne('/api/x?api-version=' + apiVersion)
      .flush('<html>502</html>', { status: 502, statusText: 'Bad Gateway' });

    try {
      await pending;
      throw new Error('expected a rejection');
    } catch (error) {
      const failure = error as ApiCallError;
      expect(failure).toBeInstanceOf(ApiCallError);
      expect(failure.status).toBe(502);
      expect(failure.error.code).toBe('InternalError');
      expect(failure.error.message).toContain('502');
    }
  });
});

describe('operationIdOf — the id, never the URL', () => {
  it('takes the last segment of /operations/{id}', () => {
    expect(operationIdOf('https://api.example/operations/abc-123?api-version=2026-08-01')).toBe('abc-123');
    expect(operationIdOf('/operations/abc-123')).toBe('abc-123');
    expect(operationIdOf('https://api.example/operations/a%2Fb')).toBe('a/b');
  });

  it('refuses a URL with no operation in it', () => {
    expect(operationIdOf('https://evil.example/somewhere/else')).toBeNull();
    expect(operationIdOf('https://api.example/operations/')).toBeNull();
  });
});

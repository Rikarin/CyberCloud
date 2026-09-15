import { HttpClient, HttpErrorResponse, HttpResponse } from '@angular/common/http';
import { Injectable, InjectionToken, inject } from '@angular/core';
import { ApiRequest, ApiResponse, ApiTransport, CyberCloudError, apiVersion } from '@cybercloud/api';
import { firstValueFrom } from 'rxjs';

/**
 * Where the public REST API is mounted on this origin.
 *
 * ⚠ **Same-origin, and that is not a convenience.** `accessTokenInterceptor` attaches the bearer
 * token only to same-origin requests, so a base on another origin would send every call
 * unauthenticated — and docs/plan/03 § `portal/` gives `CyberCloud.Portal.Host` "the API shim +
 * static serving" for exactly this reason: the portal talks to the platform through its own origin.
 * The shim does not exist yet, so this default is the path it will take, stated once.
 */
export const API_BASE_PATH = new InjectionToken<string>('cc.api.basePath', {
  providedIn: 'root',
  factory: () => '/api'
});

/**
 * A failed call, with the platform's error body attached.
 *
 * docs/plan/08 § Errors gives every failure one shape, and `CyberCloudError.target` is a JSON
 * Pointer into the request body — which is what lets a form highlight the field the platform
 * refused rather than showing a banner. The transport keeps the shape intact so the form renderer
 * can do that; a transport that flattened it to a message would lose the pointer.
 */
export class ApiCallError extends Error {
  constructor(
    readonly status: number,
    readonly error: CyberCloudError
  ) {
    super(error.message);
    this.name = 'ApiCallError';
  }
}

/**
 * The one `ApiTransport` the portal supplies.
 *
 * `libs/api/README.md`: "`ApiTransport` is the seam and the portal fills it. Nothing generated here
 * opens a socket: the bearer token, the interceptors, the retry policy and the error mapping are
 * the app's, over Angular's `HttpClient`."
 *
 * What it does, and what each part is for:
 *
 * - **`api-version` on every request**, exactly as the .NET SDK's `ApiVersionHandler` does. The
 *   generated client deliberately leaves it off (`ApiRequest.query`'s own doc), so no call site can
 *   forget it and no two can disagree.
 * - **`Azure-AsyncOperation` read off a 202** and surfaced as `operationUrl`, which is how a create
 *   blade hands off to the operation view — docs/plan/10 § Long-running operations, over HTTP.
 * - **Errors mapped to `ApiCallError`** carrying the body's `error` member, so a `400` with a
 *   `target` reaches the form as a field message rather than as a thrown `HttpErrorResponse`.
 *
 * ⚠ **No retries here.** A `PUT` is not idempotent from the caller's side once the platform has
 * accepted it — a retried create is a second operation on the same name, and the first one is
 * still running. Retry policy belongs where the verb is known, not in a layer that sees every verb
 * as `send`.
 */
@Injectable({ providedIn: 'root' })
export class HttpApiTransport implements ApiTransport {
  private readonly http = inject(HttpClient);
  private readonly basePath = inject(API_BASE_PATH);

  async send<T>(request: ApiRequest): Promise<ApiResponse<T>> {
    const params: Record<string, string> = { ...(request.query ?? {}), 'api-version': apiVersion };

    let response: HttpResponse<T>;

    try {
      response = await firstValueFrom(
        this.http.request<T>(request.method, this.basePath + request.path, {
          body: request.body,
          params,
          observe: 'response'
        })
      );
    } catch (failure) {
      throw toApiCallError(failure);
    }

    const operationUrl = response.headers.get('Azure-AsyncOperation');

    return {
      status: response.status,
      // A 202 and a 204 have no body worth typing; the caller's type says what it expects.
      value: response.body as T,
      ...(operationUrl === null ? {} : { operationUrl })
    };
  }
}

/**
 * The operation id inside an `Azure-AsyncOperation` URL.
 *
 * The header is absolute — `{PublicBaseUri}/operations/{id}?api-version=…` — and the generated
 * `getOperation` takes the id alone. ⚠ Only the id is taken, never the URL: sending a
 * server-supplied URL through the transport would put an origin the server chose in front of the
 * user's bearer token. `null` when the URL has no `/operations/{id}` segment, which is a server that
 * is not this platform.
 */
export function operationIdOf(operationUrl: string): string | null {
  const match = /\/operations\/([^/?#]+)(?:[?#]|$)/.exec(operationUrl);
  return match === null ? null : decodeURIComponent(match[1]);
}

function toApiCallError(failure: unknown): Error {
  if (!(failure instanceof HttpErrorResponse)) {
    return failure instanceof Error ? failure : new Error(String(failure));
  }

  const body = failure.error as { error?: Partial<CyberCloudError> } | null | undefined;
  const error = body?.error;

  if (error !== undefined && error !== null && typeof error.message === 'string' && typeof error.code === 'string') {
    return new ApiCallError(failure.status, error as CyberCloudError);
  }

  // A body that is not the platform's — a proxy's HTML, an empty 502 — still gets the one shape,
  // with a code that says so. `InternalError` is the document's own catch-all.
  return new ApiCallError(failure.status, {
    code: 'InternalError',
    message:
      failure.status === 0
        ? $localize`:@@api.unreachable:The platform could not be reached.`
        : $localize`:@@api.unexpected:The platform answered ${failure.status}:status: without an error body.`
  });
}

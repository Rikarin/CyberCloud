import { base64Url } from '../pkce';

/**
 * A JWT with the given payload and a signature nobody checks — what the identity host's tokens
 * look like to the portal, which reads the payload and never verifies it (`decodeJwtPayload`).
 */
export function unsignedJwt(payload: Readonly<Record<string, unknown>>): string {
  const encode = (value: unknown): string => base64Url(new TextEncoder().encode(JSON.stringify(value)));
  return `${encode({ alg: 'ES256', typ: 'JWT' })}.${encode(payload)}.${base64Url(new Uint8Array([1, 2, 3]))}`;
}

/** One request the fake `/token` saw, decoded. */
export interface TokenCall {
  readonly url: string;
  readonly init: RequestInit;
  readonly form: URLSearchParams;
}

/** How the fake answers one call. */
export type TokenAnswer = { readonly status: number; readonly body: unknown } | 'unreachable';

/**
 * A `fetch` that plays the identity host's `/token`, installed on `globalThis`.
 *
 * jsdom has no `fetch`, and the shell calls the global one on purpose — see `postTokenRequest`
 * for why it is neither `HttpClient` nor proxied — so this is the seam a suite has. It records
 * every call, form-decoded, and answers from a queue: the first answer for the first call, and
 * the last answer for every call past the queue's end. `restore()` puts the global back.
 */
export class FakeTokenEndpoint {
  readonly calls: TokenCall[] = [];
  private readonly queue: TokenAnswer[];
  private readonly previous: typeof globalThis.fetch | undefined;

  /** Calls the fake is answering right now, resolved by `release()`. */
  private pending: (() => void)[] = [];
  private held = false;

  constructor(...answers: TokenAnswer[]) {
    this.queue = answers;
    this.previous = globalThis.fetch;
    globalThis.fetch = (input, init) => this.handle(String(input), init ?? {});
  }

  /** Makes every call wait until `release()` — for the suites that assert two callers share one. */
  hold(): void {
    this.held = true;
  }

  release(): void {
    this.held = false;
    const waiting = this.pending;
    this.pending = [];
    for (const resume of waiting) resume();
  }

  restore(): void {
    if (this.previous === undefined) {
      delete (globalThis as { fetch?: typeof globalThis.fetch }).fetch;
    } else {
      globalThis.fetch = this.previous;
    }
  }

  private async handle(url: string, init: RequestInit): Promise<Response> {
    const form = new URLSearchParams(typeof init.body === 'string' ? init.body : '');
    this.calls.push({ url, init, form });

    if (this.held) await new Promise<void>(resolve => this.pending.push(resolve));

    const answer = this.queue[Math.min(this.calls.length, this.queue.length) - 1];
    if (answer === undefined) throw new Error(`FakeTokenEndpoint has no answer for call ${this.calls.length}`);
    if (answer === 'unreachable') throw new TypeError('Failed to fetch');

    // jsdom has no `Response` either; `postTokenRequest` reads `ok`, `status` and `json()`.
    const body = answer.body;
    return {
      ok: answer.status >= 200 && answer.status < 300,
      status: answer.status,
      json: () => Promise.resolve(body)
    } as Response;
  }
}

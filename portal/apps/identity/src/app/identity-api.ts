import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';

/**
 * A credential the user may present, in the order the server offered them.
 *
 * ⚠ The names match `CyberCloud.Identity.Contracts.CredentialKind`'s casing exactly, and the casing
 * is load-bearing rather than cosmetic — the server compares them ordinally. docs/plan/11's
 * `servicePrincipal`-versus-`serviceprincipal` trap is the same shape of bug one layer down: a
 * one-character casing difference produces a value no branch matches, and it surfaces as "the
 * passkey button never appears" rather than as an error.
 */
export type CredentialKind =
  | 'passkey'
  | 'password'
  | 'totp'
  | 'recoveryCode'
  | 'emailOtp'
  | 'smsOtp'
  | 'whatsAppOtp'
  | 'certificate';

/** What `POST /api/signin/begin` answers. */
export interface SignInBeginResponse {
  /**
   * The credential kinds to offer, passkey first.
   *
   * ⚠ **Identical for an address with no account.** docs/plan/11 § Credentials: sign-in "returns
   * the same response and takes the same time whether or not the account exists". A page that
   * rendered a different set for an unknown address would enumerate the tenant's users on the
   * platform's behalf, which is exactly the work the server's uniform response avoids.
   */
  offered: CredentialKind[];
}

/** What `POST /api/signin/passkey/begin` answers. */
export interface PasskeyBeginResponse {
  /**
   * The WebAuthn request options, to hand to `navigator.credentials.get()`.
   *
   * ⚠ **Passed through without being parsed or rebuilt.** The challenge binding is the server
   * library's, and reserializing the options breaks it.
   *
   * ⚠ **The challenge itself is not in here to be sent back.** The server keeps its own copy in a
   * protected, HttpOnly cookie and verifies the assertion against that — so this app cannot supply
   * a challenge of its own even by accident, which is the whole security property of the exchange.
   *
   * An empty string means the server could not build one, which is a relying-party
   * misconfiguration and never an answer about the address.
   */
  optionsJson: string;
}

/** What the credential endpoints answer. */
export interface SignInResultResponse {
  /** Whether the caller is now authenticated. */
  succeeded: boolean;

  /** Whether a second factor is still owed before the session may be used. */
  secondFactorRequired: boolean;

  /**
   * Where to go next — always a same-origin path, already sanitized by the server.
   *
   * ⚠ Sanitized again on this side before it is used. The two checks guard different things: the
   * server's guards the value it emits, this one guards a navigation the server never sees.
   */
  returnUrl: string;

  /**
   * The message to render on failure, verbatim.
   *
   * ⚠ **Render it as it arrives.** It is `UniformFailures.SignIn` for every reason a sign-in can
   * fail, and a page that helpfully translated it into "no account with that address" would undo
   * the enumeration hardening the endpoint pays a dummy Argon2id hash for.
   */
  message: string;
}

/**
 * The identity host's JSON endpoints, as this app calls them.
 *
 * ⚠ **Every path is relative and every call is same-origin.** docs/plan/11 § Hosts puts the cookie
 * on this origin and the bearer token at the gateway; a call from here to the gateway would either
 * fail CORS or, worse, succeed and start the drift that makes a session cookie an API credential.
 *
 * ⚠ **No call happens during server-side rendering.** Angular serializes transfer state into the
 * rendered document, so a request resolved on the server ships its response inside the HTML. These
 * are all invoked from event handlers, which only run after hydration.
 */
@Injectable({ providedIn: 'root' })
export class IdentityApi {
  readonly #http = inject(HttpClient);

  /**
   * Asks which credentials to offer for an address.
   *
   * @param email The address typed. Sent as-is; the server normalizes it.
   */
  begin(email: string): Observable<SignInBeginResponse> {
    return this.#http.post<SignInBeginResponse>('/api/signin/begin', { email });
  }

  /**
   * Signs in with a password.
   *
   * ⚠ The password travels in the request **body**, never in a query string. A query string lands
   * in the server's access log, in the browser's history, and in the `Referer` of the next
   * navigation — three places docs/plan/00 § Non-negotiables' "secrets are handles" discipline says
   * a credential must never be.
   */
  signInWithPassword(
    email: string,
    password: string,
    returnUrl: string,
  ): Observable<SignInResultResponse> {
    return this.#http.post<SignInResultResponse>('/api/signin/password', {
      email,
      password,
      returnUrl,
    });
  }

  /**
   * Starts a self-serve sign-up.
   *
   * @returns Always the same shape, whether or not the address was free — docs/plan/11 § Sign-up
   * and tenant creation. The mail that follows is what differs, and it goes to the address either
   * way.
   */
  signUp(email: string, returnUrl: string): Observable<SignInResultResponse> {
    return this.#http.post<SignInResultResponse>('/api/signup', { email, returnUrl });
  }

  /**
   * Asks for a WebAuthn assertion challenge.
   *
   * ⚠ Answers with a challenge of the same shape for an address with no account — the server builds
   * a discoverable-credential ("usernameless") one, which is what a real usernameless sign-in looks
   * like anyway. A refusal here would enumerate the tenant from an endpoint that needs no password
   * guess.
   *
   * @param email The address typed. Sent as-is; the server normalizes it.
   */
  beginPasskey(email: string): Observable<PasskeyBeginResponse> {
    return this.#http.post<PasskeyBeginResponse>('/api/signin/passkey/begin', { email });
  }

  /**
   * Posts the authenticator's response.
   *
   * ⚠ **`assertionJson` is the browser's result serialized verbatim**, and the challenge is
   * deliberately absent — see `PasskeyBeginResponse.optionsJson`. `withCredentials` is not set
   * because every call here is same-origin, which is what carries the challenge cookie.
   *
   * @param assertionJson The `navigator.credentials.get()` result, encoded by `passkey.ts`.
   * @param returnUrl Where to go afterwards.
   */
  completePasskey(assertionJson: string, returnUrl: string): Observable<SignInResultResponse> {
    return this.#http.post<SignInResultResponse>('/api/signin/passkey/complete', {
      assertionJson,
      returnUrl,
    });
  }

  /**
   * Presents a TOTP code as the second factor.
   *
   * ⚠ Who is answering comes from the session cookie the first factor set, never from anything this
   * app sends — a user id in the body would let anybody holding a pending session name somebody
   * else's account.
   *
   * @param code The six digits typed.
   * @param returnUrl Where to go afterwards.
   */
  verifyTotp(code: string, returnUrl: string): Observable<SignInResultResponse> {
    return this.#http.post<SignInResultResponse>('/api/signin/totp', { code, returnUrl });
  }

  /**
   * Redeems a recovery code as the second factor.
   *
   * ⚠ Single-use, and burning one is an auditable event on the server — docs/plan/11 § Credentials.
   *
   * @param code The code typed.
   * @param returnUrl Where to go afterwards.
   */
  redeemRecoveryCode(code: string, returnUrl: string): Observable<SignInResultResponse> {
    return this.#http.post<SignInResultResponse>('/api/signin/recovery-code', { code, returnUrl });
  }
}

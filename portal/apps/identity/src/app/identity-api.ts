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
  'passkey' | 'password' | 'totp' | 'recoveryCode' | 'emailOtp' | 'smsOtp' | 'whatsAppOtp' | 'certificate';

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

/** What `POST /api/signup/begin` answers — the same thing for every address. */
export interface SignUpBeginResponse {
  /** Always `true`. A malformed address, a taken one and a free one all produce it. */
  sent: true;

  /** Where to go afterwards — a same-origin path, already sanitized by the server. */
  returnUrl: string;
}

/** What `POST /api/signup/verify` answers. */
export interface SignUpVerifyResponse {
  /** Whether the address is now proven. One `false` for every way of being wrong. */
  verified: boolean;
}

/**
 * The credential a sign-up completes with — one of two shapes.
 *
 * ⚠ For a passkey the challenge it answers is in the server's cookie, never here; for a password
 * the secret travels in the body, never in a query string — `signInWithPassword` says why.
 */
export type SignUpCredential = { kind: 'passkey'; attestationJson: string } | { kind: 'password'; password: string };

/** What `POST /api/signup/complete` answers. */
export interface SignUpCompleteResponse {
  /** Whether the tenant exists and the caller is signed into it. */
  succeeded: boolean;

  /** The new tenant, or empty on failure. */
  tenantId: string;

  /**
   * Where to go next — on success, the original `/authorize` request with `tenant=<new tenant>`
   * set. ⚠ Sanitized again on this side inside `NAVIGATE`, for the reason `SignInResultResponse`
   * gives.
   */
  returnUrl: string;

  /**
   * What to render on failure, verbatim.
   *
   * ⚠ Distinguishable, and rendered as it arrives — "verify your address first", "that organisation
   * name is taken", the naming rule's own sentence, the password rule's own sentence, or
   * "something went wrong". By this point the person has proven an address and the answers are
   * about their own input, which is why this surface may say more than the sign-in surface does.
   */
  message: string;
}

/**
 * What every `/api/signup/*` call answers while sign-up is closed on a deployment — a body, not a
 * status, so a page renders one sentence and a probe learns nothing.
 */
export interface SignUpClosedResponse {
  succeeded: false;
  message: string;
}

/** Whether an answer is the closed-surface shape rather than the endpoint's own. */
export function signUpIsClosed(response: object): response is SignUpClosedResponse {
  return 'succeeded' in response && response.succeeded === false && 'message' in response;
}

/** What `GET /api/consent` answers — what the consent page renders, and nothing more. */
export interface ConsentPageResponse {
  /** Whether there is a request to consent to. When false, `message` says why. */
  ready: boolean;

  /**
   * The client's display name, from its registration.
   *
   * ⚠ **Never from the `/authorize` query string.** That string is a link anybody can send, and a
   * page that rendered a name out of it would let a phisher call their client "Cyber Cloud portal".
   * The server resolves the name from the registration and this page renders what it is handed.
   */
  clientName: string;

  /** The scopes the request asks for, cut to what the client may have. */
  scopes: string[];

  /**
   * The `/authorize` request the page posts its answer to — a same-origin path, sanitized by the
   * server and sanitized again here before it becomes a form action.
   */
  returnUrl: string;

  /** What to render when `ready` is false, verbatim. */
  message: string;
}

/**
 * What `POST /api/device/lookup` and `POST /api/device/decision` answer — what the device page
 * renders next. RFC 8628 § 3.3, #43.
 */
export interface DevicePageResponse {
  /** Whether a device sign-in is waiting for this code. When false, `message` says why. */
  found: boolean;

  /** The code as a person reads it — `BCDF-GHJK`. */
  userCode: string;

  /**
   * The client's registered display name.
   *
   * ⚠ **Never from the query.** The device that asked is whoever ran the command; a name it chose
   * would be a phisher's. The server resolves the registration and this page renders what it gets.
   */
  clientName: string;

  /** What the device asked for. */
  scopes: string[];

  /** Whether this browser holds a complete sign-in. When false the page sends the person to sign in. */
  signedIn: boolean;

  /** The address the person is signed in as — the account the device will act as. */
  account: string;

  /** `pending`, `approved` or `denied`. */
  status: 'pending' | 'approved' | 'denied' | '';

  /** What to render, verbatim. */
  message: string;
}

/** The three parts of an invitation link, as the page read them off its query. */
export interface InvitationLink {
  tenant: string;
  invitation: string;
  token: string;
}

/** What `POST /api/invitations/describe` and `POST /api/invitations/accept` answer (#43). */
export interface InvitationPageResponse {
  /** Whether the link names an invitation. When false, `message` says why. */
  found: boolean;

  /** The address invited — the account being created. */
  email: string;

  /** The organisation, by its short name. */
  tenantName: string;

  /** `pending`, `accepted` or `expired`. */
  status: 'pending' | 'accepted' | 'expired' | '';

  /** Whether an accept made the person a member and signed them in. */
  succeeded: boolean;

  /** Where the portal is, from its registration — only after an accept. */
  portalUrl: string;

  /** What to render, verbatim. */
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
   * @param tenant The tenant to sign into — see {@link signInWithPassword}. Carried here so the
   * three first-factor calls send one shape; the server reads nothing from this endpoint's body.
   */
  begin(email: string, tenant?: string): Observable<SignInBeginResponse> {
    return this.#http.post<SignInBeginResponse>('/api/signin/begin', { email, tenant });
  }

  /**
   * Signs in with a password.
   *
   * ⚠ The password travels in the request **body**, never in a query string. A query string lands
   * in the server's access log, in the browser's history, and in the `Referer` of the next
   * navigation — three places docs/plan/00 § Non-negotiables' "secrets are handles" discipline says
   * a credential must never be.
   *
   * ⚠ **The tenant names where to sign in, and an address alone names nothing.** docs/plan/11
   * § Sign-up and tenant creation refuses a global email index, so the same address may exist in
   * two tenants and the server has to be told which. It arrives on the `/authorize` request that
   * sent the person here (`tenant=<id or slug>` in the return URL) or is typed as the organisation;
   * `undefined` leaves it to the server's fallback. An unknown value is the uniform failure — the
   * server looks it up in the platform directory and touches nothing in a tenant that does not
   * exist.
   *
   * @param tenant A tenant id or slug, or `undefined`.
   */
  signInWithPassword(
    email: string,
    password: string,
    returnUrl: string,
    tenant?: string
  ): Observable<SignInResultResponse> {
    return this.#http.post<SignInResultResponse>('/api/signin/password', {
      email,
      password,
      returnUrl,
      tenant
    });
  }

  /**
   * Starts a self-serve sign-up — step one of three. docs/plan/11 § Sign-up and tenant creation.
   *
   * ⚠ Answers `sent: true` whether the address was free, taken or not an address at all, on the
   * server's timing floor, and sets the `__Host-cyc-signup` ticket cookie this app cannot read.
   * Everything that follows is authenticated by that cookie: the browser that began a sign-up is
   * the only thing that can continue it. While sign-up is closed on a deployment the answer is
   * `{ succeeded: false, message }` instead, which `signUpIsClosed` recognises.
   */
  signUpBegin(email: string, returnUrl: string): Observable<SignUpBeginResponse | SignUpClosedResponse> {
    return this.#http.post<SignUpBeginResponse | SignUpClosedResponse>('/api/signup/begin', { email, returnUrl });
  }

  /**
   * Answers the enrolment code — step two.
   *
   * ⚠ Which sign-up is answering comes from the ticket cookie, never from this body. A wrong code,
   * an expired one and one whose five guesses are spent are one `false`.
   */
  signUpVerify(code: string): Observable<SignUpVerifyResponse | SignUpClosedResponse> {
    return this.#http.post<SignUpVerifyResponse | SignUpClosedResponse>('/api/signup/verify', { code });
  }

  /**
   * Asks for a WebAuthn registration challenge — the passkey half of step three.
   *
   * ⚠ The challenge goes into the same protected cookie the sign-in ceremony uses, stamped as a
   * registration so neither endpoint can answer the other's. The options are handed to
   * `navigator.credentials.create()` without being rebuilt — `passkey.ts`.
   */
  signUpPasskeyBegin(displayName: string): Observable<PasskeyBeginResponse | SignUpClosedResponse> {
    return this.#http.post<PasskeyBeginResponse | SignUpClosedResponse>('/api/signup/passkey/begin', { displayName });
  }

  /**
   * Creates everything — step three's last call.
   *
   * ⚠ The address, the ids and the proof come from the ticket and the server's own record, never
   * from here: this body carries what the person chose and their credential, and nothing else.
   * On `succeeded` the response's `returnUrl` is the original `/authorize` request with
   * `tenant=<new tenant>` set, and the page leaves for it through `NAVIGATE`.
   */
  signUpComplete(
    displayName: string,
    organizationName: string,
    credential: SignUpCredential,
    returnUrl: string
  ): Observable<SignUpCompleteResponse> {
    return this.#http.post<SignUpCompleteResponse>('/api/signup/complete', {
      displayName,
      organizationName,
      credential,
      returnUrl
    });
  }

  /**
   * Looks a device's user code up — the device page's first step (#43).
   *
   * ⚠ Anonymous and metered: the person types the code before signing in, so the server counts
   * every lookup per IP with every other route that takes a code, and answers a wrong code, an
   * expired one and a guess with one sentence.
   *
   * @param userCode The code as typed. The server normalizes case, the dash and spaces.
   */
  lookupDevice(userCode: string): Observable<DevicePageResponse> {
    return this.#http.post<DevicePageResponse>('/api/device/lookup', { userCode });
  }

  /**
   * Answers a device's sign-in — allow or deny.
   *
   * ⚠ Who is answering, in which tenant and how they signed in all come from the session cookie,
   * never from this body; and the server takes the answer only from this origin, so a page elsewhere
   * cannot approve a device with the person's cookie.
   */
  decideDevice(userCode: string, decision: 'allow' | 'deny'): Observable<DevicePageResponse> {
    return this.#http.post<DevicePageResponse>('/api/device/decision', { userCode, decision });
  }

  /**
   * Describes an invitation link (#43). Anonymous and metered per IP, as every route that takes a
   * code is; a wrong link of any kind is one sentence.
   */
  describeInvitation(link: InvitationLink): Observable<InvitationPageResponse> {
    return this.#http.post<InvitationPageResponse>('/api/invitations/describe', link);
  }

  /**
   * Accepts an invitation: the person's name and a password for this organisation.
   *
   * ⚠ The password travels in the body, never a query string — `signInWithPassword` says why. On
   * `succeeded` the host has set the session cookie; the link is spent either way once accepted.
   */
  acceptInvitation(link: InvitationLink, displayName: string, password: string): Observable<InvitationPageResponse> {
    return this.#http.post<InvitationPageResponse>('/api/invitations/accept', { ...link, displayName, password });
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
   * @param tenant The tenant to sign into — see {@link signInWithPassword}. The server carries it in
   * the challenge ticket, so `completePasskey` needs none.
   */
  beginPasskey(email: string, tenant?: string): Observable<PasskeyBeginResponse> {
    return this.#http.post<PasskeyBeginResponse>('/api/signin/passkey/begin', { email, tenant });
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
      returnUrl
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

  /**
   * Asks what the consent page should render for the `/authorize` request it was sent with.
   *
   * ⚠ Read-only, and the person's answer does not go through this client at all: the page posts
   * `consent=allow` or `consent=deny` back to `/authorize` as a full-page form, with the request's
   * own parameters, so the server answers the client in the response mode it asked for. A `fetch`
   * would swallow that redirect.
   *
   * @param returnUrl The `/authorize` path and query, already sanitized.
   */
  describeConsent(returnUrl: string): Observable<ConsentPageResponse> {
    return this.#http.get<ConsentPageResponse>('/api/consent', { params: { returnUrl } });
  }
}

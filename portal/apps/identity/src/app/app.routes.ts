import { Routes } from '@angular/router';

/**
 * The four pages docs/plan/11 § Effort names, of which three are built — plus the two #43 added:
 * the device page RFC 8628 needs for `cyc login`, and the page an invitation link opens.
 *
 * ⚠ All three are lazily loaded even though there are only three. The reason is the bundle rather
 * than the count: a user who lands on `/signin` should not download the sign-up page's markup, and
 * wiring the split in now means adding `/reset` later does not require revisiting it.
 */
export const appRoutes: Routes = [
  {
    path: 'signin',
    loadComponent: () => import('./pages/sign-in').then(m => m.SignInPage),
    title: 'Sign in — Cyber Cloud'
  },
  {
    path: 'signup',
    loadComponent: () => import('./pages/sign-up').then(m => m.SignUpPage),
    title: 'Create an account — Cyber Cloud'
  },
  {
    path: 'consent',
    loadComponent: () => import('./pages/consent').then(m => m.ConsentPage),
    title: 'Allow access — Cyber Cloud'
  },
  // #43: the verification page for `cyc login` on a headless box. ⚠ Not `/device` — that path is
  // the identity host's device authorization endpoint, which claims every method on it.
  // #43: where an invitation mail's link lands.
  {
    path: 'invitation',
    loadComponent: () => import('./pages/invitation').then(m => m.InvitationPage),
    title: 'Join an organisation — Cyber Cloud'
  },
  {
    path: 'device-code',
    loadComponent: () => import('./pages/device-code').then(m => m.DeviceCodePage),
    title: 'Sign in a device — Cyber Cloud'
  },
  // ⚠ `/reset` is owed. docs/plan/11 § Effort scopes all four pages together at 0.8 EM; #88 built
  // sign-in and sign-up and #94 the consent page. A request for it falls through to `/signin`
  // rather than to a 404, because arriving at a dead page mid-flow is worse than arriving at the
  // one page every flow starts from.
  { path: '', pathMatch: 'full', redirectTo: 'signin' },
  { path: '**', redirectTo: 'signin' }
];

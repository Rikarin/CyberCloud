import { Routes } from '@angular/router';

/**
 * The four pages docs/plan/11 § Effort names, of which three are built.
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
  // ⚠ `/reset` is owed. docs/plan/11 § Effort scopes all four pages together at 0.8 EM; #88 built
  // sign-in and sign-up and #94 the consent page. A request for it falls through to `/signin`
  // rather than to a 404, because arriving at a dead page mid-flow is worse than arriving at the
  // one page every flow starts from.
  { path: '', pathMatch: 'full', redirectTo: 'signin' },
  { path: '**', redirectTo: 'signin' }
];

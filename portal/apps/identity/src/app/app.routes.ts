import { Routes } from '@angular/router';

/**
 * The four pages docs/plan/11 § Effort names, of which two are built.
 *
 * ⚠ Both are lazily loaded even though there are only two. The reason is the bundle rather than the
 * count: a user who lands on `/signin` should not download the sign-up page's markup, and wiring
 * the split in now means adding `/reset` and `/consent` later does not require revisiting it.
 */
export const appRoutes: Routes = [
  {
    path: 'signin',
    loadComponent: () => import('./pages/sign-in').then((m) => m.SignInPage),
    title: 'Sign in — Cyber Cloud',
  },
  {
    path: 'signup',
    loadComponent: () => import('./pages/sign-up').then((m) => m.SignUpPage),
    title: 'Create an account — Cyber Cloud',
  },
  // ⚠ `/reset` and `/consent` are owed. docs/plan/11 § Effort scopes all four pages together at
  // 0.8 EM; this task built sign-in and sign-up. A request for either falls through to `/signin`
  // rather than to a 404, because arriving at a dead page mid-flow is worse than arriving at the
  // one page every flow starts from.
  { path: '', pathMatch: 'full', redirectTo: 'signin' },
  { path: '**', redirectTo: 'signin' },
];

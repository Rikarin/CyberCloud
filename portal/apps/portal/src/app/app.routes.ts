import { Routes } from '@angular/router';
import { authGuard } from '@cybercloud/shell';

/**
 * ⚠ **Every route is lazy.** docs/plan/20 § Performance budget: "Route-level code splitting is
 * mandatory, and with 100 resource types the generated form renderer must not pull every schema
 * into the main bundle."
 *
 * `loadComponent` is what makes that true. An eager `component:` here would put the route's whole
 * import graph — and, for a resource route, the form renderer and every schema it reaches — into
 * the initial bundle, where `scripts/bundle-budget.mjs` would then fail the build. That failure is
 * the point: the budget is a gate, not a goal.
 *
 * **The paths mirror docs/plan/06 § Identifiers**, minus the tenant (which comes from the token —
 * see `portal-links.ts`): `subscriptions/{s}/resourceGroups/{g}/providers/{ns}/{type}/{name}`.
 * A deep link is therefore a resource id with the prefix swapped, which is what a link from an
 * alert email needs.
 *
 * The resource routes are `:provider/:type/:name` rather than one route per resource type, because
 * a hundred route definitions is a hundred chunks in the router's own table. The type is data the
 * blade resolves a schema from, not structure. A child type — a node pool under a cluster — is the
 * same blade with two more segments, `:parent/:childType`, which is the deepest nesting the
 * api-version declares.
 *
 * ⚠ The four resource routes are told apart by segment count alone — three, four, five and six
 * segments after `providers/` — so `…/{name}/edit` cannot be read as a child named `edit`. A
 * second nesting level would break that, and would need a literal segment to disambiguate.
 * `…/{name}/access` sits beside `…/{name}/edit` at four and six segments on the same argument,
 * and the router tells the two literals apart.
 *
 * **The access page is a `/access` suffix on every scope that has a blade** — the subscription,
 * the resource group, and the resource — because a role assignment is an extension address on
 * every scope (docs/plan/10 § Shape) and the page is the same page with a different scope.
 *
 * ⚠ **`authGuard` on every route but `auth/callback`.** docs/plan/10 § Authentication inputs:
 * the portal holds an access token in memory and nothing else, so a route reached with none
 * either refreshes from the identity host's cookie or leaves for `/authorize`. The callback is
 * the one route that runs *without* a token by definition — it is where the token comes from —
 * and a guard on it would send the person back to sign in from the page that completes the
 * sign-in. The `**` route is guarded too: an unknown address should not tell an anonymous
 * visitor what the portal looks like.
 */
export const appRoutes: Routes = [
  {
    path: 'auth/callback',
    loadComponent: () => import('../pages/auth/callback').then(m => m.AuthCallback),
    title: 'Signing in'
  },
  {
    path: '',
    pathMatch: 'full',
    canActivate: [authGuard],
    loadComponent: () => import('../pages/home/home').then(m => m.Home),
    title: 'Cyber Cloud'
  },
  {
    path: 'subscriptions',
    canActivate: [authGuard],
    loadComponent: () => import('../pages/scopes/subscriptions').then(m => m.Subscriptions),
    title: 'Subscriptions'
  },
  {
    path: 'subscriptions/:subscriptionId',
    canActivate: [authGuard],
    loadComponent: () => import('../pages/scopes/subscription-blade').then(m => m.SubscriptionBlade),
    title: 'Subscription'
  },
  {
    path: 'subscriptions/:subscriptionId/access',
    canActivate: [authGuard],
    loadComponent: () => import('../pages/access/access-blade').then(m => m.AccessBlade),
    title: 'Access'
  },
  {
    path: 'subscriptions/:subscriptionId/resourceGroups',
    canActivate: [authGuard],
    loadComponent: () => import('../pages/scopes/resource-groups').then(m => m.ResourceGroups),
    title: 'Resource groups'
  },
  {
    path: 'subscriptions/:subscriptionId/resourceGroups/:resourceGroup',
    canActivate: [authGuard],
    loadComponent: () => import('../pages/scopes/resource-group-blade').then(m => m.ResourceGroupBlade),
    title: 'Resource group'
  },
  {
    path: 'subscriptions/:subscriptionId/resourceGroups/:resourceGroup/access',
    canActivate: [authGuard],
    loadComponent: () => import('../pages/access/access-blade').then(m => m.AccessBlade),
    title: 'Access'
  },
  {
    // The cloud shell — a literal segment beside `resources`, `create` and `access`, so a
    // provider named `terminal` cannot shadow it: a resource path always has `providers/` next.
    path: 'subscriptions/:subscriptionId/resourceGroups/:resourceGroup/terminal',
    canActivate: [authGuard],
    loadComponent: () => import('../pages/terminal/terminal-blade').then(m => m.TerminalBlade),
    title: 'Cloud shell'
  },
  {
    path: 'subscriptions/:subscriptionId/resourceGroups/:resourceGroup/resources',
    canActivate: [authGuard],
    loadComponent: () => import('../pages/resources/resource-list').then(m => m.ResourceList),
    title: 'Resources'
  },
  {
    path: 'subscriptions/:subscriptionId/resourceGroups/:resourceGroup/create/:provider/:type',
    canActivate: [authGuard],
    loadComponent: () => import('../pages/resources/resource-create').then(m => m.ResourceCreate),
    title: 'Create'
  },
  {
    path: 'subscriptions/:subscriptionId/resourceGroups/:resourceGroup/create/:provider/:type/:childType',
    canActivate: [authGuard],
    loadComponent: () => import('../pages/resources/resource-create').then(m => m.ResourceCreate),
    title: 'Create'
  },
  {
    // One route for every resource type. The blade reads the schema for (type, apiVersion) at
    // runtime — docs/plan/20 § Performance budget: "Schemas are fetched per type, cached, and
    // versioned by the api-version".
    path: 'subscriptions/:subscriptionId/resourceGroups/:resourceGroup/providers/:provider/:type/:name',
    canActivate: [authGuard],
    loadComponent: () => import('../pages/resources/resource-blade').then(m => m.ResourceBlade)
  },
  {
    path: 'subscriptions/:subscriptionId/resourceGroups/:resourceGroup/providers/:provider/:type/:name/edit',
    canActivate: [authGuard],
    loadComponent: () => import('../pages/resources/resource-edit').then(m => m.ResourceEdit),
    title: 'Edit'
  },
  {
    path: 'subscriptions/:subscriptionId/resourceGroups/:resourceGroup/providers/:provider/:type/:name/access',
    canActivate: [authGuard],
    loadComponent: () => import('../pages/access/access-blade').then(m => m.AccessBlade),
    title: 'Access'
  },
  {
    path: 'subscriptions/:subscriptionId/resourceGroups/:resourceGroup/providers/:provider/:type/:parent/:childType/:name',
    canActivate: [authGuard],
    loadComponent: () => import('../pages/resources/resource-blade').then(m => m.ResourceBlade)
  },
  {
    path: 'subscriptions/:subscriptionId/resourceGroups/:resourceGroup/providers/:provider/:type/:parent/:childType/:name/edit',
    canActivate: [authGuard],
    loadComponent: () => import('../pages/resources/resource-edit').then(m => m.ResourceEdit),
    title: 'Edit'
  },
  {
    path: 'subscriptions/:subscriptionId/resourceGroups/:resourceGroup/providers/:provider/:type/:parent/:childType/:name/access',
    canActivate: [authGuard],
    loadComponent: () => import('../pages/access/access-blade').then(m => m.AccessBlade),
    title: 'Access'
  },
  {
    // The identity administration area (#41): tenant-wide, so beside `subscriptions` rather than
    // under one. The three share a tab strip, not a route parameter.
    path: 'identity/members',
    canActivate: [authGuard],
    loadComponent: () => import('../pages/identity/members').then(m => m.IdentityMembers),
    title: 'Members'
  },
  {
    path: 'identity/applications',
    canActivate: [authGuard],
    loadComponent: () => import('../pages/identity/applications').then(m => m.IdentityApplications),
    title: 'Applications'
  },
  {
    path: 'identity/sessions',
    canActivate: [authGuard],
    loadComponent: () => import('../pages/identity/sessions').then(m => m.IdentitySessions),
    title: 'My sessions'
  },
  {
    path: 'operations/:operationId',
    canActivate: [authGuard],
    loadComponent: () => import('../pages/operations/operation-view').then(m => m.OperationView),
    title: 'Operation'
  },
  {
    path: '**',
    canActivate: [authGuard],
    loadComponent: () => import('../pages/not-found/not-found').then(m => m.NotFound),
    title: 'Not found'
  }
];

import { Routes } from '@angular/router';

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
 */
export const appRoutes: Routes = [
  {
    path: '',
    pathMatch: 'full',
    loadComponent: () => import('../pages/home/home').then(m => m.Home),
    title: 'Cyber Cloud'
  },
  {
    path: 'subscriptions',
    loadComponent: () => import('../pages/scopes/subscriptions').then(m => m.Subscriptions),
    title: 'Subscriptions'
  },
  {
    path: 'subscriptions/:subscriptionId',
    loadComponent: () => import('../pages/scopes/subscription-blade').then(m => m.SubscriptionBlade),
    title: 'Subscription'
  },
  {
    path: 'subscriptions/:subscriptionId/resourceGroups',
    loadComponent: () => import('../pages/scopes/resource-groups').then(m => m.ResourceGroups),
    title: 'Resource groups'
  },
  {
    path: 'subscriptions/:subscriptionId/resourceGroups/:resourceGroup',
    loadComponent: () => import('../pages/scopes/resource-group-blade').then(m => m.ResourceGroupBlade),
    title: 'Resource group'
  },
  {
    path: 'subscriptions/:subscriptionId/resourceGroups/:resourceGroup/resources',
    loadComponent: () => import('../pages/resources/resource-list').then(m => m.ResourceList),
    title: 'Resources'
  },
  {
    path: 'subscriptions/:subscriptionId/resourceGroups/:resourceGroup/create/:provider/:type',
    loadComponent: () => import('../pages/resources/resource-create').then(m => m.ResourceCreate),
    title: 'Create'
  },
  {
    path: 'subscriptions/:subscriptionId/resourceGroups/:resourceGroup/create/:provider/:type/:childType',
    loadComponent: () => import('../pages/resources/resource-create').then(m => m.ResourceCreate),
    title: 'Create'
  },
  {
    // One route for every resource type. The blade reads the schema for (type, apiVersion) at
    // runtime — docs/plan/20 § Performance budget: "Schemas are fetched per type, cached, and
    // versioned by the api-version".
    path: 'subscriptions/:subscriptionId/resourceGroups/:resourceGroup/providers/:provider/:type/:name',
    loadComponent: () => import('../pages/resources/resource-blade').then(m => m.ResourceBlade)
  },
  {
    path: 'subscriptions/:subscriptionId/resourceGroups/:resourceGroup/providers/:provider/:type/:name/edit',
    loadComponent: () => import('../pages/resources/resource-edit').then(m => m.ResourceEdit),
    title: 'Edit'
  },
  {
    path: 'subscriptions/:subscriptionId/resourceGroups/:resourceGroup/providers/:provider/:type/:parent/:childType/:name',
    loadComponent: () => import('../pages/resources/resource-blade').then(m => m.ResourceBlade)
  },
  {
    path: 'subscriptions/:subscriptionId/resourceGroups/:resourceGroup/providers/:provider/:type/:parent/:childType/:name/edit',
    loadComponent: () => import('../pages/resources/resource-edit').then(m => m.ResourceEdit),
    title: 'Edit'
  },
  {
    path: 'operations/:operationId',
    loadComponent: () => import('../pages/operations/operation-view').then(m => m.OperationView),
    title: 'Operation'
  },
  {
    path: '**',
    loadComponent: () => import('../pages/not-found/not-found').then(m => m.NotFound),
    title: 'Not found'
  }
];

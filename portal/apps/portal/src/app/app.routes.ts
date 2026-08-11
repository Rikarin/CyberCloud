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
 * The resource route is `:provider/:type/:name` rather than one route per resource type, because
 * a hundred route definitions is a hundred chunks in the router's own table. The type is data the
 * blade resolves a schema from, not structure.
 */
export const appRoutes: Routes = [
  {
    path: '',
    pathMatch: 'full',
    loadComponent: () => import('../pages/home/home').then((m) => m.Home),
    title: 'Cyber Cloud',
  },
  {
    path: 'resources',
    loadComponent: () => import('../pages/resources/resource-list').then((m) => m.ResourceList),
    title: 'Resources',
  },
  {
    // One route for every resource type. The blade reads the schema for (type, apiVersion) at
    // runtime — docs/plan/20 § Performance budget: "Schemas are fetched per type, cached, and
    // versioned by the api-version".
    path: 'resource/:provider/:type/:name',
    loadComponent: () => import('../pages/resources/resource-blade').then((m) => m.ResourceBlade),
  },
  {
    path: '**',
    loadComponent: () => import('../pages/not-found/not-found').then((m) => m.NotFound),
    title: 'Not found',
  },
];

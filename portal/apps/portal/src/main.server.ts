import { BootstrapContext, bootstrapApplication } from '@angular/platform-browser';
import { App } from './app/app';
import { config } from './app/app.config.server';

/**
 * ⚠ A function, called once per request, and that is load-bearing rather than incidental.
 *
 * `@angular/ssr` invokes this for each incoming request, and each call builds a new application
 * injector. Every store in `libs/shell` is `providedIn: 'root'`, which means per-injector, which
 * here means per-request. That is the mechanism behind docs/plan/20 § SSR's requirement that two
 * concurrent renders share nothing.
 *
 * ⚠ The `BootstrapContext` is required and must be threaded through — Angular 22 throws NG0401
 * ("Missing Platform") without it. It carries the per-request platform, which is the same
 * per-request boundary this file depends on: a bootstrap that reached for an ambient platform
 * instead would be reaching for process-wide state.
 *
 * The way to break the isolation is to hoist state to module scope — a `let currentTenant` in any
 * file the server bundle imports would be shared across every request in the process.
 * `ssr-isolation.server.spec.ts` renders two apps concurrently with different tenants and asserts
 * that has not happened.
 */
const bootstrap = (context: BootstrapContext) => bootstrapApplication(App, config, context);

export default bootstrap;

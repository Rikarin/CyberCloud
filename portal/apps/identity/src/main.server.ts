import { BootstrapContext, bootstrapApplication } from '@angular/platform-browser';
import { App } from './app/app';
import { config } from './app/app.config.server';

/**
 * Called once per request, building a fresh application injector each time.
 *
 * ⚠ The `BootstrapContext` is required and must be threaded through — Angular 22 throws NG0401
 * ("Missing Platform") without it.
 *
 * ⚠ **Nothing in this app's server render reads a credential**, which is the property
 * `credentials-never-leak.spec.ts` asserts. Angular SSR serializes transfer state into the rendered
 * HTML, so anything resolved on the server ships inside the document — an `HttpClient` call made
 * during the render would put its response bytes in the page. The sign-in and sign-up pages
 * therefore resolve nothing server-side; every request they make happens after hydration, from the
 * browser, against the cookie origin.
 */
const bootstrap = (context: BootstrapContext) => bootstrapApplication(App, config, context);

export default bootstrap;

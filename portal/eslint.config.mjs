// @ts-check
import eslint from '@eslint/js';
import tseslint from 'typescript-eslint';
import angular from 'angular-eslint';

export default tseslint.config(
  {
    ignores: [
      'dist/**',
      'node_modules/**',
      '.angular/**',
      'coverage/**',
      // docs/plan/03 § Assembly graph rules, rule 6 — the generator owns this directory, so lint
      // has no standing to tell it how to write.
      'libs/api/**',
      // The index document is not an Angular template; running the template rules over it flags
      // `<link rel>` and `<meta content>` as untranslated copy.
      'apps/portal/src/index.html',
      'apps/identity/src/index.html',
    ],
  },
  {
    files: ['**/*.ts'],
    extends: [
      eslint.configs.recommended,
      ...tseslint.configs.recommended,
      ...tseslint.configs.stylistic,
      ...angular.configs.tsRecommended,
    ],
    processor: angular.processInlineTemplates,
    rules: {
      '@angular-eslint/directive-selector': [
        'error',
        { type: 'attribute', prefix: 'cc', style: 'camelCase' },
      ],
      '@angular-eslint/component-selector': [
        'error',
        { type: 'element', prefix: 'cc', style: 'kebab-case' },
      ],

      // ── docs/plan/20 § Live updates ────────────────────────────────────────────────────────
      // "Every stream is a `signal`; the templates are `OnPush` and zoneless. Since xUI is
      // zoneless and signal-based, this is the natural style rather than a discipline — a
      // `ChangeDetectorRef` in portal code is a code-review failure."
      //
      // Encoded here so it is a build failure rather than a code-review failure, because a
      // review catches it only when someone is looking. In a zoneless app a ChangeDetectorRef
      // is not merely unidiomatic: markForCheck on a zoneless app is a no-op wherever the state
      // it is compensating for is not a signal, so the bug it papers over is invisible until a
      // blade silently stops updating.
      '@typescript-eslint/no-restricted-imports': 'off',
      'no-restricted-syntax': [
        'error',
        {
          selector: 'TSTypeReference > Identifier[name="ChangeDetectorRef"]',
          message:
            'ChangeDetectorRef is banned in portal code — docs/plan/20 § Live updates. The portal ' +
            'is zoneless and signal-based; derive state with signal()/computed() instead.',
        },
        {
          selector: 'CallExpression[callee.name="inject"] > Identifier[name="ChangeDetectorRef"]',
          message:
            'ChangeDetectorRef is banned in portal code — docs/plan/20 § Live updates. The portal ' +
            'is zoneless and signal-based; derive state with signal()/computed() instead.',
        },
        // ── docs/plan/10 § Authentication inputs ─────────────────────────────────────────────
        // "Authorization Code + PKCE → access token in memory, refresh in an `HttpOnly` cookie
        // scoped to the identity host … Access token never in `localStorage`."
        {
          selector: 'MemberExpression[object.name=/^(localStorage|sessionStorage)$/]',
          message:
            'Web storage is banned in portal code — docs/plan/10 § Authentication inputs keeps ' +
            'the access token in memory only. Use AccessTokenStore, which holds it in a signal.',
        },
        {
          selector: 'MemberExpression[property.name=/^(localStorage|sessionStorage)$/]',
          message:
            'Web storage is banned in portal code — docs/plan/10 § Authentication inputs keeps ' +
            'the access token in memory only. Use AccessTokenStore, which holds it in a signal.',
        },
      ],

      // OnPush everywhere. docs/plan/20 § Live updates.
      '@angular-eslint/prefer-on-push-component-change-detection': 'error',
      '@angular-eslint/prefer-standalone': 'error',
      '@angular-eslint/use-lifecycle-interface': 'error',

      '@typescript-eslint/consistent-type-definitions': ['error', 'interface'],
      '@typescript-eslint/no-unused-vars': ['error', { argsIgnorePattern: '^_' }],
    },
  },
  {
    // The SSR entries legitimately talk to Express and to Node globals.
    files: ['apps/portal/src/server.ts', 'apps/identity/src/server.ts', 'scripts/**/*.mjs'],
    rules: { 'no-restricted-syntax': 'off' },
  },
  {
    // The web-storage assertions in the auth tests have to name the APIs they are asserting are
    // unused. Banning the identifier there would ban the test that enforces the ban.
    files: ['**/*.spec.ts'],
    rules: {
      'no-restricted-syntax': [
        'error',
        {
          selector: 'TSTypeReference > Identifier[name="ChangeDetectorRef"]',
          message: 'ChangeDetectorRef is banned in portal code — docs/plan/20 § Live updates.',
        },
      ],
    },
  },
  {
    files: ['**/*.html'],
    extends: [...angular.configs.templateRecommended, ...angular.configs.templateAccessibility],
    rules: {
      // ── docs/plan/20 § Accessibility, i18n, theming ────────────────────────────────────────
      // "Theming is tokens, not `dark:` classes. Light and dark both work by construction."
      // xUI's token layer maps every semantic colour through `@theme inline`, so a `dark:`
      // utility in a portal template is a colour that cannot follow a tenant's white-label
      // override — the exact failure the token model exists to prevent.
      '@angular-eslint/template/no-call-expression': 'off',

      // ── docs/plan/20 § Accessibility, i18n, theming ────────────────────────────────────────
      // "i18n from day one with `@angular/localize`. English at M1, but the string extraction is
      // in place from the first commit — retrofitting i18n across 200 components is a quarter."
      //
      // This rule is what makes "from the first commit" true rather than aspirational: a template
      // that ships a bare English string fails the build today, while there is one app and a
      // handful of components, instead of being discovered across two hundred of them later.
      //
      // `checkAttributes` stays on because `placeholder`, `title` and `alt` are user-visible copy
      // and are easy to leave untranslated. The ignore list below is the non-linguistic residue:
      // component variants, ARIA plumbing whose value is a keyword rather than prose, and the
      // structural HTML attributes. ⚠ An attribute is only flagged when its value is a *static*
      // string — an `[aria-label]` bound to a `$localize` expression is already translated and is
      // not matched, which is why the list does not need every ARIA attribute in it.
      '@angular-eslint/template/i18n': [
        'error',
        {
          checkId: false,
          checkText: true,
          checkAttributes: true,
          ignoreAttributes: [
            // Structural / non-linguistic HTML.
            'charset', 'class', 'content', 'for', 'href', 'id', 'name', 'rel', 'src', 'style',
            'tabindex', 'target', 'type', 'value',
            // ⚠ Keyword-valued form attributes, added with the identity app's sign-in form.
            // `autocomplete="current-password"` and `inputmode="email"` are vocabulary the browser
            // and the password manager read, not copy a translator should touch — translating
            // either breaks autofill rather than localising anything.
            'autocomplete', 'inputmode',
            // ARIA whose value is a keyword, not prose.
            'aria-hidden', 'aria-live', 'aria-atomic', 'role',
            // xUI component configuration.
            'color', 'hotkey', 'interactionKind', 'placement', 'size', 'variant', 'xuiButton',
            'xuiDockContent', 'xuiPopover',
          ],
        },
      ],
    },
  },
);

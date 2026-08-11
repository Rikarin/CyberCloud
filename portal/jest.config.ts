import type { Config } from 'jest';

/**
 * The Jest suite covers components, stores and the shell's conventions in jsdom.
 *
 * ⚠ SSR is deliberately **not** tested here. `scripts/ssr-isolation.test.mjs` drives the built
 * server bundle over real HTTP instead, because the property docs/plan/20 § SSR is about — a
 * rendered page being cacheable across users — is a property of the deployed process's bytes and
 * response headers, not of an Angular API call. Testing it in Jest would test a different thing,
 * with Jest's module transform in the path and none of `server.ts`'s middleware.
 */
const config: Config = {
  preset: 'jest-preset-angular',
  testEnvironment: 'jsdom',
  setupFilesAfterEnv: ['<rootDir>/apps/portal/src/test-setup.ts'],
  testMatch: ['<rootDir>/apps/portal/src/**/*.spec.ts', '<rootDir>/libs/**/*.spec.ts'],
  testPathIgnorePatterns: ['/node_modules/', '/dist/'],
  moduleNameMapper: {
    '^@cybercloud/shell$': '<rootDir>/libs/shell/src/index.ts',
    '^@cybercloud/charts$': '<rootDir>/libs/charts/src/index.ts',
    '^@cybercloud/resource-forms$': '<rootDir>/libs/resource-forms/src/index.ts',
    '^@cybercloud/resource-forms-overrides$': '<rootDir>/libs/resource-forms-overrides/src/index.ts',
  },
  transform: {
    '^.+\\.(ts|mjs|js|html)$': [
      'jest-preset-angular',
      {
        tsconfig: '<rootDir>/apps/portal/tsconfig.spec.json',
        stringifyContentPathRegex: '\\.(html|svg)$',
      },
    ],
  },
  transformIgnorePatterns: ['node_modules/(?!(.*\\.mjs$|@xui|@ng-icons|@angular))'],
};

export default config;

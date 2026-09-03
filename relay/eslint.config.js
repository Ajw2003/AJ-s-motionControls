'use strict';

const js = require('@eslint/js');

// relay/ is CommonJS, Node >=20. Base ruleset per web-js-ts-node-standards.md.
module.exports = [
  js.configs.recommended,
  {
    languageOptions: {
      ecmaVersion: 2022,
      sourceType: 'commonjs',
      globals: {
        require: 'readonly',
        module: 'writable',
        process: 'readonly',
        __dirname: 'readonly',
        console: 'readonly',
        setTimeout: 'readonly',
        setInterval: 'readonly',
        clearTimeout: 'readonly',
        clearInterval: 'readonly',
        Buffer: 'readonly',
        fetch: 'readonly',
        URL: 'readonly',
      },
    },
    rules: {},
  },
  {
    ignores: ['node_modules/**'],
  },
];

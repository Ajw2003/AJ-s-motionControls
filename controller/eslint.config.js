import js from '@eslint/js';

// controller/ is ESM, browser-targeted vanilla JS. Base ruleset per
// web-js-ts-node-standards.md.
export default [
  js.configs.recommended,
  {
    languageOptions: {
      ecmaVersion: 2022,
      sourceType: 'module',
      globals: {
        window: 'readonly',
        document: 'readonly',
        navigator: 'readonly',
        console: 'readonly',
        fetch: 'readonly',
        WebSocket: 'readonly',
        setTimeout: 'readonly',
        setInterval: 'readonly',
        clearTimeout: 'readonly',
        clearInterval: 'readonly',
        localStorage: 'readonly',
        location: 'readonly',
        URLSearchParams: 'readonly',
        screen: 'readonly',
        RTCPeerConnection: 'readonly',
        DeviceMotionEvent: 'readonly',
        DeviceOrientationEvent: 'readonly',
        performance: 'readonly',
        // identity.js mints device ids with crypto.getRandomValues; every secure context
        // this page requires already provides it.
        crypto: 'readonly',
      },
    },
    rules: {},
  },
  {
    // test-sensors.js runs under Node, not the browser.
    files: ['test-sensors.js'],
    languageOptions: {
      globals: {
        process: 'readonly',
      },
    },
  },
];

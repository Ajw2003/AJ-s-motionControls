// The Wi-Fi QR text both phone cameras read as "join this network".
import assert from 'node:assert/strict';
import { wifiPayload } from './js/join.js';

assert.equal(wifiPayload('AJ Motion 1A2B', 'abc123'), 'WIFI:T:WPA;S:AJ Motion 1A2B;P:abc123;;');
assert.equal(wifiPayload('a;b', 'c,d:e"f\\g'), 'WIFI:T:WPA;S:a\\;b;P:c\\,d\\:e\\"f\\\\g;;');
console.log('join: ok');

#!/usr/bin/env node
/**
 * Serves one page whose only job is to be unambiguous on a phone screen.
 *
 * The DNS query is the real result of the spike, but "connection refused after a successful
 * lookup" is a confusing thing to ask someone to interpret on a phone. This turns a pass into
 * a green screen and a fail into a browser error, which needs no interpretation.
 *
 * Port 80 by default so the phone can type a bare hostname with no port.
 *
 *   node confirm-server.js
 *   node confirm-server.js --port 8080
 */

'use strict';

const http = require('http');

const args = { port: 80, bind: '0.0.0.0' };
for (let i = 2; i < process.argv.length; i += 2) {
  const key = process.argv[i].replace(/^--/, '');
  if (!(key in args)) {
    console.error(`Unknown option: ${process.argv[i]}`);
    process.exit(1);
  }
  args[key] = key === 'port' ? Number(process.argv[i + 1]) : process.argv[i + 1];
}

let hits = 0;

const server = http.createServer((req, res) => {
  // Browsers ask for this constantly; counting it as a visit would inflate the result.
  if (req.url === '/favicon.ico') {
    res.writeHead(404).end();
    return;
  }

  hits += 1;
  const client = (req.socket.remoteAddress || '').replace(/^::ffff:/, '');
  const host = req.headers.host || '<no host header>';

  // The Host header is the entire result. Reaching this page by IP proves only that the
  // network path and the firewall are working; reaching it by name is the thing the spike
  // is actually testing. An instrument that says "it worked" for both is worthless, so the
  // page has to tell them apart.
  const hostname = host.replace(/:\d+$/, '');
  const byName = !/^\d{1,3}(\.\d{1,3}){3}$/.test(hostname) && hostname !== '<no host header>';

  console.log(`  HIT #${hits}  from ${client}  for http://${host}${req.url}   ${byName ? '[BY NAME - this is the result]' : '[by IP - network/firewall only]'}`);

  const palette = byName
    ? { bg: '#04180b', fg: '#eaffef', accent: '#41e07a', chip: '#0d2c17', chipFg: '#9dffbe' }
    : { bg: '#1b1405', fg: '#fff6e6', accent: '#e8b33c', chip: '#2e2209', chipFg: '#ffd98a' };

  const heading = byName ? 'DNS works' : 'Network works';

  const body = byName
    ? `<p>You reached this by <strong>name</strong>: <code>${hostname}</code></p>
       <p>Your phone is <code>${client}</code></p>
       <p class="sub">That name exists in no public DNS &mdash; <code>local-ip.sh</code> only
       publishes IP-encoded names. The only way your phone resolved it is that this PC answered
       the query itself. <strong>Offline play is possible.</strong></p>`
    : `<p>You reached this by <strong>IP address</strong>: <code>${hostname}</code></p>
       <p>Your phone is <code>${client}</code></p>
       <p class="sub">This proves the hotspot, the routing and the firewall are all working
       &mdash; but an IP URL performs <strong>no DNS lookup</strong>, so it says nothing about
       the question this spike exists to answer. Now try
       <code>http://play.local-ip.sh/</code>.</p>`;

  res.writeHead(200, { 'Content-Type': 'text/html; charset=utf-8' });
  res.end(`<!doctype html>
<meta name="viewport" content="width=device-width,initial-scale=1">
<title>${heading}</title>
<style>
  body { margin:0; min-height:100vh; display:flex; align-items:center; justify-content:center;
         background:${palette.bg}; color:${palette.fg}; font:16px/1.5 system-ui,-apple-system,sans-serif; }
  .card { text-align:center; padding:2rem; max-width:34rem; }
  h1 { font-size:2.6rem; margin:0 0 .3rem; color:${palette.accent}; letter-spacing:-.02em; }
  p { margin:.5rem 0; }
  code { background:${palette.chip}; padding:.15rem .45rem; border-radius:.3rem;
         font:.95em ui-monospace,Menlo,Consolas,monospace; color:${palette.chipFg}; word-break:break-all; }
  .sub { opacity:.8; font-size:.92rem; margin-top:1.4rem; }
</style>
<div class="card">
  <h1>${heading}</h1>
  ${body}
</div>`);
});

server.on('error', (err) => {
  if (err.code === 'EADDRINUSE') {
    console.error(`\nPort ${args.port} is already in use. Try --port 8080.\n`);
  } else if (err.code === 'EACCES') {
    console.error(`\nPermission denied on port ${args.port}.\n`);
  } else {
    console.error(`\nServer error: ${err.message}\n`);
  }
  process.exit(1);
});

server.listen(args.port, args.bind, () => {
  console.log('');
  console.log(`  Confirmation server on ${args.bind}:${args.port}`);
  console.log('  Waiting for a phone. Each request is logged below.');
  console.log('');
});

process.on('SIGINT', () => {
  console.log(`\n\n  ${hits} page view(s).\n`);
  server.close(() => process.exit(0));
});

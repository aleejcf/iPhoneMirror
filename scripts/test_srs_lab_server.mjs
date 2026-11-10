import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { get, request } from 'node:http';
import { createServer } from 'node:net';
import { fileURLToPath } from 'node:url';
import { once } from 'node:events';
import test from 'node:test';

test('SRS lab survives malformed paths and aborted responses', { timeout: 15000 }, async () => {
  const reservation = createServer();
  reservation.listen(0, '127.0.0.1');
  await once(reservation, 'listening');
  const port = reservation.address().port;
  await new Promise(resolve => reservation.close(resolve));
  const child = spawn(process.execPath, [fileURLToPath(new URL('../tools/srs-lab/server.mjs', import.meta.url))], {
    env: { ...process.env, SRS_LAB_PORT: String(port) }, stdio: ['ignore', 'pipe', 'pipe'],
  });
  let errors = '';
  child.stderr.on('data', data => { errors += data; });
  try {
    await new Promise((resolve, reject) => {
      let output = '';
      const timer = setTimeout(() => reject(new Error('Server startup timed out: ' + errors)), 5000);
      child.once('error', error => { clearTimeout(timer); reject(error); });
      child.once('exit', code => { clearTimeout(timer); reject(new Error(`Server exited ${code}: ${errors}`)); });
      child.stdout.on('data', data => {
        output += data;
        if (output.includes('Stream Lab dashboard:')) { clearTimeout(timer); resolve(); }
      });
    });
    function send(path, method = 'GET') {
      return new Promise((resolve, reject) => {
        const req = request({ hostname: '127.0.0.1', port, path, method }, response => {
          let body = '';
          response.setEncoding('utf8');
          response.on('data', chunk => { body += chunk; });
          response.on('end', () => resolve({ status: response.statusCode, body }));
          response.on('error', reject);
        });
        req.on('error', reject);
        req.end();
      });
    }
    const normal = await send('/');
    assert.equal(normal.status, 200);
    assert.match(normal.body, /html/i);
    for (const path of ['/%ZZ', '/%E0%A4%A', '/%00']) {
      assert.equal((await send(path)).status, 400, path);
      assert.equal((await send('/')).status, 200, 'normal request after ' + path);
      assert.equal(child.exitCode, null, 'server remains alive');
    }
    assert.deepEqual(await send('/', 'HEAD'), { status: 200, body: '' });
    assert.equal((await send('/missing-audit-file.html')).status, 404);
    assert.equal((await send('/', 'POST')).status, 405);
    await new Promise((resolve, reject) => {
      const req = get({ hostname: '127.0.0.1', port, path: '/' }, response => {
        response.destroy();
        resolve();
      });
      req.on('error', reject);
    });
    assert.equal((await send('/')).status, 200, 'server survives a disconnected reader');
    assert.equal(child.exitCode, null);
    assert.doesNotMatch(errors, /URIError|UnhandledPromiseRejection/);
  } finally {
    if (child.exitCode === null && child.signalCode === null) {
      const exited = once(child, 'exit');
      child.kill();
      await exited;
    }
  }
});

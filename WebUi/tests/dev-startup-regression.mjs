import assert from 'node:assert/strict'
import { spawn, spawnSync } from 'node:child_process'
import { fileURLToPath } from 'node:url'
import net from 'node:net'

const host = '127.0.0.1'
const timeoutMs = 90_000
const panicPattern = /Turbopack.{0,120}panic|unopened alternate group|thread '.+' panicked|FATAL:.*Turbopack/is

const port = await new Promise((resolve, reject) => {
  const server = net.createServer()
  server.unref()
  server.on('error', reject)
  server.listen(0, host, () => {
    const address = server.address()
    server.close((error) => error ? reject(error) : resolve(address.port))
  })
})

const windows = process.platform === 'win32'
const command = windows ? process.env.ComSpec : 'npm'
const args = windows
  ? ['/d', '/s', '/c', `npm run dev -- --hostname ${host} --port ${port}`]
  : ['run', 'dev', '--', '--hostname', host, '--port', String(port)]
const child = spawn(command, args, {
  cwd: fileURLToPath(new URL('..', import.meta.url)),
  env: { ...process.env, NO_COLOR: '1' },
  stdio: ['ignore', 'pipe', 'pipe'],
})

let output = ''
let readyResolve
let readyReject
const ready = new Promise((resolve, reject) => { readyResolve = resolve; readyReject = reject })
const append = (chunk) => {
  output = (output + chunk.toString()).slice(-65_536)
  if (panicPattern.test(output)) readyReject(new Error(`development server panic:\n${output}`))
  if (/\bReady\b/i.test(output)) readyResolve()
}
child.stdout.on('data', append)
child.stderr.on('data', append)
child.on('error', readyReject)
child.on('exit', (code, signal) => readyReject(new Error(`development server exited before verification (${code ?? signal})\n${output}`)))

const timeout = setTimeout(() => readyReject(new Error(`development server was not ready within ${timeoutMs}ms\n${output}`)), timeoutMs)
try {
  await ready
  const response = await fetch(`http://${host}:${port}/`, { signal: AbortSignal.timeout(30_000) })
  const body = await response.text()
  assert.equal(response.status, 200, `expected HTTP 200, received ${response.status}\n${body.slice(0, 1000)}\n${output}`)
  await new Promise((resolve) => setTimeout(resolve, 500))
  assert.doesNotMatch(output, panicPattern)
  console.log(`PASS dev startup: Ready; GET / HTTP ${response.status}; Turbopack panic/unopened alternate group absent`)
} finally {
  clearTimeout(timeout)
  if (process.platform === 'win32' && child.pid) {
    spawnSync('taskkill.exe', ['/pid', String(child.pid), '/t', '/f'], { stdio: 'ignore' })
  } else if (!child.killed) {
    child.kill('SIGTERM')
  }
}

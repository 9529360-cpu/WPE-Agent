import assert from 'node:assert/strict'
import { readFileSync, existsSync } from 'node:fs'
import { join } from 'node:path'
import test from 'node:test'

const root = new URL('..', import.meta.url).pathname.replace(/^\/(?:[A-Za-z]:)/, value => value.slice(1))
const routes = ['/', '/agents', '/orders', '/positions', '/history', '/strategies', '/risk', '/monitoring', '/settings']
const removed = ['/backtest', '/plugins', '/equities', '/research', '/distribution', '/security']

function source(path) {
  return readFileSync(join(root, path), 'utf8')
}

test('v1 navigation exposes only customer-usable routes', () => {
  const nav = source('lib/nav.ts')
  for (const route of routes) assert.match(nav, new RegExp(`href:['"]${route === '/' ? '\\/' : route}['"]`), `missing ${route}`)
  for (const route of removed) assert.doesNotMatch(nav, new RegExp(`href:['"]${route}['"]`), `future route ${route} is visible`)
  assert.equal((nav.match(/href:['"][^'"]+['"]/g) ?? []).length, routes.length)
})

test('every v1 route has host provenance and no production fallback markers', () => {
  const layout = source('app/(dashboard)/layout.tsx')
  const evidence = source('app/(dashboard)/_components/runtime-evidence-strip.tsx')
  assert.match(layout, /<RuntimeEvidenceStrip\s*\/>/)
  for (const field of ['runtimeProviderId', 'environment', 'sourceTimestampUtc', 'snapshotTimestampUtc', 'runtimeAgeSeconds', 'runtimeDiagnosticReason']) assert.match(evidence, new RegExp(field))
  const productionSources = routes.map(route => route === '/' ? 'app/(dashboard)/page.tsx' : `app/(dashboard)${route}/page.tsx`)
  for (const path of productionSources) {
    const text = source(path)
    assert.doesNotMatch(text, /runtime-preview|fixture|fake|cached fallback/i, path)
  }
})

test('static export contains deterministic nonblank route DOM', () => {
  for (const route of routes) {
    const output = route === '/' ? join(root, 'out/index.html') : join(root, `out${route}/index.html`)
    assert.equal(existsSync(output), true, `missing build output for ${route}`)
    const html = readFileSync(output, 'utf8')
    assert.match(html, /<main[^>]*>/, `${route} has no main landmark`)
    assert.match(html, /data-runtime-evidence="host-authoritative"/, `${route} has no runtime evidence DOM`)
    const visibleDom = html.replace(/<script\b[^>]*>[\s\S]*?<\/script>/gi, '')
    assert.doesNotMatch(visibleDom, />\s*(?:PREVIEW DATA|fixture|fake|cached fallback)\s*</i, `${route} contains visible fallback marker`)
  }
})

test('visible controls are navigation or completed local interactions', () => {
  const shell = [source('components/shell/sidebar.tsx'), source('components/shell/topbar.tsx')].join('\n')
  assert.doesNotMatch(shell, /postMessage|postRuntimeHostCommand|open-settings|agent-start|agent-stop/)
  for (const behavior of ['setCollapsed', 'setLocale', 'setOpen', 'setMobilePath']) assert.match(shell, new RegExp(behavior))
})

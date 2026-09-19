import assert from 'node:assert/strict'
import { readFileSync, existsSync } from 'node:fs'
import { join } from 'node:path'
import test from 'node:test'

const root = new URL('..', import.meta.url).pathname.replace(/^\/(?:[A-Za-z]:)/, value => value.slice(1))
const navRoutes = [
  '/',
  '/agents',
  '/teacher',
  '/orders',
  '/positions',
  '/strategies',
  '/backtest',
  '/risk',
  '/history',
  '/plugins',
  '/security',
  '/monitoring',
  '/settings',
]
const intentionallyHiddenRoutes = ['/equities', '/research', '/distribution']

function source(path) {
  return readFileSync(join(root, path), 'utf8')
}

test('primary navigation exposes accepted operator routes and keeps unaccepted scopes out', () => {
  const nav = source('lib/nav.ts')
  for (const route of navRoutes) {
    assert.match(nav, new RegExp(`href:['"]${route === '/' ? '\\/' : route}['"]`), `missing ${route}`)
  }
  for (const route of intentionallyHiddenRoutes) {
    assert.doesNotMatch(nav, new RegExp(`href:['"]${route}['"]`), `unaccepted route ${route} is visible`)
  }
  assert.equal((nav.match(/href:['"][^'"]+['"]/g) ?? []).length, navRoutes.length)
})

test('every primary route has host provenance and no production fallback markers', () => {
  const layout = source('app/(dashboard)/layout.tsx')
  const evidence = source('app/(dashboard)/_components/runtime-evidence-strip.tsx')
  assert.match(layout, /<RuntimeEvidenceStrip\s*\/>/)
  for (const field of ['runtimeProviderId', 'environment', 'sourceTimestampUtc', 'snapshotTimestampUtc', 'runtimeAgeSeconds', 'runtimeDiagnosticReason']) {
    assert.match(evidence, new RegExp(field))
  }
  const productionSources = navRoutes.map(route => route === '/' ? 'app/(dashboard)/page.tsx' : `app/(dashboard)${route}/page.tsx`)
  for (const path of productionSources) {
    const text = source(path)
    assert.doesNotMatch(text, /runtime-preview|fixture|fake|cached fallback/i, path)
  }
})

test('dashboard uses canonical Agent runtime and decision context instead of simulated thought UI', () => {
  const home = source('app/(dashboard)/page.tsx')
  const context = source('components/dashboard/decision-context.tsx')
  assert.match(home, /DecisionFlow/)
  assert.match(home, /DecisionContext/)
  assert.doesNotMatch(home, /AgentStatusPanel|ThoughtStream/)
  for (const field of ['lastDecision', 'decisionDiagnostics', 'lastReason']) assert.match(context, new RegExp(field))
  for (const legacy of ['currentThought', 'workflowNode', 'thinkingProgress', 'reviewerStatus']) {
    assert.doesNotMatch(context, new RegExp(legacy), `legacy thought/progress semantic leaked into decision context: ${legacy}`)
  }
})


test('execution command center is read-only and backed by canonical host projections', () => {
  const home = source('app/(dashboard)/page.tsx')
  const cockpit = source('components/dashboard/execution-cockpit.tsx')
  assert.match(home, /ExecutionCockpit/)
  for (const field of ['positions', 'orders', 'historicalOrders', 'historicalPostTradeReviews', 'historicalReconciliations', 'traceId', 'riskDecision', 'executionStatus', 'marketDataVersion', 'riskApprovalStatus', 'executionApprovalStatus', 'authorizationMode']) {
    assert.match(cockpit, new RegExp(field), `missing canonical field ${field}`)
  }
  assert.match(cockpit, /READ ONLY/)
  assert.match(cockpit, /dashboard\.closedTradeHelp/)
  assert.match(cockpit, /dashboard\.reconciliationHelp/)
  assert.doesNotMatch(cockpit, /item\.allowsRiskIncrease\s*\?\s*gateTone/, 'historical reconciliation must not masquerade as a current success gate')
  assert.doesNotMatch(cockpit, /clientOrderId|cycleId|executionId/, 'raw execution identities must not enter the cockpit')
  const forbiddenCockpitSurfaces = [
    /postHostCommand/,
    /\.postMessage\s*\(/,
    /place-order|cancel-order|direct-exchange-submit/,
    /\bsubmitOrder\b|\bcancelOrder\b/,
    /\bfetch\s*\(/,
    /\bWebSocket\b|\bEventSource\b/,
  ]
  for (const forbidden of forbiddenCockpitSurfaces) {
    assert.doesNotMatch(cockpit, forbidden, `read-only cockpit contains forbidden mutation/egress surface: ${forbidden}`)
  }
})

test('static export contains deterministic nonblank DOM for every primary route', () => {
  for (const route of navRoutes) {
    const output = route === '/' ? join(root, 'out/index.html') : join(root, `out${route}/index.html`)
    assert.equal(existsSync(output), true, `missing build output for ${route}`)
    const html = readFileSync(output, 'utf8')
    assert.match(html, /<main[^>]*>/, `${route} has no main landmark`)
    assert.match(html, /data-runtime-evidence="host-authoritative"/, `${route} has no runtime evidence DOM`)
    const visibleDom = html.replace(/<script\b[^>]*>[\s\S]*?<\/script>/gi, '')
    assert.doesNotMatch(visibleDom, />\s*(?:PREVIEW DATA|fixture|fake|cached fallback)\s*</i, `${route} contains visible fallback marker`)
  }
})

test('global shell remains read-only and lifecycle commands have one typed bridge owner', () => {
  const shell = [
    source('components/shell/sidebar.tsx'),
    source('components/shell/topbar.tsx'),
    source('components/shell/status-bar.tsx'),
  ].join('\n')
  assert.doesNotMatch(shell, /postMessage|postHostCommand|open-settings|open-notification-settings|agent-start|agent-stop/)
  for (const behavior of ['setCollapsed', 'setLocale', 'setOpen', 'setMobilePath']) assert.match(shell, new RegExp(behavior))

  const hostCommand = source('lib/host-command.ts')
  for (const command of ['open-settings', 'open-notification-settings', 'agent-start', 'agent-stop']) {
    assert.match(hostCommand, new RegExp(`'${command}'`), `missing allowed host command ${command}`)
  }
  assert.doesNotMatch(hostCommand, /place-order|approve|confirm|submitOrder|direct-exchange-submit/)

  const agents = source('app/(dashboard)/agents/page.tsx')
  const settings = source('app/(dashboard)/settings/page.tsx')
  for (const consumer of [agents, settings]) {
    assert.match(consumer, /postHostCommand/)
    assert.doesNotMatch(consumer, /\.postMessage\s*\(/)
  }
  assert.match(agents, /window\.confirm/)
  assert.match(agents, /agentStartAllowed/)
  assert.match(agents, /agentStopAllowed/)
})

test('motion-heavy status affordances honor reduced-motion preference', () => {
  const css = source('app/globals.css')
  assert.match(css, /@media\s*\(prefers-reduced-motion:\s*reduce\)/)
  for (const className of ['animate-pulse-dot', 'animate-ticker', 'animate-sweep']) {
    assert.match(css, new RegExp(`\\.${className}`))
  }
  assert.match(css, /animation:\s*none\s*!important/)
})

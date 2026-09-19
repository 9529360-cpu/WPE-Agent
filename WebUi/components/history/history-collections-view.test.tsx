import assert from 'node:assert/strict'
import test from 'node:test'
import { renderToStaticMarkup } from 'react-dom/server'
import { projectRuntimeHistory } from '../../app/(dashboard)/history/page'
import type { RuntimeHistoricalCollection, WpeRuntimeState } from '../runtime-bridge'
import { HistoryCollectionsView, unsupportedHistoryProjection, type HistoryProjection } from './history-collections-view'

const emptyPage = { state: 'available' as const, items: [] }
const base = (): HistoryProjection => ({ orders: emptyPage, postTradeReviews: emptyPage, reconciliations: emptyPage, equity: emptyPage, backtests: emptyPage, skillCalls: emptyPage, auditEvents: emptyPage })
const render = (projection: HistoryProjection) => renderToStaticMarkup(<HistoryCollectionsView projection={projection} />)
const collection = <T,>(items: T[], nextCursor: string | null = null): RuntimeHistoricalCollection<T> => ({ state: 'available', items, nextCursor, sourceUpdatedAtUtc: '2026-07-22T01:00:00Z', source: 'local-agent-sqlite' })

test('maps all seven normalized top-level collections to real available rows', () => {
  const secret = 'MUST-NOT-RENDER'
  const runtime: WpeRuntimeState = {
    historicalOrders: collection([{ sequence: 1, occurredAtUtc: '2026-07-22T01:00:00Z', correlationId: secret, clientOrderId: secret, symbol: 'BTCUSDT', side: 'Buy', action: 'Open', reduceOnly: false, quantity: 0.25, averagePrice: 64000, status: 'Filled' }], secret),
    historicalEquity: collection([{ sequence: 2, observedAtUtc: '2026-07-22T01:01:00Z', equity: 12500, availableBalance: 9000, environment: 'Testnet', providerId: 'binance-futures' }]),
    historicalBacktests: collection([{ backtestId: 'backtest-real-1', completedAtUtc: '2026-07-22T01:02:00Z', strategyId: 'trend-alpha', strategyVersion: '2.1.0', symbol: 'ETHUSDT', status: 'Passed', coverageDays: 365, trades: 42, outOfSampleReturn: 0.12, maxDrawdown: 0.04, sharpe: 1.7 }]),
    historicalSkillCalls: collection([{ id: 'skill-real-1', occurredAtUtc: '2026-07-22T01:03:00Z', skill: 'market-research', status: 'Completed', durationMs: 87, mode: 'Local Only', remoteLlmUsed: false, tokens: 0, costUsd: 0 }]),
    historicalAuditEvents: collection([{ id: 'audit-real-1', occurredAtUtc: '2026-07-22T01:04:00Z', category: 'risk', source: 'risk-gate', correlationId: secret, status: 'Blocked', ...({ payload: secret } as object) }]),
    historicalPostTradeReviews: collection([{ schema: 'wpe.post-trade-review/1.4', clientOrderId: secret, cycleId: secret, symbol: 'SOLUSDT', side: 'Long', entryPrice: 145, exitPrice: 150, quantity: 2, fees: .12, feeBasis: 'exchange-reported-usdt', feeRate: 0, entrySlippageAmount: .01, exitSlippageAmount: .02, totalSlippageAmount: .03, slippageBasis: 'intent-expected-vs-fill', fundingAmount: .5, fundingBasis: 'exchange-reported-window', netPnl: 10.38, returnPct: .0358, outcome: 'win', closedAtUtc: '2026-07-22T01:05:00Z', strategyId: 'mean-reversion-sol', strategyVersion: '3.0.0', attributionBasis: 'automatic-artifact' }]),
    historicalReconciliations: collection([{ kind: 'position', reportId: secret, schema: 'wpe.position-reconciliation/1.0', observedAtUtc: '2026-07-22T01:06:00Z', evaluatedAtUtc: '2026-07-22T01:06:00Z', state: 'Confirmed', allowsRiskIncrease: true, canonicalSha256: 'a'.repeat(64) }]),
  }

  const html = render(projectRuntimeHistory(runtime))
  for (const value of ['BTCUSDT', 'binance-futures', 'trend-alpha', 'market-research', 'risk-gate', 'mean-reversion-sol', 'exchange-reported-usdt', 'Confirmed']) assert.match(html, new RegExp(value))
  assert.doesNotMatch(html, new RegExp(secret))
})

test('top-level unsupported, stale, and error collections withhold rows and expose explicit state', () => {
  for (const state of ['unsupported', 'stale', 'error'] as const) {
    const hidden = collection([{ sequence: 1, occurredAtUtc: 'hidden', correlationId: null, clientOrderId: null, symbol: 'MUST-NOT-RENDER', side: 'hidden', action: 'hidden', reduceOnly: false, quantity: 1, averagePrice: 1, status: 'hidden' }])
    hidden.state = state
    hidden.message = `${state} host state`
    const html = render(projectRuntimeHistory({ historicalOrders: hidden }))
    assert.match(html, new RegExp(`${state} host state`))
    assert.doesNotMatch(html, /MUST-NOT-RENDER/)
  }
})

test('does not treat legacy history or preview data as live historical collections', () => {
  const runtime = { previewMode: true, history: { orders: collection([{ symbol: 'PREVIEW-MUST-NOT-RENDER' }]) } } as unknown as WpeRuntimeState
  const html = render(projectRuntimeHistory(runtime))
  assert.equal(html.match(/unsupported/g)?.length, 14)
  assert.doesNotMatch(html, /PREVIEW-MUST-NOT-RENDER/)
})

test('shows all seven capability states and real available empty states', () => {
  const html = render(base())
  for (const label of ['Orders', 'Closed trade accounting', 'Reconciliation audits', 'Equity', 'Backtests', 'Skill calls', 'Audit events']) assert.match(html, new RegExp(label))
  assert.equal(html.match(/No records in this collection/g)?.length, 7)
  assert.equal(html.match(/Page 1/g)?.length, 7)
})

test('clears rows for unsupported, stale, and error collections', () => {
  for (const state of ['unsupported', 'stale', 'error'] as const) {
    const projection = base()
    projection.orders = { state, items: [{ sequence: 1, occurredAtUtc: 'hidden', symbol: 'MUST-NOT-RENDER', side: 'hidden', action: 'hidden', quantity: 1, averagePrice: 1, status: 'hidden' }] }
    assert.doesNotMatch(render(projection), /MUST-NOT-RENDER/)
  }
})

test('does not render or retain opaque cursors', () => {
  const opaqueCursor = 'opaque-secret-cursor-MUST-NOT-RENDER'
  const projection = base()
  projection.orders = { ...emptyPage, pageNumber: 3, hasPreviousPage: true, hasNextPage: true, ...({ nextCursor: opaqueCursor } as object) }
  const html = render(projection)
  assert.match(html, /Page 3/)
  assert.doesNotMatch(html, new RegExp(opaqueCursor))
  assert.doesNotMatch(html, /nextCursor/i)
})

test('audit table exposes metadata but never audit payload', () => {
  const payload = 'SENSITIVE-AUDIT-PAYLOAD-MUST-NOT-RENDER'
  const projection = base()
  projection.auditEvents = { state: 'available', items: [{ id: 'audit-1', occurredAtUtc: '2026-07-22T01:00:00Z', category: 'risk', source: 'host-audit', status: 'blocked', ...({ payload } as object) }] }
  const html = render(projection)
  assert.match(html, /host-audit/)
  assert.match(html, /blocked/)
  assert.doesNotMatch(html, new RegExp(payload))
  assert.doesNotMatch(html, />Payload</i)
})

test('default projection is fail-closed and contains no records', () => {
  const html = render(unsupportedHistoryProjection)
  assert.equal(html.match(/unsupported/g)?.length, 14)
  assert.doesNotMatch(html, /<tbody>/)
})

test('malformed top-level projections fail closed instead of throwing', () => {
  const malformed = { state: 'error', items: [{ value: 'MUST-NOT-RENDER' }] } as unknown as HistoryProjection
  const html = render(malformed)
  assert.equal(html.match(/unsupported/g)?.length, 14)
  assert.doesNotMatch(html, /MUST-NOT-RENDER/)
})

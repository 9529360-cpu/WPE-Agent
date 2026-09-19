import assert from 'node:assert/strict'
import test from 'node:test'
import { renderToStaticMarkup } from 'react-dom/server'
import { normalizeRuntimeEvent } from '../runtime-bridge'
import { DistributionStatusView, type DistributionProjection } from './distribution-status-view'

const render = (projection: DistributionProjection) => renderToStaticMarkup(<DistributionStatusView projection={projection} />)
const value = {
  status: 'Denied' as const,
  allowed: false,
  approvalRoles: ['PrimaryReviewer', 'SecondaryReviewer'] as Array<'PrimaryReviewer' | 'SecondaryReviewer'>,
  validUntilUtc: '2026-08-01T00:00:00Z',
  withdrawn: false,
  receiptHash: 'A'.repeat(64),
  policyHash: 'B'.repeat(64),
  contentFactsHash: 'C'.repeat(64),
  consentScopeHash: 'D'.repeat(64),
  consentVersion: 'consent-v1',
  suitabilityVersion: 'suitability-v1',
  auditCorrelationHash: 'E'.repeat(64),
  reasonCodes: ['FRESH_DUAL_REVIEW_REQUIRED'],
  asOfUtc: '2026-07-22T00:00:00Z',
}

function hostSnapshot(distribution: unknown) {
  const generated = new Date()
  const source = new Date(generated.getTime() - 1000)
  return {
    contractVersion: '1.0',
    generatedAtUtc: generated.toISOString(),
    sourceUpdatedAtUtc: source.toISOString(),
    freshness: { fresh: true, ageSeconds: 1, staleAfterSeconds: 15 },
    environment: 'Testnet',
    connectionStatus: {
      state: 'Available',
      value: {
        connectionId: 'primary',
        displayName: 'Primary',
        providerId: 'binance',
        environment: 'Testnet',
        credentialStatus: 'configured',
        adapterStatus: 'ready',
        lastCheckedAtUtc: generated.toISOString(),
        ready: true,
        exchangeConnected: true,
        tradePermission: true,
        withdrawPermission: false,
      },
    },
    distribution,
  }
}

test('fails closed for unsupported stale and error', () => {
  for (const state of ['unsupported', 'stale', 'error'] as const)
    assert.doesNotMatch(render({ state, value: { ...value, consentVersion: 'MUST-NOT-RENDER' } }), /MUST-NOT-RENDER/)
})

test('fails closed when an available projection carries Error status', () => {
  const hostile = {
    ...value,
    status: 'Error' as const,
    receiptHash: 'RECEIPT-MUST-NOT-RENDER',
    policyHash: 'POLICY-MUST-NOT-RENDER',
    contentFactsHash: 'CONTENT-MUST-NOT-RENDER',
    consentScopeHash: 'CONSENT-MUST-NOT-RENDER',
    consentVersion: 'CONSENT-VERSION-MUST-NOT-RENDER',
    suitabilityVersion: 'SUITABILITY-MUST-NOT-RENDER',
    auditCorrelationHash: 'AUDIT-MUST-NOT-RENDER',
  }
  const html = render({ state: 'available', value: hostile })
  assert.match(html, /Distribution projection error/)
  assert.doesNotMatch(html, /MUST-NOT-RENDER|Approval evidence/)
})

test('bridge drops evidence for Error status and non-available states', () => {
  const hostileValue = { ...value, status: 'Error', allowed: false }
  for (const distribution of [
    { state: 'available', value: hostileValue },
    { state: 'stale', value: hostileValue },
    { state: 'error', value: hostileValue },
  ]) {
    const normalized = normalizeRuntimeEvent(hostSnapshot(distribution))
    assert.ok(normalized)
    assert.equal(normalized.distribution?.state, distribution.state === 'error' ? 'error' : distribution.state === 'stale' ? 'stale' : 'error')
    assert.equal(normalized.distribution?.value, undefined)
    assert.doesNotMatch(JSON.stringify(normalized.distribution), /[A-E]{64}|consent-v1|suitability-v1/)
  }
})

test('renders minimized denied evidence', () => {
  const html = render({ state: 'available', value: { ...value, approvalRoles: [...value.approvalRoles] } })
  assert.match(html, /Denied/)
  assert.match(html, /PrimaryReviewer/)
  assert.match(html, /FRESH_DUAL_REVIEW_REQUIRED/)
  assert.doesNotMatch(html, /recipient|destination|reviewer id|opinion|send|retry|approve|configuration/i)
  assert.doesNotMatch(html, /<button/)
})

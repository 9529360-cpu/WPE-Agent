import assert from 'node:assert/strict'
import test from 'node:test'
import { renderToStaticMarkup } from 'react-dom/server'
import { EquityMarketView, type EquityMarketReadModel } from './equity-market-view'

function render(model: EquityMarketReadModel) {
  return renderToStaticMarkup(<EquityMarketView model={model} />)
}

test('renders unsupported, stale, and error states without market rows', () => {
  for (const [state, expected] of [
    ['unsupported', 'not supported'],
    ['stale', 'is stale'],
    ['error', 'is unavailable'],
  ] as const) {
    const html = render({ state, items: [], message: undefined })
    assert.match(html, new RegExp(expected))
    assert.doesNotMatch(html, /<table/)
    assert.doesNotMatch(html, /MUST-NOT-RENDER/)
  }
})

test('renders a real empty state for an available collection', () => {
  const html = render({ state: 'available', items: [] })
  assert.match(html, /No equity instruments available/)
  assert.doesNotMatch(html, /<table/)
})

test('renders only host-provided available observations', () => {
  const html = render({
    state: 'available',
    asOfUtc: '2026-07-21T01:02:03Z',
    items: [{
      instrumentId: 'instrument-from-host',
      symbol: 'HOST-SYMBOL',
      displayName: 'Host supplied issuer',
      venue: 'HOST-VENUE',
      currency: 'HOST-CCY',
      sessionState: 'host-session-state',
      lastPrice: 12.5,
      changePercent: null,
      observedAtUtc: '2026-07-21T01:02:00Z',
      providerId: 'host-provider',
    }],
  })

  assert.match(html, /HOST-SYMBOL/)
  assert.match(html, /host-provider/)
  assert.match(html, /Not provided/)
  assert.doesNotMatch(html, /AAPL|NASDAQ|broker connected/i)
})

test('preserves long host text while adding wrapping boundaries', () => {
  const longToken = `provider-${'x'.repeat(180)}`
  const longMessage = `diagnostic-${'y'.repeat(180)}`
  const stateHtml = render({ state: 'error', items: [], message: longMessage })
  assert.match(stateHtml, new RegExp(longMessage))
  assert.match(stateHtml, /overflow-wrap:anywhere/)

  const availableHtml = render({
    state: 'available',
    items: [{
      instrumentId: 'long-text-row',
      symbol: `symbol-${'s'.repeat(120)}`,
      displayName: `issuer-${'i'.repeat(160)}`,
      venue: `venue-${'v'.repeat(120)}`,
      currency: `currency-${'c'.repeat(80)}`,
      sessionState: `session-${'q'.repeat(120)}`,
      lastPrice: 4.25,
      changePercent: 0,
      observedAtUtc: 'host-time-without-inventing-a-date',
      providerId: longToken,
    }],
  })
  assert.match(availableHtml, new RegExp(longToken))
  assert.match(availableHtml, /host-time-without-inventing-a-date/)
})

test('renders a compact mobile view and a desktop table for available rows', () => {
  const html = render({
    state: 'available',
    items: [{
      instrumentId: 'responsive-row', symbol: 'HOST', displayName: 'Host row', venue: 'VENUE',
      currency: 'CCY', sessionState: 'session', lastPrice: null, changePercent: null,
      observedAtUtc: '2026-07-21T01:02:00Z', providerId: 'provider',
    }],
  })
  assert.match(html, /hidden overflow-x-auto md:block/)
  assert.match(html, /grid min-w-0 gap-3 p-3 md:hidden/)
  assert.match(html, /<article/)
  assert.match(html, /<table/)
})

test('does not reveal rows carried by a non-available collection', () => {
  const html = render({
    state: 'stale',
    items: [{
      instrumentId: 'hidden',
      symbol: 'MUST-NOT-RENDER',
      displayName: 'Hidden stale row',
      venue: 'hidden',
      currency: 'hidden',
      sessionState: 'hidden',
      lastPrice: 1,
      changePercent: 0,
      observedAtUtc: '2026-07-20T01:02:00Z',
      providerId: 'hidden',
    }],
  })

  assert.doesNotMatch(html, /MUST-NOT-RENDER/)
})

'use client'

import { AlertTriangle, Building2, Clock3, DatabaseZap } from 'lucide-react'
import { Panel, PanelBody, PanelHeader } from '@/components/ui/panel'
import { StatusBadge } from '@/components/ui/status-badge'
import type { RuntimeCollectionState } from '@/components/runtime-bridge'

export type EquityMarketRow = {
  instrumentId: string
  symbol: string
  displayName: string
  venue: string
  currency: string
  sessionState: string
  lastPrice: number | null
  changePercent: number | null
  observedAtUtc: string
  providerId: string
}

export type EquityMarketReadModel = {
  state: RuntimeCollectionState
  items: EquityMarketRow[]
  message?: string
  asOfUtc?: string
}

const stateCopy: Record<Exclude<RuntimeCollectionState, 'available'>, { title: string; body: string }> = {
  unsupported: {
    title: 'Equity market data is not supported',
    body: 'The host has not exposed an authorized equity market read model. No prices, instruments, or broker support are inferred.',
  },
  stale: {
    title: 'Equity market data is stale',
    body: 'The last host snapshot is outside its freshness window. Previous values are hidden until a fresh snapshot arrives.',
  },
  error: {
    title: 'Equity market data is unavailable',
    body: 'The host could not provide a valid equity market snapshot. No cached or estimated values are shown.',
  },
}

function formatNumber(value: number, options?: Intl.NumberFormatOptions) {
  return new Intl.NumberFormat(undefined, options).format(value)
}

function formatObservedAt(value: string) {
  const time = Date.parse(value)
  return Number.isNaN(time) ? value : new Date(time).toLocaleString()
}

function StateNotice({ state, message }: { state: Exclude<RuntimeCollectionState, 'available'>; message?: string }) {
  const copy = stateCopy[state]
  return (
    <Panel>
      <PanelBody className="flex min-h-64 flex-col items-center justify-center p-8 text-center">
        <div role={state === 'error' ? 'alert' : 'status'} aria-live={state === 'error' ? 'assertive' : 'polite'} aria-atomic="true" className="flex flex-col items-center">
          <AlertTriangle className="mb-3 size-5 text-warning" aria-hidden="true" />
          <h2 className="max-w-full break-words text-sm font-medium">{copy.title}</h2>
          <p className="mt-2 max-w-2xl [overflow-wrap:anywhere] text-xs leading-5 text-muted-foreground">{message || copy.body}</p>
        </div>
      </PanelBody>
    </Panel>
  )
}

function MarketTable({ items }: { items: EquityMarketRow[] }) {
  return (
    <div className="hidden overflow-x-auto md:block">
      <table className="w-full min-w-[860px] text-left text-xs">
        <thead className="border-b border-border text-muted-foreground">
          <tr>
            {['Instrument', 'Venue', 'Session', 'Last price', 'Change', 'Observed', 'Source'].map((label) => (
              <th key={label} scope="col" className="px-4 py-3 font-medium">{label}</th>
            ))}
          </tr>
        </thead>
        <tbody>
          {items.map((item) => (
            <tr key={item.instrumentId} className="border-b border-border/70 last:border-b-0">
              <td className="px-4 py-3">
                <div className="max-w-56 [overflow-wrap:anywhere] font-medium">{item.symbol}</div>
                <div className="mt-0.5 max-w-56 [overflow-wrap:anywhere] text-muted-foreground">{item.displayName}</div>
              </td>
              <td className="max-w-36 [overflow-wrap:anywhere] px-4 py-3">{item.venue}</td>
              <td className="max-w-36 [overflow-wrap:anywhere] px-4 py-3">{item.sessionState}</td>
              <td className="px-4 py-3 tabular-nums">
                {item.lastPrice === null ? 'Not provided' : `${formatNumber(item.lastPrice, { maximumFractionDigits: 6 })} ${item.currency}`}
              </td>
              <td className={`px-4 py-3 tabular-nums ${item.changePercent === null ? 'text-muted-foreground' : item.changePercent >= 0 ? 'text-success' : 'text-danger'}`}>
                {item.changePercent === null ? 'Not provided' : formatNumber(item.changePercent, { style: 'percent', maximumFractionDigits: 2 })}
              </td>
              <td className="max-w-44 [overflow-wrap:anywhere] px-4 py-3">{formatObservedAt(item.observedAtUtc)}</td>
              <td className="max-w-48 [overflow-wrap:anywhere] px-4 py-3 font-mono text-[11px]">{item.providerId}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  )
}

function MarketCards({ items }: { items: EquityMarketRow[] }) {
  return (
    <div className="grid min-w-0 gap-3 p-3 md:hidden">
      {items.map((item) => (
        <article key={item.instrumentId} className="min-w-0 rounded-lg border border-border p-3">
          <div className="flex min-w-0 items-start justify-between gap-3">
            <div className="min-w-0">
              <div className="[overflow-wrap:anywhere] font-medium">{item.symbol}</div>
              <div className="mt-0.5 [overflow-wrap:anywhere] text-xs text-muted-foreground">{item.displayName}</div>
            </div>
            <span className="max-w-28 shrink-0 [overflow-wrap:anywhere] text-right text-[11px] text-muted-foreground">{item.sessionState}</span>
          </div>
          <dl className="mt-3 grid min-w-0 grid-cols-2 gap-x-3 gap-y-2 border-t border-border pt-3 text-xs">
            <div className="min-w-0"><dt className="text-muted-foreground">Venue</dt><dd className="[overflow-wrap:anywhere]">{item.venue}</dd></div>
            <div className="min-w-0"><dt className="text-muted-foreground">Last price</dt><dd className="[overflow-wrap:anywhere] tabular-nums">{item.lastPrice === null ? 'Not provided' : `${formatNumber(item.lastPrice, { maximumFractionDigits: 6 })} ${item.currency}`}</dd></div>
            <div className="min-w-0"><dt className="text-muted-foreground">Change</dt><dd className={`tabular-nums ${item.changePercent === null ? 'text-muted-foreground' : item.changePercent >= 0 ? 'text-success' : 'text-danger'}`}>{item.changePercent === null ? 'Not provided' : formatNumber(item.changePercent, { style: 'percent', maximumFractionDigits: 2 })}</dd></div>
            <div className="min-w-0"><dt className="text-muted-foreground">Observed</dt><dd className="[overflow-wrap:anywhere]">{formatObservedAt(item.observedAtUtc)}</dd></div>
          </dl>
          <div className="mt-3 border-t border-border pt-2 font-mono text-[10px] text-muted-foreground [overflow-wrap:anywhere]">{item.providerId}</div>
        </article>
      ))}
    </div>
  )
}

export function EquityMarketView({ model }: { model: EquityMarketReadModel }) {
  if (model.state !== 'available') return <StateNotice state={model.state} message={model.message} />

  if (model.items.length === 0) {
    return (
      <Panel>
        <PanelBody className="flex min-h-64 flex-col items-center justify-center p-8 text-center">
          <div role="status" aria-live="polite" aria-atomic="true" className="flex flex-col items-center">
            <DatabaseZap className="mb-3 size-5 text-muted-foreground" aria-hidden="true" />
            <h2 className="text-sm font-medium">No equity instruments available</h2>
            <p className="mt-2 max-w-xl [overflow-wrap:anywhere] text-xs leading-5 text-muted-foreground">
              The host reported an available collection with no instruments. Connect an authorized read provider before market rows can appear.
            </p>
          </div>
        </PanelBody>
      </Panel>
    )
  }

  const venueCount = new Set(model.items.map((item) => item.venue)).size
  const pricedCount = model.items.filter((item) => item.lastPrice !== null).length

  return (
    <div className="space-y-5">
      <div className="grid gap-3 sm:grid-cols-3">
        <div className="rounded-lg border border-border bg-panel/40 p-3">
          <div className="text-xs text-muted-foreground">Instruments</div>
          <div className="mt-1 text-sm font-medium">{model.items.length}</div>
        </div>
        <div className="rounded-lg border border-border bg-panel/40 p-3">
          <div className="text-xs text-muted-foreground">Venues</div>
          <div className="mt-1 text-sm font-medium">{venueCount}</div>
        </div>
        <div className="rounded-lg border border-border bg-panel/40 p-3">
          <div className="text-xs text-muted-foreground">Priced instruments</div>
          <div className="mt-1 text-sm font-medium">{pricedCount} / {model.items.length}</div>
        </div>
      </div>
      <Panel>
        <PanelHeader
          icon={<Building2 className="size-4" />}
          title={<h2 className="text-sm font-medium">Equity market</h2>}
          action={<StatusBadge token="success" label="Available" />}
        />
        <MarketTable items={model.items} />
        <MarketCards items={model.items} />
        {model.asOfUtc ? (
          <div className="flex min-w-0 items-start gap-1.5 border-t border-border px-4 py-3 text-[11px] text-muted-foreground">
            <Clock3 className="mt-0.5 size-3.5 shrink-0" aria-hidden="true" />
            <span className="[overflow-wrap:anywhere]">Host snapshot {formatObservedAt(model.asOfUtc)}</span>
          </div>
        ) : null}
      </Panel>
    </div>
  )
}

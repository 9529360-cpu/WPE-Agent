'use client'

import { AlertTriangle, Archive, ChevronLeft, ChevronRight, DatabaseZap } from 'lucide-react'
import type { ReactNode } from 'react'
import type { RuntimeCollectionState } from '@/components/runtime-bridge'
import { Panel, PanelBody, PanelHeader } from '@/components/ui/panel'
import { StatusBadge } from '@/components/ui/status-badge'

type PageState<T> = {
  state: RuntimeCollectionState
  items: T[]
  message?: string
  source?: string
  sourceUpdatedAtUtc?: string
  pageNumber?: number
  hasPreviousPage?: boolean
  hasNextPage?: boolean
}

export type HistoricalOrder = { sequence: number; occurredAtUtc: string; symbol: string; side: string; action: string; quantity: number; averagePrice: number | null; status: string }
export type HistoricalEquity = { sequence: number; observedAtUtc: string; equity: number; availableBalance: number; environment: string; providerId: string }
export type HistoricalBacktest = { backtestId: string; completedAtUtc: string; strategyId: string; strategyVersion: string; symbol: string; status: string; coverageDays: number; trades: number; outOfSampleReturn: number; maxDrawdown: number; sharpe: number }
export type HistoricalSkillCall = { id: string; occurredAtUtc: string; skill: string; status: string; durationMs: number; mode: string | null; remoteLlmUsed: boolean | null; tokens: number | null; costUsd: number | null }
export type HistoricalAuditEvent = { id: string; occurredAtUtc: string; category: string; source: string; status: string }
export type HistoricalExecutionReality = { observedAtUtc: string; strategyId: string; strategyVersion: string; costModelVersion: string; symbol: string; state: string; terminal: boolean; priceComparable: boolean; feeComparable: boolean; totalComparable: boolean; fillRatio: number; slippageDriftBps: number; feeDriftBps: number | null; totalExecutionDriftBps: number | null; observationLatencyMs: number; reasonCode: string }

export type HistoryProjection = {
  orders: PageState<HistoricalOrder>
  equity: PageState<HistoricalEquity>
  backtests: PageState<HistoricalBacktest>
  skillCalls: PageState<HistoricalSkillCall>
  auditEvents: PageState<HistoricalAuditEvent>
  executionReality: PageState<HistoricalExecutionReality>
}

const unsupported = (message: string): PageState<never> => ({ state: 'unsupported', items: [], message })
export const unsupportedHistoryProjection: HistoryProjection = {
  orders: unsupported('Historical orders are not exposed by the current host runtime.'),
  equity: unsupported('Historical equity is not exposed by the current host runtime.'),
  backtests: unsupported('Historical backtests are not exposed by the current host runtime.'),
  skillCalls: unsupported('Historical skill calls are not exposed by the current host runtime.'),
  auditEvents: unsupported('Historical audit events are not exposed by the current host runtime.'),
  executionReality: unsupported('Execution reality history is not exposed by the current host runtime.'),
}

const labels = { orders: 'Orders', equity: 'Equity', backtests: 'Backtests', skillCalls: 'Skill calls', auditEvents: 'Audit events', executionReality: 'Execution reality' } as const
const stateTone: Record<RuntimeCollectionState, string> = { available: 'success', unsupported: 'muted', stale: 'warning', error: 'danger' }
const stateMessage: Record<Exclude<RuntimeCollectionState, 'available'>, string> = {
  unsupported: 'This historical collection is not supported by the host.',
  stale: 'This historical collection is stale. Previous rows are hidden.',
  error: 'This historical collection could not be read. No cached rows are shown.',
}

function date(value: string) { const parsed = Date.parse(value); return Number.isNaN(parsed) ? value : new Date(parsed).toLocaleString() }
function number(value: number, options?: Intl.NumberFormatOptions) { return new Intl.NumberFormat(undefined, options).format(value) }
function Cell({ children, mono = false }: { children: ReactNode; mono?: boolean }) { return <td className={`max-w-56 [overflow-wrap:anywhere] px-3 py-2.5 align-top ${mono ? 'font-mono text-[11px]' : ''}`}>{children}</td> }

function PageControls({ page }: { page: Pick<PageState<unknown>, 'pageNumber' | 'hasPreviousPage' | 'hasNextPage'> }) {
  const current = page.pageNumber && page.pageNumber > 0 ? page.pageNumber : 1
  return (
    <div className="flex flex-wrap items-center justify-between gap-2 border-t border-border px-3 py-2 text-xs text-muted-foreground" aria-label="Pagination status">
      <span>Page {current}</span>
      <div className="flex items-center gap-1" aria-label="Read-only pagination controls">
        <button type="button" disabled title={page.hasPreviousPage ? 'Previous page is available through the host projection.' : 'No previous page is available.'} className="inline-flex h-7 items-center gap-1 rounded-md border border-border px-2 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-ring disabled:cursor-not-allowed disabled:opacity-60"><ChevronLeft className="size-3.5" aria-hidden="true"/>Previous</button>
        <button type="button" disabled title={page.hasNextPage ? 'Next page is available through the host projection.' : 'No next page is available.'} className="inline-flex h-7 items-center gap-1 rounded-md border border-border px-2 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-ring disabled:cursor-not-allowed disabled:opacity-60">Next<ChevronRight className="size-3.5" aria-hidden="true"/></button>
      </div>
    </div>
  )
}

function CollectionFrame<T>({ title, page, children }: { title: string; page: PageState<T>; children: (items: T[]) => ReactNode }) {
  const items = page.state === 'available' ? page.items : []
  return (
    <Panel className="min-w-0 overflow-hidden">
      <PanelHeader title={<h2 className="text-sm font-medium">{title}</h2>} icon={<Archive className="size-4" />} action={<span role="status" aria-live="polite" aria-atomic="true"><StatusBadge token={stateTone[page.state]} label={page.state} /></span>} />
      {page.state !== 'available' ? (
        <PanelBody className="flex min-h-36 flex-col items-center justify-center text-center">
          <div role={page.state === 'error' ? 'alert' : 'status'} aria-live={page.state === 'error' ? 'assertive' : 'polite'} aria-atomic="true" className="flex flex-col items-center">
            <AlertTriangle className="mb-2 size-4 text-warning" aria-hidden="true" />
            <p className="max-w-xl [overflow-wrap:anywhere] text-xs text-muted-foreground">{page.message || stateMessage[page.state]}</p>
          </div>
        </PanelBody>
      ) : items.length === 0 ? (
        <PanelBody className="flex min-h-36 flex-col items-center justify-center text-center">
          <div role="status" aria-live="polite" aria-atomic="true" className="flex flex-col items-center">
            <DatabaseZap className="mb-2 size-4 text-muted-foreground" aria-hidden="true" />
            <h3 className="text-sm font-medium">No records in this collection</h3>
            <p className="mt-1 text-xs text-muted-foreground">The host returned an available page with no rows.</p>
          </div>
        </PanelBody>
      ) : children(items)}
      {page.state === 'available' ? <PageControls page={page} /> : null}
    </Panel>
  )
}

function Table({ headings, children }: { headings: string[]; children: ReactNode }) {
  return <div className="overflow-x-auto"><table className="w-full min-w-[720px] text-left text-xs"><thead className="border-b border-border text-muted-foreground"><tr>{headings.map(x => <th key={x} scope="col" className="px-3 py-2.5 font-medium">{x}</th>)}</tr></thead><tbody>{children}</tbody></table></div>
}

export function HistoryCollectionsView({ projection }: { projection: HistoryProjection }) {
  const safeProjection: HistoryProjection = {
    orders: projection?.orders ?? unsupportedHistoryProjection.orders,
    equity: projection?.equity ?? unsupportedHistoryProjection.equity,
    backtests: projection?.backtests ?? unsupportedHistoryProjection.backtests,
    skillCalls: projection?.skillCalls ?? unsupportedHistoryProjection.skillCalls,
    auditEvents: projection?.auditEvents ?? unsupportedHistoryProjection.auditEvents,
    executionReality: projection?.executionReality ?? unsupportedHistoryProjection.executionReality,
  }
  const entries = Object.entries(labels) as [keyof HistoryProjection, string][]
  return (
    <div className="min-w-0 space-y-5">
      <div className="grid min-w-0 gap-2 sm:grid-cols-2 xl:grid-cols-3 2xl:grid-cols-6" role="status" aria-live="polite" aria-atomic="true" aria-label="Historical collection capabilities">
        {entries.map(([key, label]) => <div key={key} className="flex min-w-0 items-center justify-between gap-2 rounded-lg border border-border bg-panel/40 p-3"><span className="min-w-0 break-words text-xs">{label}</span><StatusBadge token={stateTone[safeProjection[key].state]} label={safeProjection[key].state} /></div>)}
      </div>

      <CollectionFrame title={labels.orders} page={safeProjection.orders}>{items => <Table headings={['Time', 'Symbol', 'Side / action', 'Quantity', 'Average price', 'Status']}>{items.map(x => <tr key={x.sequence} className="border-b border-border/70 last:border-0"><Cell>{date(x.occurredAtUtc)}</Cell><Cell mono>{x.symbol}</Cell><Cell>{x.side} / {x.action}</Cell><Cell>{number(x.quantity, { maximumFractionDigits: 8 })}</Cell><Cell>{x.averagePrice === null ? 'Not provided' : number(x.averagePrice, { maximumFractionDigits: 8 })}</Cell><Cell>{x.status}</Cell></tr>)}</Table>}</CollectionFrame>
      <CollectionFrame title={labels.equity} page={safeProjection.equity}>{items => <Table headings={['Time', 'Equity', 'Available', 'Environment', 'Provider']}>{items.map(x => <tr key={x.sequence} className="border-b border-border/70 last:border-0"><Cell>{date(x.observedAtUtc)}</Cell><Cell>{number(x.equity, { maximumFractionDigits: 8 })}</Cell><Cell>{number(x.availableBalance, { maximumFractionDigits: 8 })}</Cell><Cell>{x.environment}</Cell><Cell mono>{x.providerId}</Cell></tr>)}</Table>}</CollectionFrame>
      <CollectionFrame title={labels.backtests} page={safeProjection.backtests}>{items => <Table headings={['Completed', 'Strategy', 'Symbol', 'Status', 'Coverage / trades', 'OOS / drawdown / Sharpe']}>{items.map(x => <tr key={x.backtestId} className="border-b border-border/70 last:border-0"><Cell>{date(x.completedAtUtc)}</Cell><Cell>{x.strategyId}<div className="text-muted-foreground">{x.strategyVersion}</div></Cell><Cell mono>{x.symbol}</Cell><Cell>{x.status}</Cell><Cell>{x.coverageDays} days / {x.trades}</Cell><Cell>{number(x.outOfSampleReturn, { style: 'percent', maximumFractionDigits: 2 })} / {number(x.maxDrawdown, { style: 'percent', maximumFractionDigits: 2 })} / {number(x.sharpe, { maximumFractionDigits: 2 })}</Cell></tr>)}</Table>}</CollectionFrame>
      <CollectionFrame title={labels.skillCalls} page={safeProjection.skillCalls}>{items => <Table headings={['Time', 'Skill', 'Status', 'Duration', 'Mode', 'Remote', 'Tokens / cost']}>{items.map(x => <tr key={x.id} className="border-b border-border/70 last:border-0"><Cell>{date(x.occurredAtUtc)}</Cell><Cell>{x.skill}</Cell><Cell>{x.status}</Cell><Cell>{x.durationMs} ms</Cell><Cell>{x.mode ?? 'Not provided'}</Cell><Cell>{x.remoteLlmUsed === null ? 'Not provided' : x.remoteLlmUsed ? 'Yes' : 'No'}</Cell><Cell>{x.tokens ?? 'Not provided'}{x.costUsd === null ? '' : ` / $${number(x.costUsd, { maximumFractionDigits: 6 })}`}</Cell></tr>)}</Table>}</CollectionFrame>
      <CollectionFrame title={labels.auditEvents} page={safeProjection.auditEvents}>{items => <Table headings={['Time', 'Category', 'Source', 'Status']}>{items.map(x => <tr key={x.id} className="border-b border-border/70 last:border-0"><Cell>{date(x.occurredAtUtc)}</Cell><Cell>{x.category}</Cell><Cell>{x.source}</Cell><Cell>{x.status}</Cell></tr>)}</Table>}</CollectionFrame>
      <CollectionFrame title={labels.executionReality} page={safeProjection.executionReality}>{items => <Table headings={['Observed', 'Strategy / cost model', 'Symbol / state', 'Fill', 'Slippage drift', 'Fee / total drift', 'Latency / reason']}>{items.map((x,index) => <tr key={`${x.observedAtUtc}-${x.strategyId}-${x.strategyVersion}-${index}`} className="border-b border-border/70 last:border-0"><Cell>{date(x.observedAtUtc)}</Cell><Cell>{x.strategyId}<div className="text-muted-foreground">{x.strategyVersion} · {x.costModelVersion}</div></Cell><Cell mono>{x.symbol}<div className="font-sans text-muted-foreground">{x.state}{x.terminal ? ' · terminal' : ''}</div></Cell><Cell>{number(x.fillRatio, { style: 'percent', maximumFractionDigits: 1 })}<div className="text-muted-foreground">{x.priceComparable ? 'price comparable' : 'price unavailable'}</div></Cell><Cell>{x.priceComparable ? `${number(x.slippageDriftBps, { maximumFractionDigits: 2 })} bps` : 'Not comparable'}</Cell><Cell>{x.feeDriftBps === null ? 'Fee unavailable' : `${number(x.feeDriftBps, { maximumFractionDigits: 2 })} bps fee`}<div className="text-muted-foreground">{x.totalExecutionDriftBps === null ? 'Total unavailable' : `${number(x.totalExecutionDriftBps, { maximumFractionDigits: 2 })} bps total`}</div></Cell><Cell>{x.observationLatencyMs} ms<div className="text-muted-foreground">{x.reasonCode}</div></Cell></tr>)}</Table>}</CollectionFrame>
    </div>
  )
}

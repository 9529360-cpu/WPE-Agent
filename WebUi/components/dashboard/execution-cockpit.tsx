'use client'

import Link from 'next/link'
import { Activity, AlertTriangle, History, ReceiptText, ShieldAlert, WalletCards } from 'lucide-react'
import type { ReactNode } from 'react'
import { useWpeRuntime, type RuntimeCollectionState, type RuntimeHistoricalReconciliation } from '@/components/runtime-bridge'
import { RuntimeUnavailable } from '@/components/runtime-state'
import { Panel, PanelBody, PanelHeader } from '@/components/ui/panel'
import { StatusBadge } from '@/components/ui/status-badge'
import { useI18n } from '@/lib/i18n/context'

function stateOf(fresh: boolean | undefined, state: RuntimeCollectionState | undefined): RuntimeCollectionState {
  return fresh === false && state === 'available' ? 'stale' : state ?? 'unsupported'
}

function gateTone(value: string | undefined) {
  const normalized = value?.toLowerCase() ?? ''
  if (/ready|approved|allow|pass|enabled|confirmed|protected|clear/.test(normalized)) return 'success'
  if (/block|deny|fail|halt|reject|disabled|conflict|invalid|isolated|incomplete|stale/.test(normalized)) return 'danger'
  return value ? 'warning' : 'muted'
}

function sideTone(value: string) {
  return /long|buy/i.test(value) ? 'text-success' : /short|sell/i.test(value) ? 'text-danger' : 'text-foreground'
}

function SummaryMetric({ label, value, detail }: { label: string; value: ReactNode; detail?: ReactNode }) {
  return (
    <div className="min-w-0 border-l-2 border-border pl-3">
      <div className="text-[10px] font-medium uppercase tracking-wide text-muted-foreground">{label}</div>
      <div className="mt-1 min-w-0 break-words text-sm font-semibold tabular">{value}</div>
      {detail ? <div className="mt-1 min-w-0 break-words text-[10px] text-muted-foreground">{detail}</div> : null}
    </div>
  )
}

function CollectionNotice({ state, message }: { state: RuntimeCollectionState; message?: string }) {
  return (
    <div className="flex min-h-24 items-center justify-center gap-2 p-4 text-center text-xs text-muted-foreground">
      <AlertTriangle className="size-4 shrink-0 text-warning" aria-hidden="true" />
      <span className="break-words">{message || state}</span>
    </div>
  )
}

function SectionLink({ href, children }: { href: string; children: ReactNode }) {
  return <Link href={href} className="text-[11px] text-muted-foreground transition-colors hover:text-foreground focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-ring">{children}</Link>
}

function ReconciliationRow({ label, item, formatDate }: { label: string; item?: RuntimeHistoricalReconciliation; formatDate: (value: Date | string, options?: Intl.DateTimeFormatOptions) => string }) {
  return (
    <div className="grid grid-cols-[minmax(0,1fr)_auto] gap-x-3 gap-y-1 border-b border-border/70 py-3 last:border-0">
      <div className="min-w-0 text-xs font-medium">{label}</div>
      {item ? <StatusBadge token={item.allowsRiskIncrease ? gateTone(item.state) : 'danger'} label={item.state} /> : <StatusBadge token="muted" label="Unavailable" />}
      <div className="min-w-0 break-words text-[10px] text-muted-foreground">{item?.schema ?? 'No persisted audit'}</div>
      <div className="whitespace-nowrap text-right text-[10px] text-muted-foreground">{item ? formatDate(item.evaluatedAtUtc) : ''}</div>
    </div>
  )
}

export function ExecutionCockpit() {
  const runtime = useWpeRuntime()
  const { t, formatDate, formatNumber } = useI18n()

  if (!runtime.runtimeFresh) {
    return <RuntimeUnavailable stale={Boolean(runtime.lastUpdated)} subject={t('orders.title')} />
  }

  const positionsState = stateOf(runtime.runtimeFresh, runtime.collectionStates?.positions)
  const ordersState = stateOf(runtime.runtimeFresh, runtime.collectionStates?.orders)
  const ledgerState = stateOf(runtime.runtimeFresh, runtime.historicalOrders?.state ?? runtime.collectionStates?.historicalOrders)
  const postTradeState = stateOf(runtime.runtimeFresh, runtime.historicalPostTradeReviews?.state ?? runtime.collectionStates?.historicalPostTradeReviews)
  const reconciliationState = stateOf(runtime.runtimeFresh, runtime.historicalReconciliations?.state ?? runtime.collectionStates?.historicalReconciliations)
  const riskState = stateOf(runtime.runtimeFresh, runtime.collectionStates?.risk)
  const authorizationState = stateOf(runtime.runtimeFresh, runtime.authorizationMode?.state)

  const positions = positionsState === 'available' ? runtime.positions ?? [] : []
  const orders = ordersState === 'available' ? runtime.orders ?? [] : []
  const ledger = ledgerState === 'available' ? runtime.historicalOrders?.items.slice(0, 6) ?? [] : []
  const postTrades = postTradeState === 'available' ? runtime.historicalPostTradeReviews?.items.slice(0, 5) ?? [] : []
  const reconciliations = reconciliationState === 'available' ? runtime.historicalReconciliations?.items ?? [] : []
  const latestReconciliation = (kind: RuntimeHistoricalReconciliation['kind']) => reconciliations.find(item => item.kind === kind)
  const authorizationMode = authorizationState === 'available' ? runtime.authorizationMode?.value?.mode : undefined
  const provider = runtime.runtimeProviderId || runtime.connectionStatus?.value?.providerId

  return (
    <Panel className="overflow-hidden">
      <PanelHeader
        icon={<Activity className="size-4" />}
        title={t('orders.title')}
        action={<StatusBadge token="muted" label="READ ONLY" />}
      />
      <PanelBody className="space-y-4">
        <div className="grid gap-3 sm:grid-cols-2 xl:grid-cols-4">
          <SummaryMetric label="Environment" value={runtime.environment || t('common.unknown')} detail={provider || t('common.notProvided')} />
          <SummaryMetric label="Authorization" value={authorizationMode || t('common.unavailable')} detail={authorizationState} />
          <SummaryMetric
            label={t('dashboard.riskGate')}
            value={<StatusBadge token={riskState === 'available' ? gateTone(runtime.riskApprovalStatus) : 'warning'} label={riskState === 'available' ? runtime.riskApprovalStatus || t('common.unknown') : riskState} />}
            detail={runtime.riskReady === undefined ? undefined : runtime.riskReady ? t('common.ready') : t('common.notReady')}
          />
          <SummaryMetric
            label={t('orders.gate')}
            value={<StatusBadge token={gateTone(runtime.executionApprovalStatus)} label={runtime.executionApprovalStatus || t('common.unknown')} />}
            detail={runtime.automaticExecutions?.state === 'available' ? runtime.automaticExecutions.items[0]?.status : runtime.automaticExecutions?.state}
          />
        </div>

        <div className="grid min-w-0 gap-4 xl:grid-cols-2">
          <section className="min-w-0 overflow-hidden rounded-lg border border-border">
            <div className="flex items-center justify-between gap-3 border-b border-border px-3 py-2.5">
              <div className="flex items-center gap-2 text-xs font-medium"><WalletCards className="size-4 text-muted-foreground" aria-hidden="true" />{t('dashboard.portfolio')}</div>
              <SectionLink href="/positions">{t('dashboard.viewAll')}</SectionLink>
            </div>
            {positionsState !== 'available' ? (
              <CollectionNotice state={positionsState} message={runtime.collectionMessages?.positions} />
            ) : positions.length === 0 ? (
              <div className="flex min-h-24 items-center justify-center p-4 text-xs text-muted-foreground">{t('dashboard.noPositions')}</div>
            ) : (
              <div className="overflow-x-auto">
                <table className="w-full min-w-[520px] text-left text-xs">
                  <thead className="border-b border-border text-muted-foreground">
                    <tr><th className="px-3 py-2 font-medium">{t('common.symbol')}</th><th className="px-3 py-2 font-medium">{t('common.side')}</th><th className="px-3 py-2 text-right font-medium">{t('common.quantity')}</th><th className="px-3 py-2 text-right font-medium">{t('dashboard.entryPrice')}</th><th className="px-3 py-2 text-right font-medium">uPnL</th></tr>
                  </thead>
                  <tbody>
                    {positions.slice(0, 6).map((position, index) => (
                      <tr key={[position.symbol, position.side, index].join(':')} className="border-b border-border/70 last:border-0">
                        <td className="px-3 py-2.5 font-medium">{position.symbol}</td>
                        <td className={'px-3 py-2.5 ' + sideTone(position.side)}>{position.side}</td>
                        <td className="px-3 py-2.5 text-right tabular">{formatNumber(position.quantity, { maximumFractionDigits: 8 })}</td>
                        <td className="px-3 py-2.5 text-right tabular">{formatNumber(position.entryPrice, { maximumFractionDigits: 8 })}</td>
                        <td className={'px-3 py-2.5 text-right tabular ' + (position.unrealizedPnl >= 0 ? 'text-success' : 'text-danger')}>{formatNumber(position.unrealizedPnl, { maximumFractionDigits: 2 })}</td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            )}
          </section>

          <section className="min-w-0 overflow-hidden rounded-lg border border-border">
            <div className="flex items-center justify-between gap-3 border-b border-border px-3 py-2.5">
              <div className="flex items-center gap-2 text-xs font-medium"><ReceiptText className="size-4 text-muted-foreground" aria-hidden="true" />{t('dashboard.openOrders')}</div>
              <SectionLink href="/orders">{t('dashboard.viewAll')}</SectionLink>
            </div>
            {ordersState !== 'available' ? (
              <CollectionNotice state={ordersState} message={runtime.collectionMessages?.orders} />
            ) : orders.length === 0 ? (
              <div className="flex min-h-24 items-center justify-center p-4 text-xs text-muted-foreground">{t('dashboard.noOrders')}</div>
            ) : (
              <div className="overflow-x-auto">
                <table className="w-full min-w-[520px] text-left text-xs">
                  <thead className="border-b border-border text-muted-foreground">
                    <tr><th className="px-3 py-2 font-medium">{t('common.symbol')}</th><th className="px-3 py-2 font-medium">{t('common.side')}</th><th className="px-3 py-2 font-medium">{t('common.type')}</th><th className="px-3 py-2 font-medium">{t('common.status')}</th><th className="px-3 py-2 text-right font-medium">{t('common.quantity')}</th></tr>
                  </thead>
                  <tbody>
                    {orders.slice(0, 6).map(order => (
                      <tr key={order.orderId} className="border-b border-border/70 last:border-0">
                        <td className="px-3 py-2.5 font-medium">{order.symbol}</td>
                        <td className={'px-3 py-2.5 ' + sideTone(order.side)}>{order.side}</td>
                        <td className="px-3 py-2.5">{order.type}</td>
                        <td className="px-3 py-2.5">{order.status}</td>
                        <td className="px-3 py-2.5 text-right tabular">{formatNumber(order.quantity, { maximumFractionDigits: 8 })}</td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            )}
          </section>
        </div>

        <div className="grid min-w-0 gap-4 xl:grid-cols-[minmax(0,1.55fr)_minmax(280px,.75fr)]">
          <section className="min-w-0 overflow-hidden rounded-lg border border-border">
            <div className="flex flex-wrap items-center justify-between gap-3 border-b border-border px-3 py-2.5">
              <div>
                <div className="text-xs font-medium">{t('dashboard.closedTradeAttribution')}</div>
                <div className="mt-0.5 text-[10px] text-muted-foreground">{t('dashboard.closedTradeHelp')}</div>
              </div>
              <SectionLink href="/history">{t('dashboard.viewAll')}</SectionLink>
            </div>
            {postTradeState !== 'available' ? (
              <CollectionNotice state={postTradeState} message={runtime.historicalPostTradeReviews?.message} />
            ) : postTrades.length === 0 ? (
              <div className="flex min-h-24 items-center justify-center p-4 text-xs text-muted-foreground">{t('dashboard.noClosedTradeReviews')}</div>
            ) : (
              <div className="overflow-x-auto">
                <table className="w-full min-w-[860px] text-left text-xs">
                  <thead className="border-b border-border text-muted-foreground">
                    <tr><th className="px-3 py-2 font-medium">{t('common.time')}</th><th className="px-3 py-2 font-medium">{t('common.symbol')}</th><th className="px-3 py-2 font-medium">{t('dashboard.strategyIdentity')}</th><th className="px-3 py-2 text-right font-medium">{t('dashboard.entryExit')}</th><th className="px-3 py-2 text-right font-medium">{t('dashboard.netPnl')}</th><th className="px-3 py-2 text-right font-medium">{t('dashboard.feeFundingSlippage')}</th></tr>
                  </thead>
                  <tbody>
                    {postTrades.map(review => (
                      <tr key={review.clientOrderId} className="border-b border-border/70 last:border-0">
                        <td className="whitespace-nowrap px-3 py-2.5 text-muted-foreground">{formatDate(review.closedAtUtc)}</td>
                        <td className="px-3 py-2.5"><div className="font-medium">{review.symbol}</div><div className={sideTone(review.side)}>{review.side}</div></td>
                        <td className="max-w-52 px-3 py-2.5"><div className="truncate font-medium">{review.strategyId ?? review.strategyVersion}</div><div className="truncate text-[10px] text-muted-foreground">{review.strategyId ? review.strategyVersion : t('dashboard.legacyVersionOnly')} · {review.attributionBasis}</div></td>
                        <td className="px-3 py-2.5 text-right tabular">{formatNumber(review.entryPrice, { maximumFractionDigits: 8 })} → {formatNumber(review.exitPrice, { maximumFractionDigits: 8 })}</td>
                        <td className={'px-3 py-2.5 text-right tabular font-medium ' + (review.netPnl >= 0 ? 'text-success' : 'text-danger')}><div>{formatNumber(review.netPnl, { maximumFractionDigits: 4 })}</div><div className="text-[10px]">{review.returnPct >= 0 ? '+' : ''}{formatNumber(review.returnPct * 100, { maximumFractionDigits: 2 })}%</div></td>
                        <td className="px-3 py-2.5 text-right tabular"><div>{formatNumber(review.fees, { maximumFractionDigits: 4 })} / {formatNumber(review.fundingAmount, { maximumFractionDigits: 4 })} / {formatNumber(review.totalSlippageAmount, { maximumFractionDigits: 4 })}</div><div className="mt-1 max-w-72 text-[10px] text-muted-foreground">{review.feeBasis} · {review.fundingBasis} · {review.slippageBasis}</div></td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            )}
          </section>

          <section className="min-w-0 overflow-hidden rounded-lg border border-border">
            <div className="flex items-center justify-between gap-3 border-b border-border px-3 py-2.5">
              <div className="flex items-center gap-2 text-xs font-medium"><ShieldAlert className="size-4 text-muted-foreground" aria-hidden="true" />{t('dashboard.reconciliationGates')}</div>
              <SectionLink href="/risk">{t('dashboard.riskSummary')}</SectionLink>
            </div>
            {reconciliationState !== 'available' ? (
              <CollectionNotice state={reconciliationState} message={runtime.historicalReconciliations?.message} />
            ) : (
              <div className="px-3">
                <ReconciliationRow label={t('dashboard.positionReconciliation')} item={latestReconciliation('position')} formatDate={formatDate} />
                <ReconciliationRow label={t('dashboard.protectionReconciliation')} item={latestReconciliation('protection')} formatDate={formatDate} />
                <ReconciliationRow label={t('dashboard.externalPositionIsolation')} item={latestReconciliation('externalIsolation')} formatDate={formatDate} />
              </div>
            )}
          </section>
        </div>

        <section className="min-w-0 overflow-hidden rounded-lg border border-border">
          <div className="flex flex-wrap items-center justify-between gap-3 border-b border-border px-3 py-2.5">
            <div className="flex items-center gap-2 text-xs font-medium"><History className="size-4 text-muted-foreground" aria-hidden="true" />{t('dashboard.executionLedger')}</div>
            <SectionLink href="/history">{t('dashboard.viewAll')}</SectionLink>
          </div>
          {ledgerState !== 'available' ? (
            <CollectionNotice state={ledgerState} message={runtime.historicalOrders?.message} />
          ) : ledger.length === 0 ? (
            <div className="flex min-h-24 items-center justify-center p-4 text-xs text-muted-foreground">{t('dashboard.noExecutionEvents')}</div>
          ) : (
            <div className="overflow-x-auto">
              <table className="w-full min-w-[760px] text-left text-xs">
                <thead className="border-b border-border text-muted-foreground">
                  <tr><th className="px-3 py-2 font-medium">{t('common.time')}</th><th className="px-3 py-2 font-medium">{t('common.symbol')}</th><th className="px-3 py-2 font-medium">{t('dashboard.sideAction')}</th><th className="px-3 py-2 text-right font-medium">{t('common.quantity')}</th><th className="px-3 py-2 text-right font-medium">{t('dashboard.averagePrice')}</th><th className="px-3 py-2 font-medium">{t('common.status')}</th></tr>
                </thead>
                <tbody>
                  {ledger.map(row => (
                    <tr key={row.sequence} className="border-b border-border/70 last:border-0">
                      <td className="whitespace-nowrap px-3 py-2.5 text-muted-foreground">{formatDate(row.occurredAtUtc)}</td>
                      <td className="px-3 py-2.5 font-medium">{row.symbol}</td>
                      <td className={'px-3 py-2.5 ' + sideTone(row.side)}>{row.side} / {row.action}</td>
                      <td className="px-3 py-2.5 text-right tabular">{formatNumber(row.quantity, { maximumFractionDigits: 8 })}</td>
                      <td className="px-3 py-2.5 text-right tabular">{row.averagePrice === null ? t('common.notProvided') : formatNumber(row.averagePrice, { maximumFractionDigits: 8 })}</td>
                      <td className="px-3 py-2.5">{row.status}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
        </section>
      </PanelBody>
    </Panel>
  )
}

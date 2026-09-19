'use client'

import { HistoryCollectionsView, unsupportedHistoryProjection, type HistoryProjection } from '@/components/history/history-collections-view'
import { useWpeRuntime, type RuntimeHistoricalCollection, type WpeRuntimeState } from '@/components/runtime-bridge'
import { PageHeader } from '@/components/shell/page-header'
import { StatusBadge } from '@/components/ui/status-badge'

function pageMetadata<T>(collection: RuntimeHistoricalCollection<T>) {
  return {
    state: collection.state,
    message: collection.message,
    source: collection.source,
    sourceUpdatedAtUtc: collection.sourceUpdatedAtUtc ?? undefined,
    pageNumber: 1,
    hasPreviousPage: false,
    hasNextPage: collection.state === 'available' && collection.nextCursor !== null,
  }
}

export function projectRuntimeHistory(runtime: WpeRuntimeState): HistoryProjection {
  const orders = runtime.historicalOrders
  const equity = runtime.historicalEquity
  const backtests = runtime.historicalBacktests
  const skillCalls = runtime.historicalSkillCalls
  const auditEvents = runtime.historicalAuditEvents
  const postTradeReviews = runtime.historicalPostTradeReviews
  const reconciliations = runtime.historicalReconciliations

  return {
    orders: orders ? { ...pageMetadata(orders), items: orders.state === 'available' ? orders.items.map(({ sequence, occurredAtUtc, symbol, side, action, quantity, averagePrice, status }) => ({ sequence, occurredAtUtc, symbol, side, action, quantity, averagePrice, status })) : [] } : unsupportedHistoryProjection.orders,
    equity: equity ? { ...pageMetadata(equity), items: equity.state === 'available' ? equity.items.map(({ sequence, observedAtUtc, equity: value, availableBalance, environment, providerId }) => ({ sequence, observedAtUtc, equity: value, availableBalance, environment, providerId })) : [] } : unsupportedHistoryProjection.equity,
    backtests: backtests ? { ...pageMetadata(backtests), items: backtests.state === 'available' ? backtests.items.map(({ backtestId, completedAtUtc, strategyId, strategyVersion, symbol, status, coverageDays, trades, outOfSampleReturn, maxDrawdown, sharpe }) => ({ backtestId, completedAtUtc: completedAtUtc!, strategyId, strategyVersion, symbol, status, coverageDays, trades, outOfSampleReturn, maxDrawdown, sharpe })) : [] } : unsupportedHistoryProjection.backtests,
    skillCalls: skillCalls ? { ...pageMetadata(skillCalls), items: skillCalls.state === 'available' ? skillCalls.items.map(({ id, occurredAtUtc, skill, status, durationMs, mode, remoteLlmUsed, tokens, costUsd }) => ({ id, occurredAtUtc, skill, status, durationMs, mode, remoteLlmUsed, tokens, costUsd })) : [] } : unsupportedHistoryProjection.skillCalls,
    auditEvents: auditEvents ? { ...pageMetadata(auditEvents), items: auditEvents.state === 'available' ? auditEvents.items.map(({ id, occurredAtUtc, category, source, status }) => ({ id, occurredAtUtc, category, source, status })) : [] } : unsupportedHistoryProjection.auditEvents,
    postTradeReviews: postTradeReviews ? { ...pageMetadata(postTradeReviews), items: postTradeReviews.state === 'available' ? postTradeReviews.items.map(({ closedAtUtc, symbol, side, entryPrice, exitPrice, quantity, fees, feeBasis, totalSlippageAmount, slippageBasis, fundingAmount, fundingBasis, netPnl, returnPct, outcome, strategyId, strategyVersion, attributionBasis }) => ({ closedAtUtc, symbol, side, entryPrice, exitPrice, quantity, fees, feeBasis, totalSlippageAmount, slippageBasis, fundingAmount, fundingBasis, netPnl, returnPct, outcome, strategyId, strategyVersion, attributionBasis })) : [] } : unsupportedHistoryProjection.postTradeReviews,
    reconciliations: reconciliations ? { ...pageMetadata(reconciliations), items: reconciliations.state === 'available' ? reconciliations.items.map(({ kind, schema, observedAtUtc, evaluatedAtUtc, state, allowsRiskIncrease, canonicalSha256 }) => ({ kind, schema, observedAtUtc, evaluatedAtUtc, state, allowsRiskIncrease, canonicalSha256 })) : [] } : unsupportedHistoryProjection.reconciliations,
  }
}

export default function HistoryPage() {
  const runtime = useWpeRuntime()
  return (
    <div className="flex min-w-0 flex-col gap-5">
      <PageHeader
        title="Historical records"
        description="Host-authoritative, read-only collection pages"
        actions={runtime.previewMode ? <StatusBadge token="warning" label="Preview" /> : undefined}
      />
      <HistoryCollectionsView projection={projectRuntimeHistory(runtime)} />
    </div>
  )
}

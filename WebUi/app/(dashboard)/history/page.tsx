'use client'

import { useEffect, useMemo, useRef, useState } from 'react'
import { HistoryCollectionsView, unsupportedHistoryProjection, type HistoryCollectionKey, type HistoryProjection } from '@/components/history/history-collections-view'
import { normalizeHistoricalPageResponse, useWpeRuntime, type RuntimeHistoricalCollection, type WpeRuntimeState } from '@/components/runtime-bridge'
import { postHistoryPageRequest } from '@/lib/host-command'
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
    nextCursor: collection.state === 'available' ? collection.nextCursor : null,
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
    postTradeReviews: postTradeReviews ? { ...pageMetadata(postTradeReviews), items: postTradeReviews.state === 'available' ? postTradeReviews.items.map(({ traceId, closedAtUtc, symbol, side, entryPrice, exitPrice, quantity, fees, feeBasis, totalSlippageAmount, slippageBasis, fundingAmount, fundingBasis, netPnl, returnPct, outcome, strategyId, strategyVersion, attributionBasis, traceState, riskDecision, executionStatus, executionCode, executionAttempts, marketCollectedAtUtc, marketDataVersion, evidenceState, evidenceChain }) => ({ traceId, closedAtUtc, symbol, side, entryPrice, exitPrice, quantity, fees, feeBasis, totalSlippageAmount, slippageBasis, fundingAmount, fundingBasis, netPnl, returnPct, outcome, strategyId, strategyVersion, attributionBasis, traceState, riskDecision, executionStatus, executionCode, executionAttempts, marketCollectedAtUtc, marketDataVersion, evidenceState, evidenceChain: evidenceChain.map(({ stage, status, canonicalSha256, asOfUtc }) => ({ stage, status, canonicalSha256, asOfUtc })) })) : [] } : unsupportedHistoryProjection.postTradeReviews,
    reconciliations: reconciliations ? { ...pageMetadata(reconciliations), items: reconciliations.state === 'available' ? reconciliations.items.map(({ kind, traceId, schema, observedAtUtc, evaluatedAtUtc, state, allowsRiskIncrease, canonicalSha256 }) => ({ kind, traceId, schema, observedAtUtc, evaluatedAtUtc, state, allowsRiskIncrease, canonicalSha256 })) : [] } : unsupportedHistoryProjection.reconciliations,
  }
}

const historyKeys:HistoryCollectionKey[]=['orders','postTradeReviews','reconciliations','equity','backtests','skillCalls','auditEvents']
type AnyHistoryPage=HistoryProjection[HistoryCollectionKey]
type HistoryOverride={page:AnyHistoryPage;anchorCursor:string}
type PendingHistoryRequest={kind:HistoryCollectionKey;current:AnyHistoryPage;stack:AnyHistoryPage[];anchorCursor:string}

export default function HistoryPage() {
  const runtime=useWpeRuntime()
  const baseProjection=useMemo(()=>projectRuntimeHistory(runtime),[runtime])
  const baseRef=useRef(baseProjection)
  const [overrides,setOverrides]=useState<Partial<Record<HistoryCollectionKey,HistoryOverride>>>({})
  const [stacks,setStacks]=useState<Partial<Record<HistoryCollectionKey,AnyHistoryPage[]>>>({})
  const [pendingKinds,setPendingKinds]=useState<ReadonlySet<HistoryCollectionKey>>(new Set())
  const pendingRef=useRef(new Map<string,PendingHistoryRequest>())

  useEffect(()=>{baseRef.current=baseProjection},[baseProjection])

  const projection=useMemo(()=>{
    const merged={...baseProjection} as Record<HistoryCollectionKey,AnyHistoryPage>
    for(const key of historyKeys){
      const override=overrides[key],base=baseProjection[key]
      if(override&&base.state==='available'&&override.anchorCursor===base.nextCursor)merged[key]=override.page
    }
    return merged as HistoryProjection
  },[baseProjection,overrides])

  useEffect(()=>{
    const handler=(event:Event)=>{
      const response=normalizeHistoricalPageResponse((event as CustomEvent<unknown>).detail)
      if(!response)return
      const pending=pendingRef.current.get(response.requestId)
      if(!pending||pending.kind!==response.kind)return
      pendingRef.current.delete(response.requestId)
      setPendingKinds(current=>{const next=new Set(current);next.delete(response.kind);return next})
      const base=baseRef.current[response.kind]
      if(base.state!=='available'||base.nextCursor!==pending.anchorCursor)return
      const projected=projectRuntimeHistory(response.runtimePatch as WpeRuntimeState)[response.kind] as AnyHistoryPage
      if(projected.state!=='available'||projected.sourceUpdatedAtUtc!==base.sourceUpdatedAtUtc){
        setOverrides(current=>{const next={...current};delete next[response.kind];return next})
        setStacks(current=>{const next={...current};delete next[response.kind];return next})
        return
      }
      const nextPage={...projected,pageNumber:(pending.current.pageNumber??1)+1,hasPreviousPage:true}
      setStacks(current=>({...current,[response.kind]:[...pending.stack,pending.current]}))
      setOverrides(current=>({...current,[response.kind]:{page:nextPage,anchorCursor:pending.anchorCursor}}))
    }
    window.addEventListener('wpe-history-page',handler as EventListener)
    return()=>window.removeEventListener('wpe-history-page',handler as EventListener)
  },[])

  const nextPage=(kind:HistoryCollectionKey)=>{
    if(pendingKinds.has(kind))return
    const current=projection[kind],base=baseProjection[kind],cursor=current.nextCursor
    if(current.state!=='available'||base.state!=='available'||!cursor)return
    const anchorCursor=base.nextCursor,active=overrides[kind]
    if(!anchorCursor)return
    const stack=active?.anchorCursor===anchorCursor?[...(stacks[kind]??[])]:[]
    const requestId=window.crypto.randomUUID()
    pendingRef.current.set(requestId,{kind,current,stack,anchorCursor})
    setPendingKinds(currentPending=>new Set(currentPending).add(kind))
    if(!postHistoryPageRequest(requestId,kind,cursor)){
      pendingRef.current.delete(requestId)
      setPendingKinds(currentPending=>{const next=new Set(currentPending);next.delete(kind);return next})
    }
  }

  const previousPage=(kind:HistoryCollectionKey)=>{
    if(pendingKinds.has(kind))return
    const active=overrides[kind],base=baseProjection[kind]
    if(!active||active.anchorCursor!==base.nextCursor)return
    const stack=stacks[kind]??[],previous=stack.at(-1)
    if(!previous)return
    const remaining=stack.slice(0,-1)
    setStacks(current=>({...current,[kind]:remaining}))
    setOverrides(current=>{
      const next={...current}
      if(remaining.length===0&&(previous.pageNumber??1)===1)delete next[kind]
      else next[kind]={page:previous,anchorCursor:active.anchorCursor}
      return next
    })
  }

  return (
    <div className="flex min-w-0 flex-col gap-5">
      <PageHeader
        title="Historical records"
        description="Host-authoritative, read-only collection pages"
        actions={runtime.previewMode ? <StatusBadge token="warning" label="Preview" /> : undefined}
      />
      <HistoryCollectionsView projection={projection} onPreviousPage={previousPage} onNextPage={nextPage} pendingCollections={pendingKinds} />
    </div>
  )
}

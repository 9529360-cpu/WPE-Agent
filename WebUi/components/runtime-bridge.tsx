'use client'

import { createContext, useContext, useEffect, useMemo, useState } from 'react'

export type WpeRuntimeState = {
  contractVersion?: string
  freshness?: { fresh: boolean; ageSeconds: number; staleAfterSeconds: number }
  collectionStates?: { account?: RuntimeCollectionState; positions?: RuntimeCollectionState; orders?: RuntimeCollectionState; risk?: RuntimeCollectionState; strategies?: RuntimeCollectionState; backtests?: RuntimeCollectionState; crossAssetResearch?:RuntimeCollectionState; distribution?:RuntimeCollectionState; telemetry?: RuntimeCollectionState; markets?: RuntimeCollectionState; publicMarkets?: RuntimeCollectionState; publicKlines?: RuntimeCollectionState; capabilities?: RuntimeCollectionState; llmGovernance?: RuntimeCollectionState; plugins?: RuntimeCollectionState; auditEvents?: RuntimeCollectionState; equityHistory?: RuntimeCollectionState; equityMarkets?: RuntimeCollectionState; equityBroker?: RuntimeCollectionState; historicalOrders?:RuntimeCollectionState; historicalEquity?:RuntimeCollectionState; historicalBacktests?:RuntimeCollectionState; historicalSkillCalls?:RuntimeCollectionState; historicalAuditEvents?:RuntimeCollectionState; historicalPostTradePnlDrift?:RuntimeCollectionState; connectionStatus?: RuntimeCollectionState; strategyRegistry?: RuntimeCollectionState; strategyLifecycleEvents?: RuntimeCollectionState; skillCalls?: RuntimeCollectionState; memoryStatus?:RuntimeCollectionState; recentMemoryRetrievals?:RuntimeCollectionState; agentOperations?:RuntimeCollectionState; agentHandoffs?:RuntimeCollectionState; teacherLessons?:RuntimeCollectionState; teacherRecommendations?:RuntimeCollectionState; teacherCorrections?:RuntimeCollectionState; teacherOutcomes?:RuntimeCollectionState; notificationStatus?:RuntimeCollectionState; notificationOutbox?:RuntimeCollectionState; telegramSubscribers?:RuntimeCollectionState; authorizationMode?: RuntimeCollectionState; automaticExecutions?:RuntimeCollectionState; pendingApprovals?: RuntimeCollectionState; securityStorage?:RuntimeCollectionState }
  collectionMessages?: { positions?: string; orders?: string; backtests?: string; equityHistory?: string; equityMarkets?: string; equityBroker?: string; connectionStatus?: string; strategyRegistry?: string; strategyLifecycleEvents?: string }
  positions?: RuntimePosition[]
  orders?: RuntimeOrder[]
  backtests?: RuntimeBacktest[]
  previewMode?: boolean
  aiRuntimeMode?: string
  aiRuntimeEffectiveMode?: string
  brainRemoteAllowed?: boolean
  brainFallbackReason?: string
  activeBrainProvider?: string
  activeBrainModel?: string
  btcPrice?: number
  ethPrice?: number
  btcTrend?: number
  ethTrend?: number
  marketRegime?: string
  brainConfidence?: number
  decisionScore?: number
  riskLoad?: number
  conflictRate?: number
  walletBalance?: number
  availableBalance?: number
  positionQuantity?: number
  workflowNode?: string
  status?: string
  agentIsRunning?: boolean
  nextCycleAtUtc?: string
  environment?: string
  thinkingProgress?: number
  reflectionStatus?: string
  reviewerStatus?: string
  riskApprovalStatus?: string
  executionApprovalStatus?: string
  dataQualityScore?: number
  liquidityScore?: number
  volatilityPercent?: number
  researchScore?: number
  historicalCoverageDays?: number
  missingConditions?: string
  riskSummary?: string
  signalContributions?: Record<string, number>
  lastUpdated?: string
  lastDecision?: string
  lastReason?: string
  decisionAuditSummary?: string
  strategyStatus?: string
  strategySummary?: string
  strategyCandidates?: number
  plannedEntry?: number
  plannedStop?: number
  plannedTakeProfit?: number
  plannedQuantity?: number
  riskRewardRatio?: number
  exchangeConnected?: boolean
  brainConnected?: boolean
  apiTradePermission?: boolean
  riskReady?: boolean
  loggedInUser?: string
  realtimeStatus?: string
  runtimeRecoveryStatus?: string
  newsFullTextDocuments?: number
  newsCorroboratingSources?: number
  positionsSummary?: string
  ordersSummary?: string
  marketSummary?: string
  newsSummary?: string
  decisionDiagnostics?: string
  dailyPnl?: number
  maxDrawdown?: number
  runtimeEventSequence?: number
  runtimeHeartbeatAtUtc?: string
  runtimeFresh?: boolean
  runtimeAgeSeconds?: number
  runtimeProviderId?: string
  sourceTimestampUtc?: string
  snapshotTimestampUtc?: string
  runtimeDiagnosticReason?: string
  hostConnected?: boolean
  agentStartAllowed?: boolean
  agentStopAllowed?: boolean
  /** Optional host-provided market catalog. Missing means unsupported, never inferred. */
  markets?: RuntimeMarket[]
  publicMarkets?: RuntimePublicMarketCollection
  publicKlines?: RuntimePublicKlineCollection
  /** Optional host-provided exchange/provider capability catalog. */
  capabilities?: RuntimeCapability[]
  /** Host-authoritative LLM usage and policy counters. Missing means unsupported. */
  llmGovernance?: RuntimeLlmGovernanceCollection
  /** Host-authoritative, read-only plugin registry. Missing means unsupported. */
  plugins?: RuntimePluginCollection
  /** Host-authoritative, read-only audit timeline. Missing means unsupported. */
  auditEvents?: RuntimeAuditEventCollection
  /** Host-authoritative, provider-neutral account equity history. */
  equityHistory?: RuntimeEquityPoint[]
  /** Host-authoritative, non-sensitive access readiness projection. */
  connectionStatus?: RuntimeConnectionStatusCollection
  strategyRegistry?: RuntimeStrategyProfile[]
  strategyLifecycleEvents?: RuntimeStrategyLifecycleEvent[]
  skillCalls?: RuntimeSkillCall[]
  diagnostic?: RuntimeDiagnosticCollection
  memoryStatus?:RuntimeMemoryStatusCollection
  recentMemoryRetrievals?:RuntimeMemoryRetrieval[]
  agentOperations?:RuntimeAgentOperation[]
  agentHandoffs?:RuntimeAgentHandoff[]
  teacherLessons?:RuntimeTeacherLesson[]
  teacherRecommendations?:RuntimeTeacherRecommendation[]
  teacherCorrections?:RuntimeTeacherCorrection[]
  teacherOutcomes?:RuntimeTeacherOutcome[]
  notificationStatus?:RuntimeNotificationStatusCollection
  notificationOutbox?:RuntimeNotificationOutboxRow[]
  telegramSubscribers?:RuntimeTelegramSubscriber[]
  authorizationMode?: RuntimeAuthorizationModeCollection
  automaticExecutions?: RuntimeAutomaticExecutionCollection
  securityStorage?:RuntimeSecurityStorageCollection
  pendingApprovals?: RuntimePendingApprovalsCollection
  research?:RuntimeCrossAssetResearchCollection
  distribution?:RuntimeDistributionCollection
  equities?: RuntimeEquityMarketCollection
  equityBroker?: RuntimeEquityBrokerCollection
  historicalOrders?:RuntimeHistoricalCollection<RuntimeHistoricalOrder>
  historicalEquity?:RuntimeHistoricalCollection<RuntimeHistoricalEquity>
  historicalBacktests?:RuntimeHistoricalCollection<RuntimeBacktest>
  historicalSkillCalls?:RuntimeHistoricalCollection<RuntimeSkillCall>
  historicalAuditEvents?:RuntimeHistoricalCollection<RuntimeHistoricalAuditEvent>
  historicalPostTradePnlDrift?:RuntimeHistoricalCollection<RuntimeHistoricalPostTradePnlDrift>
}

export type RuntimeCollectionState = 'available' | 'unsupported' | 'stale' | 'error'

export type RuntimeEquityMarket = { instrumentId:string; venueId:string; currency:string; lastPrice:number; bid:number|null; ask:number|null; sessionState:string; haltStatus:string; observedAtUtc:string; providerId:string }
export type RuntimeEquityMarketCollection = { state:RuntimeCollectionState; items:Array<{instrumentId:string;symbol:string;displayName:string;venue:string;currency:string;sessionState:string;lastPrice:number|null;changePercent:number|null;observedAtUtc:string;providerId:string}>; message?:string; asOfUtc?:string }
export type RuntimeEquityBrokerCollection = { state:RuntimeCollectionState; message?:string; value?:{providerId:string;environment:string;canReadAccounts:boolean;canReadPositions:boolean;canReadOrders:boolean;canSubmitOrders:boolean;canCancelOrders:boolean;checkedAtUtc:string;reasonCode:string} }
export type RuntimeHistoricalCollection<T>={state:RuntimeCollectionState;items:T[];nextCursor:string|null;sourceUpdatedAtUtc:string|null;source:string;message?:string}
export type RuntimeHistoricalOrder={sequence:number;occurredAtUtc:string;correlationId:string|null;clientOrderId:string|null;symbol:string;side:string;action:string;reduceOnly:boolean;quantity:number;averagePrice:number|null;status:string}
export type RuntimeHistoricalEquity={sequence:number;observedAtUtc:string;equity:number;availableBalance:number;environment:string;providerId:string}
export type RuntimeHistoricalAuditEvent={id:string;occurredAtUtc:string;category:string;source:string;correlationId:string|null;status:string}
export type RuntimeHistoricalPostTradePnlDrift={sequence:number;comparedAtUtc:string;strategyId:string;strategyVersion:string;symbol:string;side:string;closingQuantity:number;state:'GrossComparable'|'FeeAdjustedComparable';actualGrossPnl:number;simulatedGrossPnl:number;observedMinusSimulatedGrossPnl:number;feeAdjustedPnlComparable:boolean;observedMinusSimulatedFeeAdjustedPnl:number|null;netPnlComparable:false;reasonCode:string}

export type RuntimePosition = {
  symbol: string
  side: string
  quantity: number
  entryPrice: number
  unrealizedPnl: number
}

export type RuntimeOrder = {
  orderId: string
  symbol: string
  side: string
  type: string
  status: string
  quantity: number
  price: number | null
}

export type RuntimeEquityPoint = {
  timeUtc: string
  equity: number
  availableBalance: number
  environment: string
  providerId: string
}

export type RuntimeConnectionStatusValue = {
  connectionId: string
  displayName: string
  providerId: string
  environment: string
  credentialStatus: 'configured' | 'missing'
  adapterStatus: 'ready' | 'unavailable'
  lastCheckedAtUtc: string
  ready: boolean
  exchangeConnected: boolean
  tradePermission: boolean | null
  withdrawPermission: boolean | null
  withdrawalWarning?: string | null
}

export type RuntimeConnectionStatusCollection = {
  state: RuntimeCollectionState
  message?: string
  value?: RuntimeConnectionStatusValue
}
export type RuntimeStrategyProfile = { id: string; version: string; symbol: string; family: string; lifecycle: string; lastReason: string; stateChangedAtUtc: string | null; qualityScore: number }
export type RuntimeStrategyLifecycleEvent = { id: number; strategyId: string; fromState: string; toState: string; occurredAtUtc: string; reason: string }
export type RuntimeSkillCall = { id:string; occurredAtUtc:string; skill:string; status:string; durationMs:number; mode:string|null; remoteLlmUsed:boolean|null; tokens:number|null; costUsd:number|null }
export type RuntimeDiagnosticCollection={state:RuntimeCollectionState;value?:{code:string;timeUtc:string;summary:string}}
export type RuntimeMemoryStatusCollection={state:RuntimeCollectionState;message?:string;value?:{workingCount:number;episodicCount:number;longTermCount:number;lastRetrievedAtUtc:string|null}}
export type RuntimeMemoryRetrieval={id:number;tier:string;symbol:string|null;providerId:string|null;strategyId:string|null;occurredAtUtc:string;result:string;source:string;resultCount:number}
export type RuntimeAgentOperation={roleId:string;status:'running'|'monitoring'|'waiting'|'degraded'|'stopped';lastActivityAtUtc:string|null;activity:string|null;mode:'Local Only'|'Hybrid'|'AI Research'}
export type RuntimeAgentHandoff={id:string;occurredAtUtc:string;sourceRoleId:string;targetRoleId:string;result:string}
export type RuntimeTeacherBlock={blockId:string;kind:string;heading:string;content:string;evidenceHashes:string[];sha256:string}
export type RuntimeTeacherLesson={lessonId:string;kind:string;timeZoneId:string;scheduledForUtc:string;generatedAtUtc:string;language:string;teachingLevel:string;personaVersion:string;blocks:RuntimeTeacherBlock[];contentSha256:string;executionAuthority:false}
export type RuntimeTeacherRecommendation={recommendationId:string;version:number;instrument:string;assetClass:string;state:string;horizon:string;issuedAtUtc:string;expiresAtUtc:string;thesis:string;evidenceHashes:string[];confirmationConditions:string[];invalidationConditions:string[];materialRisks:string[];supersedesId:string|null;executionAuthority:false}
export type RuntimeTeacherCorrection={correctionId:string;supersededLessonId:string;reasonCode:string;issuedAtUtc:string;replacementBlocks:RuntimeTeacherBlock[];sha256:string}
export type RuntimeTeacherOutcome={outcomeId:string;recommendationId:string;recommendationVersion:number;instrument:string;benchmark:string;horizon:string;issuedAtUtc:string;evaluatedAtUtc:string;instrumentReturnPct:number;benchmarkReturnPct:number;relativeReturnPct:number;maximumFavorableExcursionPct:number;maximumAdverseExcursionPct:number;invalidatedBeforeHorizon:boolean;processState:string;canonicalSha256:string}
export type RuntimeNotificationStatus={enabled:boolean;telegramStored:boolean;telegramReady:boolean;whatsAppStored:boolean;whatsAppReady:boolean;legacyMigrationPending:boolean;legacyMigrationDiagnosticCode:string|null;eventKinds:string[];quietHoursEnabled:boolean;quietHoursStart:string;quietHoursEnd:string;quietHoursTimeZone:string;pendingCount:number;retryingCount:number;sentCount:number;deadLetterCount:number}
export type RuntimeNotificationStatusCollection={state:RuntimeCollectionState;message?:string;value?:RuntimeNotificationStatus}
export type RuntimeNotificationOutboxRow={id:number;channel:'Telegram'|'WhatsApp';kind:string;uiState:'Pending'|'Retrying'|'Sent'|'DeadLetter';inFlight:boolean;attempts:number;maxAttempts:number;occurredAtUtc:string;nextAttemptAtUtc:string;updatedAtUtc:string;diagnosticCode:string|null}
export type RuntimeTelegramSubscriber={subscriberId:string;chatType:string;state:'Pending'|'Approved'|'Disabled';eventKinds:string[];firstSeenAtUtc:string;updatedAtUtc:string}
export type RuntimeAuthorizationMode = 'Research' | 'Signal' | 'Review' | 'Auto'
export type RuntimeAuthorizationModeCollection = { state: RuntimeCollectionState; message?: string; value?: { mode: RuntimeAuthorizationMode } }
export type RuntimeAutomaticExecution = { executionId:string; status:string; code:string; attemptCount:number; updatedAtUtc:string }
export type RuntimeAutomaticExecutionCollection = { state:RuntimeCollectionState; message?:string; items:RuntimeAutomaticExecution[] }
export type RuntimeSecurityStorage={state:'Unknown'|'Ready'|'Committed'|'Failed';reasonCode:string;envelopeVersion:number;recordCount:number;evidenceSha256:string|null}
export type RuntimeSecurityStorageCollection={state:RuntimeCollectionState;message?:string;value?:RuntimeSecurityStorage}
export type RuntimePendingApproval = { approvalId: string; symbol: string | null; side: string | null; orderType: string | null; quantity: number | null; entryPrice: number | null; stopLoss: number | null; takeProfit: number | null; createdAtUtc: string; expiresAtUtc: string; status: 'Pending' | 'Revoked' | 'Expired' | 'ArtifactUnavailable'; reasonCode: 'approval.awaiting-user' | 'approval.revoked' | 'approval.expired' | 'approval.artifact-unavailable' }
export type RuntimePendingApprovalsCollection = { state: RuntimeCollectionState; message?: string; items: RuntimePendingApproval[] }
export type RuntimeCrossAssetResearchMetrics={observations:number;trades:number;totalReturn:number;outOfSampleReturn:number;maximumDrawdown:number;sharpe:number;walkForwardScore:number;monteCarloLossProbability:number}
export type RuntimeResearchAlignmentSummary={required:boolean;venueCount:number;alignmentPointCount:number;synchronizationWindowSeconds:number|null;maximumTimestampDeviationSeconds:number|null;forwardFillAllowed:boolean}
export type RuntimeMultipleTestingSummary={trialCount:number;correctionMethod:string;nominalAlpha:number;correctedSignificanceThreshold:number;holdoutUntouched:boolean;holdoutUsedForSelection:boolean;holdoutEvaluationCount:number}
export type RuntimeCrossAssetResearch={researchId:string;strategyId:string;instrumentId:string;assetClass:string;status:'Available'|'Unsupported';lifecycle:'Draft';passed:boolean;reasonCodes:string[];metrics:RuntimeCrossAssetResearchMetrics|null;artifactHash:string;datasetHash:string;parameterHash:string;codeVersion:string;strategyVersion:string;seed:number;outOfSampleStartsAtUtc:string|null;outOfSampleEndsAtUtc:string|null;oosPurgeObservations:number;oosEmbargoObservations:number;alignment:RuntimeResearchAlignmentSummary;multipleTesting:RuntimeMultipleTestingSummary|null;evaluatedAtUtc:string}
export type RuntimeCrossAssetResearchCollection={state:RuntimeCollectionState;items:RuntimeCrossAssetResearch[];message?:string;sourceUpdatedAtUtc?:string}
export type RuntimeDistribution={status:'Denied'|'Authorized'|'Error';allowed:boolean;approvalRoles:Array<'PrimaryReviewer'|'SecondaryReviewer'>;validUntilUtc:string|null;withdrawn:boolean;receiptHash:string|null;policyHash:string|null;contentFactsHash:string|null;consentScopeHash:string|null;consentVersion:string|null;suitabilityVersion:string|null;auditCorrelationHash:string|null;reasonCodes:string[];asOfUtc:string}
export type RuntimeDistributionCollection={state:RuntimeCollectionState;message?:string;value?:RuntimeDistribution}

export type RuntimeBacktest = {
  backtestId: string
  strategyId: string
  strategyVersion: string
  symbol: string
  completedAtUtc: string | null
  status: string
  coverageDays: number
  trades: number
  outOfSampleReturn: number
  maxDrawdown: number
  sharpe: number
}

export type RuntimeMarket = {
  exchangeId: string
  providerId: string
  symbol: string
  nativeSymbol?: string
  price?: number
  changePercent?: number
  trend?: number
  regime?: string
  state?: string
}

export type RuntimePublicMarketTicker = { symbol:string; price:number|null; changePercent:number|null; volume:number|null; source:string; updatedAt:string|null; stale:boolean; state:'Unknown'|'Available'|'Stale' }
export type RuntimePublicMarketCollection = { state:RuntimeCollectionState; items:RuntimePublicMarketTicker[]; message?:string }
export type RuntimePublicMarketKline = { symbol:string; interval:string; close:number|null; volume:number|null; source:string; updatedAt:string|null; stale:boolean; state:'Unknown'|'Available'|'Stale' }
export type RuntimePublicKlineCollection = { state:RuntimeCollectionState; items:RuntimePublicMarketKline[]; message?:string }

export type RuntimeCapability = {
  exchangeId: string
  providerId: string
  canonicalSymbol: string
  nativeSymbol: string
  marketType?: string
  status: string
  canTrade: boolean
  testnetAvailable: boolean
}

export type RuntimeLlmGovernanceValue = {
  mode: string
  remoteAllowed: boolean
  calls: number
  tokens: number
  costUsd: number
  cacheHits: number
  budgetBlocks: number
  fallbacks: number
  privacyBlocks: number
  offlineCompletions: number
  topProvider?: string
  topPurpose?: string
  topAgent?: string
  topTool?: string
  lastCallAtUtc?: string
}

export type RuntimeLlmGovernanceCollection = {
  state: RuntimeCollectionState
  message?: string
  value?: RuntimeLlmGovernanceValue
}

export type RuntimePluginType = 'exchange-adapter' | 'data-source' | 'notification'

export type RuntimePlugin = {
  id: string
  name: string
  type: RuntimePluginType
  version: string
  publisherId: string
  publisherName: string
  entryKind: 'wpe-contract'
  permissions: string[]
  enabled: boolean
  defaultEnabled: boolean
  active: boolean
  runtimeStatus: 'running' | 'stale' | 'unavailable' | 'not-configured'
  testnetOnly: boolean
  compatibilityStatus: 'compatible' | 'incompatible'
  signatureStatus: 'metadataPresent'
  riskLevel: 'low' | 'medium' | 'high'
  statusMessage: string | null
}

export type RuntimePluginCollection = {
  state: RuntimeCollectionState
  items: RuntimePlugin[]
  message?: string
}

export type RuntimeAuditEventCategory = 'decision' | 'risk' | 'execution' | 'recovery' | 'system'

export type RuntimeAuditEventV1 = {
  id: string
  timeUtc: string
  category: RuntimeAuditEventCategory
  source: string
  correlationId?: string | null
  status: string
  summary: string
}

export type RuntimeAuditEventCollection = {
  state: RuntimeCollectionState
  items: RuntimeAuditEventV1[]
  message?: string
}

export const RUNTIME_CONTRACT_VERSION = '1.0'

const collectionStateValues = new Set<RuntimeCollectionState>(['available', 'unsupported', 'stale', 'error'])

function normalizeCollectionState(input: unknown): RuntimeCollectionState {
  const state = typeof input === 'string' ? input.toLowerCase() : ''
  return collectionStateValues.has(state as RuntimeCollectionState) ? state as RuntimeCollectionState : 'error'
}

function finiteNumber(input: unknown): number | null {
  return typeof input === 'number' && Number.isFinite(input) ? input : null
}

function nonEmptyString(input: unknown): string | null {
  return typeof input === 'string' && input.trim() ? input.trim() : null
}

function normalizePosition(input: unknown): RuntimePosition | null {
  if (!input || typeof input !== 'object') return null
  const item = input as Record<string, unknown>
  const symbol = nonEmptyString(item.symbol)
  const side = nonEmptyString(item.side)
  const quantity = finiteNumber(item.quantity)
  const entryPrice = finiteNumber(item.entryPrice)
  const unrealizedPnl = finiteNumber(item.unrealizedPnl)
  if (!symbol || !side || quantity === null || quantity < 0 || entryPrice === null || entryPrice < 0 || unrealizedPnl === null) return null
  return { symbol, side, quantity, entryPrice, unrealizedPnl }
}

function normalizeOrder(input: unknown): RuntimeOrder | null {
  if (!input || typeof input !== 'object') return null
  const item = input as Record<string, unknown>
  const orderId = nonEmptyString(item.orderId)
  const symbol = nonEmptyString(item.symbol)
  const side = nonEmptyString(item.side)
  const type = nonEmptyString(item.type)
  const status = nonEmptyString(item.status)
  const quantity = finiteNumber(item.quantity)
  const priceMissing = item.price === null || item.price === undefined
  const price = priceMissing ? null : finiteNumber(item.price)
  if (!orderId || !symbol || !side || !type || !status || quantity === null || quantity < 0 || (!priceMissing && price === null) || (price !== null && price < 0)) return null
  return { orderId, symbol, side, type, status, quantity, price }
}

function normalizeEquityPoint(input: unknown): RuntimeEquityPoint | null {
  if (!input || typeof input !== 'object') return null
  const item = input as Record<string, unknown>
  const timeUtc = nonEmptyString(item.timeUtc)
  const equity = finiteNumber(item.equity)
  const availableBalance = finiteNumber(item.availableBalance)
  const environment = nonEmptyString(item.environment)
  const providerId = nonEmptyString(item.providerId)
  if (!timeUtc || Number.isNaN(Date.parse(timeUtc)) || equity === null || equity < 0 || availableBalance === null || availableBalance < 0 || !environment || !providerId) return null
  return { timeUtc, equity, availableBalance, environment, providerId }
}

function normalizeEquityMarket(input:unknown):RuntimeEquityMarket|null{if(!input||typeof input!=='object')return null;const x=input as Record<string,unknown>;const instrumentId=nonEmptyString(x.instrumentId),venueId=nonEmptyString(x.venueId),currency=nonEmptyString(x.currency),sessionState=nonEmptyString(x.sessionState),haltStatus=nonEmptyString(x.haltStatus),observedAtUtc=nonEmptyString(x.observedAtUtc),providerId=nonEmptyString(x.providerId),lastPrice=finiteNumber(x.lastPrice);const bid=x.bid===null?null:finiteNumber(x.bid),ask=x.ask===null?null:finiteNumber(x.ask);if(!instrumentId||!venueId||!currency||!sessionState||!haltStatus||!observedAtUtc||Number.isNaN(Date.parse(observedAtUtc))||!providerId||lastPrice===null||lastPrice<0||bid===undefined||ask===undefined||(bid!==null&&bid<0)||(ask!==null&&ask<0))return null;return{instrumentId,venueId,currency,lastPrice,bid,ask,sessionState,haltStatus,observedAtUtc,providerId}}

function normalizePublicMarketTicker(input:unknown):RuntimePublicMarketTicker|null{if(!input||typeof input!=='object')return null;const x=input as Record<string,unknown>,symbol=nonEmptyString(x.symbol),source=nonEmptyString(x.source),state=nonEmptyString(x.state),price=x.price===null?null:finiteNumber(x.price),changePercent=x.changePercent===null?null:finiteNumber(x.changePercent),volume=x.volume===null?null:finiteNumber(x.volume),updatedAt=x.updatedAt===null?null:nonEmptyString(x.updatedAt);if(!symbol||!source||!state||!['Unknown','Available','Stale'].includes(state)||typeof x.stale!=='boolean'||price===undefined||changePercent===undefined||volume===undefined||updatedAt===undefined||(updatedAt!==null&&Number.isNaN(Date.parse(updatedAt)))||(price!==null&&price<=0)||(volume!==null&&volume<0))return null;if(state==='Unknown'&&(price!==null||changePercent!==null||volume!==null||updatedAt!==null||x.stale))return null;if(state==='Available'&&(price===null||updatedAt===null||x.stale))return null;if(state==='Stale'&&(updatedAt===null||!x.stale))return null;return{symbol,price,changePercent,volume,source,updatedAt,stale:x.stale,state:state as RuntimePublicMarketTicker['state']}}

function normalizePublicMarkets(input:unknown):RuntimePublicMarketCollection{const value=input&&typeof input==='object'?input as Record<string,unknown>:{};const state=normalizeCollectionState(value.state),source=Array.isArray(value.items)?value.items:[],items=source.map(normalizePublicMarketTicker);const message=nonEmptyString(value.message)??undefined;if(items.some(item=>item===null))return{state:'error',items:[],message:'Public market collection contains invalid items.'};return{state,items:items as RuntimePublicMarketTicker[],message}}

function normalizePublicMarketKline(input:unknown):RuntimePublicMarketKline|null{if(!input||typeof input!=='object')return null;const x=input as Record<string,unknown>,symbol=nonEmptyString(x.symbol),interval=nonEmptyString(x.interval),source=nonEmptyString(x.source),state=nonEmptyString(x.state),close=x.close===null?null:finiteNumber(x.close),volume=x.volume===null?null:finiteNumber(x.volume),updatedAt=x.updatedAt===null?null:nonEmptyString(x.updatedAt);if(!symbol||!interval||!source||!state||!['Unknown','Available','Stale'].includes(state)||typeof x.stale!=='boolean'||(updatedAt!==null&&Number.isNaN(Date.parse(updatedAt)))||(close!==null&&close<=0)||(volume!==null&&volume<0))return null;if(state==='Unknown'&&(close!==null||volume!==null||updatedAt!==null||x.stale))return null;if(state==='Available'&&(close===null||updatedAt===null||x.stale))return null;if(state==='Stale'&&(updatedAt===null||!x.stale))return null;return{symbol,interval,close,volume,source,updatedAt,stale:x.stale,state:state as RuntimePublicMarketKline['state']}}

function normalizePublicKlines(input:unknown):RuntimePublicKlineCollection{const value=input&&typeof input==='object'?input as Record<string,unknown>:{};const state=normalizeCollectionState(value.state),source=Array.isArray(value.items)?value.items:[],items=source.map(normalizePublicMarketKline);const message=nonEmptyString(value.message)??undefined;if(items.some(item=>item===null))return{state:'error',items:[],message:'Public market kline collection contains invalid items.'};return{state,items:items as RuntimePublicMarketKline[],message}}

function normalizeEquityBroker(input:unknown):RuntimeEquityBrokerCollection{if(!input||typeof input!=='object')return{state:'unsupported',message:'No paper or sandbox equity broker is connected.'};const x=input as Record<string,unknown>,state=normalizeCollectionState(x.state),message=nonEmptyString(x.message)??undefined;if(state!=='available')return{state,message};if(!x.value||typeof x.value!=='object')return{state:'error',message:'Equity broker capability is marked available without a value.'};const value=x.value as Record<string,unknown>,providerId=nonEmptyString(value.providerId),environment=nonEmptyString(value.environment),checkedAtUtc=nonEmptyString(value.checkedAtUtc),reasonCode=nonEmptyString(value.reasonCode);const flags=['canReadAccounts','canReadPositions','canReadOrders','canSubmitOrders','canCancelOrders'] as const;if(!providerId||!environment||!checkedAtUtc||Number.isNaN(Date.parse(checkedAtUtc))||!reasonCode||flags.some(flag=>typeof value[flag]!=='boolean'))return{state:'error',message:'Equity broker capability contains invalid fields.'};return{state:'available',message,value:{providerId,environment,checkedAtUtc,reasonCode,canReadAccounts:value.canReadAccounts as boolean,canReadPositions:value.canReadPositions as boolean,canReadOrders:value.canReadOrders as boolean,canSubmitOrders:value.canSubmitOrders as boolean,canCancelOrders:value.canCancelOrders as boolean}}}

function normalizeHistoricalOrder(input:unknown):RuntimeHistoricalOrder|null{if(!input||typeof input!=='object')return null;const x=input as Record<string,unknown>,sequence=finiteNumber(x.sequence),occurredAtUtc=nonEmptyString(x.occurredAtUtc),symbol=nonEmptyString(x.symbol),side=nonEmptyString(x.side),action=nonEmptyString(x.action),quantity=finiteNumber(x.quantity),status=nonEmptyString(x.status);const correlationId=x.correlationId===null?null:nonEmptyString(x.correlationId),clientOrderId=x.clientOrderId===null?null:nonEmptyString(x.clientOrderId),averagePrice=x.averagePrice===null?null:finiteNumber(x.averagePrice);if(sequence===null||!Number.isInteger(sequence)||!occurredAtUtc||Number.isNaN(Date.parse(occurredAtUtc))||!symbol||!side||!action||typeof x.reduceOnly!=='boolean'||quantity===null||quantity<0||!status||(averagePrice!==null&&averagePrice<0))return null;return{sequence,occurredAtUtc,correlationId,clientOrderId,symbol,side,action,reduceOnly:x.reduceOnly,quantity,averagePrice,status}}
function normalizeHistoricalEquity(input:unknown):RuntimeHistoricalEquity|null{if(!input||typeof input!=='object')return null;const x=input as Record<string,unknown>,sequence=finiteNumber(x.sequence),observedAtUtc=nonEmptyString(x.observedAtUtc),equity=finiteNumber(x.equity),availableBalance=finiteNumber(x.availableBalance),environment=nonEmptyString(x.environment),providerId=nonEmptyString(x.providerId);if(sequence===null||!Number.isInteger(sequence)||!observedAtUtc||Number.isNaN(Date.parse(observedAtUtc))||equity===null||availableBalance===null||!environment||!providerId)return null;return{sequence,observedAtUtc,equity,availableBalance,environment,providerId}}
function normalizeHistoricalAudit(input:unknown):RuntimeHistoricalAuditEvent|null{if(!input||typeof input!=='object')return null;const x=input as Record<string,unknown>,id=nonEmptyString(x.id),occurredAtUtc=nonEmptyString(x.occurredAtUtc),category=nonEmptyString(x.category),source=nonEmptyString(x.source),status=nonEmptyString(x.status),correlationId=x.correlationId===null?null:nonEmptyString(x.correlationId);if(!id||!occurredAtUtc||Number.isNaN(Date.parse(occurredAtUtc))||!category||!source||!status)return null;return{id,occurredAtUtc,category,source,correlationId,status}}
function normalizeHistoricalPostTradePnlDrift(input:unknown):RuntimeHistoricalPostTradePnlDrift|null{if(!input||typeof input!=='object')return null;const x=input as Record<string,unknown>,sequence=finiteNumber(x.sequence),comparedAtUtc=nonEmptyString(x.comparedAtUtc),strategyId=nonEmptyString(x.strategyId),strategyVersion=nonEmptyString(x.strategyVersion),symbol=nonEmptyString(x.symbol),side=nonEmptyString(x.side),state=nonEmptyString(x.state),closingQuantity=finiteNumber(x.closingQuantity),actualGrossPnl=finiteNumber(x.actualGrossPnl),simulatedGrossPnl=finiteNumber(x.simulatedGrossPnl),grossDelta=finiteNumber(x.observedMinusSimulatedGrossPnl),reasonCode=nonEmptyString(x.reasonCode),feeDelta=x.observedMinusSimulatedFeeAdjustedPnl===null?null:finiteNumber(x.observedMinusSimulatedFeeAdjustedPnl);if(sequence===null||!Number.isInteger(sequence)||!comparedAtUtc||Number.isNaN(Date.parse(comparedAtUtc))||!strategyId||!strategyVersion||!symbol||!side||!state||!['GrossComparable','FeeAdjustedComparable'].includes(state)||closingQuantity===null||closingQuantity<=0||actualGrossPnl===null||simulatedGrossPnl===null||grossDelta===null||typeof x.feeAdjustedPnlComparable!=='boolean'||typeof x.netPnlComparable!=='boolean'||x.netPnlComparable!==false||!reasonCode||(x.feeAdjustedPnlComparable?feeDelta===null:x.observedMinusSimulatedFeeAdjustedPnl!==null))return null;return{sequence,comparedAtUtc,strategyId,strategyVersion,symbol,side,closingQuantity,state:state as RuntimeHistoricalPostTradePnlDrift['state'],actualGrossPnl,simulatedGrossPnl,observedMinusSimulatedGrossPnl:grossDelta,feeAdjustedPnlComparable:x.feeAdjustedPnlComparable,observedMinusSimulatedFeeAdjustedPnl:feeDelta,netPnlComparable:false,reasonCode}}
function normalizeHistoricalCollection<T>(input:unknown,normalize:(item:unknown)=>T|null):RuntimeHistoricalCollection<T>{if(!input||typeof input!=='object')return{state:'unsupported',items:[],nextCursor:null,sourceUpdatedAtUtc:null,source:'local-agent-sqlite'};const x=input as Record<string,unknown>,state=normalizeCollectionState(x.state),message=nonEmptyString(x.message)??undefined,source=nonEmptyString(x.source)??'local-agent-sqlite';if(state!=='available')return{state,items:[],nextCursor:null,sourceUpdatedAtUtc:typeof x.sourceUpdatedAtUtc==='string'?x.sourceUpdatedAtUtc:null,source,message};if(!Array.isArray(x.items))return{state:'error',items:[],nextCursor:null,sourceUpdatedAtUtc:null,source,message:'Historical collection is available without items.'};const items=x.items.map(normalize);if(items.some(item=>item===null))return{state:'error',items:[],nextCursor:null,sourceUpdatedAtUtc:null,source,message:'Historical collection contains invalid items.'};const cursor=x.nextCursor===null?null:nonEmptyString(x.nextCursor),updated=x.sourceUpdatedAtUtc===null?null:nonEmptyString(x.sourceUpdatedAtUtc);if((cursor!==null&&cursor.length>2048)||(updated!==null&&Number.isNaN(Date.parse(updated))))return{state:'error',items:[],nextCursor:null,sourceUpdatedAtUtc:null,source,message:'Historical collection metadata is invalid.'};return{state,items:items as T[],nextCursor:cursor,sourceUpdatedAtUtc:updated,source,message}}

function normalizeBacktest(input: unknown): RuntimeBacktest | null {
  if (!input || typeof input !== 'object') return null
  const item = input as Record<string, unknown>
  const backtestId = nonEmptyString(item.backtestId)
  const strategyId = nonEmptyString(item.strategyId)
  const strategyVersion = nonEmptyString(item.strategyVersion)
  const symbol = nonEmptyString(item.symbol)
  const status = nonEmptyString(item.status)
  const completedAtUtc = item.completedAtUtc === null ? null : nonEmptyString(item.completedAtUtc)
  const coverageDays = finiteNumber(item.coverageDays)
  const trades = finiteNumber(item.trades)
  const outOfSampleReturn = finiteNumber(item.outOfSampleReturn)
  const maxDrawdown = finiteNumber(item.maxDrawdown)
  const sharpe = finiteNumber(item.sharpe)
  if (!backtestId || !strategyId || !strategyVersion || !symbol || !status || !completedAtUtc || Number.isNaN(Date.parse(completedAtUtc)) || coverageDays === null || coverageDays < 0 || !Number.isInteger(coverageDays) || trades === null || trades < 0 || !Number.isInteger(trades) || outOfSampleReturn === null || maxDrawdown === null || maxDrawdown < 0 || sharpe === null) return null
  return { backtestId, strategyId, strategyVersion, symbol, completedAtUtc, status, coverageDays, trades, outOfSampleReturn, maxDrawdown, sharpe }
}
function normalizeStrategyProfile(input: unknown): RuntimeStrategyProfile | null {
  if(!input||typeof input!=='object')return null;const x=input as Record<string,unknown>;const id=nonEmptyString(x.id),version=nonEmptyString(x.version),symbol=nonEmptyString(x.symbol),family=nonEmptyString(x.family),lifecycle=nonEmptyString(x.lifecycle),lastReason=nonEmptyString(x.lastReason);const changed=x.stateChangedAtUtc===null?null:nonEmptyString(x.stateChangedAtUtc);const qualityScore=finiteNumber(x.qualityScore);if(!id||!version||!symbol||!family||!lifecycle||!lastReason||qualityScore===null||(changed!==null&&Number.isNaN(Date.parse(changed))))return null;return{id,version,symbol,family,lifecycle,lastReason,stateChangedAtUtc:changed,qualityScore}
}
function normalizeStrategyEvent(input: unknown): RuntimeStrategyLifecycleEvent | null {
  if(!input||typeof input!=='object')return null;const x=input as Record<string,unknown>;const id=finiteNumber(x.id),strategyId=nonEmptyString(x.strategyId),fromState=nonEmptyString(x.fromState),toState=nonEmptyString(x.toState),occurredAtUtc=nonEmptyString(x.occurredAtUtc),reason=nonEmptyString(x.reason);if(id===null||!Number.isInteger(id)||!strategyId||!fromState||!toState||!occurredAtUtc||Number.isNaN(Date.parse(occurredAtUtc))||!reason)return null;return{id,strategyId,fromState,toState,occurredAtUtc,reason}
}
function normalizeSkillCall(input:unknown):RuntimeSkillCall|null{if(!input||typeof input!=='object')return null;const x=input as Record<string,unknown>,id=nonEmptyString(x.id),occurredAtUtc=nonEmptyString(x.occurredAtUtc),skill=nonEmptyString(x.skill),status=nonEmptyString(x.status),durationMs=finiteNumber(x.durationMs);const mode=x.mode===null?null:nonEmptyString(x.mode);const remoteLlmUsed=x.remoteLlmUsed===null?null:typeof x.remoteLlmUsed==='boolean'?x.remoteLlmUsed:undefined;const tokens=x.tokens===null?null:finiteNumber(x.tokens);const costUsd=x.costUsd===null?null:finiteNumber(x.costUsd);if(!id||!occurredAtUtc||Number.isNaN(Date.parse(occurredAtUtc))||!skill||!status||durationMs===null||durationMs<0||remoteLlmUsed===undefined||(tokens!==null&&(tokens<0||!Number.isInteger(tokens)))||(costUsd!==null&&costUsd<0))return null;return{id,occurredAtUtc,skill,status,durationMs,mode,remoteLlmUsed,tokens,costUsd}}
function normalizeAgentOperation(input:unknown):RuntimeAgentOperation|null{if(!input||typeof input!=='object')return null;const x=input as Record<string,unknown>,roleId=nonEmptyString(x.roleId),status=nonEmptyString(x.status),activity=x.activity===null?null:nonEmptyString(x.activity),mode=nonEmptyString(x.mode),last=x.lastActivityAtUtc===null?null:nonEmptyString(x.lastActivityAtUtc);if(!roleId||!status||!['running','monitoring','waiting','degraded','stopped'].includes(status)||!mode||!['Local Only','Hybrid','AI Research'].includes(mode)||(last!==null&&Number.isNaN(Date.parse(last))))return null;return{roleId,status:status as RuntimeAgentOperation['status'],lastActivityAtUtc:last,activity,mode:mode as RuntimeAgentOperation['mode']}}
function normalizeAgentHandoff(input:unknown):RuntimeAgentHandoff|null{if(!input||typeof input!=='object')return null;const x=input as Record<string,unknown>,id=nonEmptyString(x.id),occurredAtUtc=nonEmptyString(x.occurredAtUtc),sourceRoleId=nonEmptyString(x.sourceRoleId),targetRoleId=nonEmptyString(x.targetRoleId),result=nonEmptyString(x.result);if(!id||!occurredAtUtc||Number.isNaN(Date.parse(occurredAtUtc))||!sourceRoleId||!targetRoleId||sourceRoleId===targetRoleId||!result)return null;return{id,occurredAtUtc,sourceRoleId,targetRoleId,result}}

function normalizeItems<T>(collection: unknown, normalize: (item: unknown) => T | null) {
  const value = collection && typeof collection === 'object' ? collection as Record<string, unknown> : {}
  let state = normalizeCollectionState(value.state)
  const source = Array.isArray(value.items) ? value.items : []
  const items = source.map(normalize).filter((item): item is T => item !== null)
  let message = typeof value.message === 'string' ? value.message : undefined
  if (state === 'available' && items.length !== source.length) {
    state = 'error'
    message = 'Runtime collection contains invalid items.'
    return { state, items: [] as T[], message }
  }
  return { state, items: state === 'available' ? items : [] as T[], message }
}

function normalizeConnectionStatus(input: unknown): RuntimeConnectionStatusCollection {
  if (!input || typeof input !== 'object') return { state: 'unsupported', message: 'The host has not published access readiness.' }
  const collection = input as Record<string, unknown>
  const state = normalizeCollectionState(collection.state)
  const message = nonEmptyString(collection.message) ?? undefined
  if (state !== 'available') return { state, message }
  if (!collection.value || typeof collection.value !== 'object') return { state: 'error', message: 'Connection status is marked available without a value.' }
  const value = collection.value as Record<string, unknown>
  const connectionId = nonEmptyString(value.connectionId)
  const displayName = nonEmptyString(value.displayName)
  const providerId = nonEmptyString(value.providerId)
  const environment = nonEmptyString(value.environment)
  const credentialStatus = nonEmptyString(value.credentialStatus)
  const adapterStatus = nonEmptyString(value.adapterStatus)
  const lastCheckedAtUtc = nonEmptyString(value.lastCheckedAtUtc)
  const ready = typeof value.ready === 'boolean' ? value.ready : null
  const exchangeConnected = typeof value.exchangeConnected === 'boolean' ? value.exchangeConnected : null
  const nullableBoolean = (key: string) => value[key] === null ? null : typeof value[key] === 'boolean' ? value[key] as boolean : undefined
  const tradePermission = nullableBoolean('tradePermission')
  const withdrawPermission = nullableBoolean('withdrawPermission')
  if (!connectionId || !displayName || !providerId || !environment || !lastCheckedAtUtc || Number.isNaN(Date.parse(lastCheckedAtUtc)) || !['configured', 'missing'].includes(credentialStatus ?? '') || !['ready', 'unavailable'].includes(adapterStatus ?? '') || ready === null || exchangeConnected === null || tradePermission === undefined || withdrawPermission === undefined) return { state: 'error', message: 'Connection status contains invalid or incomplete metadata.' }
  return { state: 'available', message, value: { connectionId, displayName, providerId, environment, credentialStatus: credentialStatus as 'configured' | 'missing', adapterStatus: adapterStatus as 'ready' | 'unavailable', lastCheckedAtUtc, ready, exchangeConnected, tradePermission, withdrawPermission, withdrawalWarning: nonEmptyString(value.withdrawalWarning) } }
}
function normalizeNotificationStatus(input:unknown):RuntimeNotificationStatusCollection{if(!input||typeof input!=='object')return{state:'unsupported'};const c=input as Record<string,unknown>,state=normalizeCollectionState(c.state),message=nonEmptyString(c.message)??undefined;if(state!=='available')return{state,message};if(!c.value||typeof c.value!=='object')return{state:'error'};const v=c.value as Record<string,unknown>,bools=['enabled','telegramStored','telegramReady','whatsAppStored','whatsAppReady','quietHoursEnabled'] as const,counts=['pendingCount','retryingCount','sentCount','deadLetterCount'] as const;if(!bools.every(k=>typeof v[k]==='boolean')||!counts.every(k=>{const n=finiteNumber(v[k]);return n!==null&&Number.isInteger(n)&&n>=0})||!Array.isArray(v.eventKinds)||!v.eventKinds.every(x=>typeof x==='string')||!nonEmptyString(v.quietHoursStart)||!nonEmptyString(v.quietHoursEnd)||!nonEmptyString(v.quietHoursTimeZone))return{state:'error'};return{state,message,value:v as unknown as RuntimeNotificationStatus}}
function normalizeNotificationRow(input:unknown):RuntimeNotificationOutboxRow|null{if(!input||typeof input!=='object')return null;const v=input as Record<string,unknown>,id=finiteNumber(v.id),attempts=finiteNumber(v.attempts),maxAttempts=finiteNumber(v.maxAttempts),channel=nonEmptyString(v.channel),kind=nonEmptyString(v.kind),uiState=nonEmptyString(v.uiState),occurredAtUtc=nonEmptyString(v.occurredAtUtc),nextAttemptAtUtc=nonEmptyString(v.nextAttemptAtUtc),updatedAtUtc=nonEmptyString(v.updatedAtUtc),diagnosticCode=v.diagnosticCode===null?null:nonEmptyString(v.diagnosticCode);if(id===null||!Number.isInteger(id)||attempts===null||maxAttempts===null||!Number.isInteger(attempts)||!Number.isInteger(maxAttempts)||!['Telegram','WhatsApp'].includes(channel??'')||!kind||!['Pending','Retrying','Sent','DeadLetter'].includes(uiState??'')||typeof v.inFlight!=='boolean'||!occurredAtUtc||!nextAttemptAtUtc||!updatedAtUtc||[occurredAtUtc,nextAttemptAtUtc,updatedAtUtc].some(x=>Number.isNaN(Date.parse(x)))||diagnosticCode===undefined)return null;return{id,channel:channel as RuntimeNotificationOutboxRow['channel'],kind,uiState:uiState as RuntimeNotificationOutboxRow['uiState'],inFlight:v.inFlight as boolean,attempts,maxAttempts,occurredAtUtc,nextAttemptAtUtc,updatedAtUtc,diagnosticCode}}
function normalizeTelegramSubscriber(input:unknown):RuntimeTelegramSubscriber|null{if(!input||typeof input!=='object')return null;const v=input as Record<string,unknown>,subscriberId=nonEmptyString(v.subscriberId),chatType=nonEmptyString(v.chatType),state=nonEmptyString(v.state),firstSeenAtUtc=nonEmptyString(v.firstSeenAtUtc),updatedAtUtc=nonEmptyString(v.updatedAtUtc);if(!subscriberId||!/^tg#[a-f0-9]{12}$/.test(subscriberId)||!chatType||!state||!['Pending','Approved','Disabled'].includes(state)||!Array.isArray(v.eventKinds)||!v.eventKinds.every(x=>typeof x==='string')||!firstSeenAtUtc||!updatedAtUtc||Number.isNaN(Date.parse(firstSeenAtUtc))||Number.isNaN(Date.parse(updatedAtUtc)))return null;return{subscriberId,chatType,state:state as RuntimeTelegramSubscriber['state'],eventKinds:v.eventKinds as string[],firstSeenAtUtc,updatedAtUtc}}
function normalizeAuthorizationMode(input:unknown):RuntimeAuthorizationModeCollection{if(!input||typeof input!=='object')return{state:'unsupported',message:'The host runtime does not expose trading authorization.'};const c=input as Record<string,unknown>,state=normalizeCollectionState(c.state),message=nonEmptyString(c.message)??undefined;if(state!=='available')return{state,message};if(!c.value||typeof c.value!=='object')return{state:'error',message:'Authorization mode is marked available without a value.'};const mode=nonEmptyString((c.value as Record<string,unknown>).mode);if(!mode||!['Research','Signal','Review','Auto'].includes(mode))return{state:'error',message:'Authorization mode is invalid.'};return{state:'available',message,value:{mode:mode as RuntimeAuthorizationMode}}}
function normalizeAutomaticExecution(input:unknown):RuntimeAutomaticExecution|null{if(!input||typeof input!=='object')return null;const v=input as Record<string,unknown>,executionId=nonEmptyString(v.executionId),status=nonEmptyString(v.status),code=nonEmptyString(v.code),attemptCount=finiteNumber(v.attemptCount),updatedAtUtc=nonEmptyString(v.updatedAtUtc);if(!executionId||!status||!code||attemptCount===null||!Number.isInteger(attemptCount)||attemptCount<0||!updatedAtUtc||Number.isNaN(Date.parse(updatedAtUtc)))return null;return{executionId,status,code,attemptCount,updatedAtUtc}}
function normalizeSecurityStorage(input:unknown):RuntimeSecurityStorageCollection{if(!input||typeof input!=='object')return{state:'unsupported',message:'The host runtime does not expose security storage status.'};const c=input as Record<string,unknown>,state=normalizeCollectionState(c.state),message=nonEmptyString(c.message)??undefined;if(state!=='available')return{state,message};if(!c.value||typeof c.value!=='object')return{state:'error',message:'Security storage status is marked available without a value.'};const v=c.value as Record<string,unknown>,allowed=new Set(['state','reasonCode','envelopeVersion','recordCount','evidenceSha256']);if(Object.keys(v).some(key=>!allowed.has(key)))return{state:'error',message:'Security storage status contains unsupported fields.'};const runtimeState=nonEmptyString(v.state),reasonCode=nonEmptyString(v.reasonCode),envelopeVersion=finiteNumber(v.envelopeVersion),recordCount=finiteNumber(v.recordCount),evidence=v.evidenceSha256===null?null:nonEmptyString(v.evidenceSha256);if(!runtimeState||!['Unknown','Ready','Committed','Failed'].includes(runtimeState)||!reasonCode||envelopeVersion===null||!Number.isInteger(envelopeVersion)||envelopeVersion<1||recordCount===null||!Number.isInteger(recordCount)||recordCount<0||evidence===undefined||(evidence!==null&&!/^[A-Fa-f0-9]{64}$/.test(evidence)))return{state:'error',message:'Security storage status is invalid.'};return{state:'available',message,value:{state:runtimeState as RuntimeSecurityStorage['state'],reasonCode,envelopeVersion,recordCount,evidenceSha256:evidence}}}
function normalizePendingApproval(input:unknown):RuntimePendingApproval|null{if(!input||typeof input!=='object')return null;const v=input as Record<string,unknown>,sensitive=['userId','deviceId','sessionId','intentHash','approvalReceipt','receipt','secret','destination','payload'];if(sensitive.some(key=>Object.hasOwn(v,key)))return null;const nullableString=(key:string)=>!Object.hasOwn(v,key)?undefined:v[key]===null?null:nonEmptyString(v[key]),nullableNumber=(key:string)=>!Object.hasOwn(v,key)?undefined:v[key]===null?null:finiteNumber(v[key]);const approvalId=nonEmptyString(v.approvalId),symbol=nullableString('symbol'),side=nullableString('side'),orderType=nullableString('orderType'),quantity=nullableNumber('quantity'),entryPrice=nullableNumber('entryPrice'),stopLoss=nullableNumber('stopLoss'),takeProfit=nullableNumber('takeProfit'),createdAtUtc=nonEmptyString(v.createdAtUtc),expiresAtUtc=nonEmptyString(v.expiresAtUtc),status=nonEmptyString(v.status),reasonCode=nonEmptyString(v.reasonCode);const expectedReason:Record<string,string>={Pending:'approval.awaiting-user',Revoked:'approval.revoked',Expired:'approval.expired',ArtifactUnavailable:'approval.artifact-unavailable'};if(!approvalId||!/^approval#[A-Fa-f0-9]{12}$/.test(approvalId)||symbol===undefined||side===undefined||orderType===undefined||quantity===undefined||entryPrice===undefined||stopLoss===undefined||takeProfit===undefined||!createdAtUtc||!expiresAtUtc||Number.isNaN(Date.parse(createdAtUtc))||Number.isNaN(Date.parse(expiresAtUtc))||Date.parse(expiresAtUtc)<=Date.parse(createdAtUtc)||!status||!Object.hasOwn(expectedReason,status)||reasonCode!==expectedReason[status]||[quantity,entryPrice,stopLoss,takeProfit].some(value=>value!==null&&value<=0)||(symbol!==null&&!/^[A-Z0-9]{2,24}$/.test(symbol))||(side!==null&&!['Long','Short'].includes(side))||(orderType!==null&&!['Market','Limit'].includes(orderType)))return null;return{approvalId,symbol,side,orderType,quantity,entryPrice,stopLoss,takeProfit,createdAtUtc,expiresAtUtc,status:status as RuntimePendingApproval['status'],reasonCode:reasonCode as RuntimePendingApproval['reasonCode']}}
function normalizePendingApprovals(input:unknown):RuntimePendingApprovalsCollection{if(!input||typeof input!=='object')return{state:'unsupported',items:[],message:'The host runtime does not expose pending approvals.'};const c=input as Record<string,unknown>,state=normalizeCollectionState(c.state),message=nonEmptyString(c.message)??undefined;if(state!=='available')return{state,items:[],message};if(!Array.isArray(c.items)||c.items.length>100)return{state:'error',items:[],message:'Pending approvals are marked available without a bounded items array.'};const items=c.items.map(normalizePendingApproval);if(items.some(item=>item===null))return{state:'error',items:[],message:'Pending approvals contain invalid items.'};return{state:'available',items:items as RuntimePendingApproval[],message}}
function normalizeResearchMetrics(input:unknown):RuntimeCrossAssetResearchMetrics|null{if(!input||typeof input!=='object')return null;const x=input as Record<string,unknown>,keys=['observations','trades','totalReturn','outOfSampleReturn','maximumDrawdown','sharpe','walkForwardScore','monteCarloLossProbability'] as const,values=Object.fromEntries(keys.map(k=>[k,finiteNumber(x[k])])) as Record<(typeof keys)[number],number|null>;if(keys.some(k=>values[k]===null)||!Number.isInteger(values.observations!)||!Number.isInteger(values.trades!)||values.observations!<0||values.trades!<0)return null;return values as RuntimeCrossAssetResearchMetrics}
function normalizeCrossAssetResearch(input:unknown):RuntimeCrossAssetResearch|null{if(!input||typeof input!=='object')return null;const x=input as Record<string,unknown>,forbidden=['observations','prices','positions','trialParameters','trialPValues','credentials','canonicalPayload','inputReferences','costModel'];if(forbidden.some(k=>Object.hasOwn(x,k)))return null;const researchId=nonEmptyString(x.researchId),strategyId=nonEmptyString(x.strategyId),instrumentId=nonEmptyString(x.instrumentId),assetClass=nonEmptyString(x.assetClass),status=nonEmptyString(x.status),lifecycle=nonEmptyString(x.lifecycle),evaluatedAtUtc=nonEmptyString(x.evaluatedAtUtc);if(!researchId||!strategyId||!instrumentId||!assetClass||!['Available','Unsupported'].includes(status??'')||lifecycle!=='Draft'||typeof x.passed!=='boolean'||!evaluatedAtUtc||Number.isNaN(Date.parse(evaluatedAtUtc))||!Array.isArray(x.reasonCodes)||!x.reasonCodes.every(v=>typeof v==='string'))return null;const stringKeys=['artifactHash','datasetHash','parameterHash','codeVersion','strategyVersion'] as const,strings=Object.fromEntries(stringKeys.map(k=>[k,typeof x[k]==='string'?x[k]:null])) as Record<(typeof stringKeys)[number],string|null>;const seed=finiteNumber(x.seed),purge=finiteNumber(x.oosPurgeObservations),embargo=finiteNumber(x.oosEmbargoObservations);if(stringKeys.some(k=>strings[k]===null)||seed===null||purge===null||embargo===null||![seed,purge,embargo].every(Number.isInteger)||purge<0||embargo<0)return null;const date=(key:string)=>x[key]===null?null:nonEmptyString(x[key]),oosStart=date('outOfSampleStartsAtUtc'),oosEnd=date('outOfSampleEndsAtUtc');if(oosStart===undefined||oosEnd===undefined||[oosStart,oosEnd].some(v=>v!==null&&Number.isNaN(Date.parse(v))))return null;const a=x.alignment;if(!a||typeof a!=='object')return null;const alignment=a as Record<string,unknown>,venueCount=finiteNumber(alignment.venueCount),pointCount=finiteNumber(alignment.alignmentPointCount),sync=alignment.synchronizationWindowSeconds===null?null:finiteNumber(alignment.synchronizationWindowSeconds),deviation=alignment.maximumTimestampDeviationSeconds===null?null:finiteNumber(alignment.maximumTimestampDeviationSeconds);if(typeof alignment.required!=='boolean'||typeof alignment.forwardFillAllowed!=='boolean'||venueCount===null||pointCount===null||![venueCount,pointCount].every(Number.isInteger)||venueCount<0||pointCount<0||sync===null&&alignment.synchronizationWindowSeconds!==null||deviation===null&&alignment.maximumTimestampDeviationSeconds!==null)return null;const metrics=x.metrics===null?null:normalizeResearchMetrics(x.metrics);if(x.metrics!==null&&!metrics)return null;let multipleTesting:RuntimeMultipleTestingSummary|null=null;if(x.multipleTesting!==null){if(!x.multipleTesting||typeof x.multipleTesting!=='object')return null;const m=x.multipleTesting as Record<string,unknown>,trialCount=finiteNumber(m.trialCount),nominalAlpha=finiteNumber(m.nominalAlpha),threshold=finiteNumber(m.correctedSignificanceThreshold),holdoutCount=finiteNumber(m.holdoutEvaluationCount),correctionMethod=nonEmptyString(m.correctionMethod);if(trialCount===null||!Number.isInteger(trialCount)||trialCount<1||nominalAlpha===null||threshold===null||holdoutCount===null||!Number.isInteger(holdoutCount)||holdoutCount<0||!correctionMethod||typeof m.holdoutUntouched!=='boolean'||typeof m.holdoutUsedForSelection!=='boolean'||Object.hasOwn(m,'trialPValues'))return null;multipleTesting={trialCount,correctionMethod,nominalAlpha,correctedSignificanceThreshold:threshold,holdoutUntouched:m.holdoutUntouched,holdoutUsedForSelection:m.holdoutUsedForSelection,holdoutEvaluationCount:holdoutCount}}return{researchId,strategyId,instrumentId,assetClass,status:status as RuntimeCrossAssetResearch['status'],lifecycle:'Draft',passed:x.passed,reasonCodes:x.reasonCodes as string[],metrics,artifactHash:strings.artifactHash!,datasetHash:strings.datasetHash!,parameterHash:strings.parameterHash!,codeVersion:strings.codeVersion!,strategyVersion:strings.strategyVersion!,seed,oosPurgeObservations:purge,oosEmbargoObservations:embargo,outOfSampleStartsAtUtc:oosStart,outOfSampleEndsAtUtc:oosEnd,alignment:{required:alignment.required,venueCount,alignmentPointCount:pointCount,synchronizationWindowSeconds:sync,maximumTimestampDeviationSeconds:deviation,forwardFillAllowed:alignment.forwardFillAllowed},multipleTesting,evaluatedAtUtc}}
function normalizeCrossAssetResearchCollection(input:unknown):RuntimeCrossAssetResearchCollection{const normalized=normalizeItems(input,normalizeCrossAssetResearch);const source=input&&typeof input==='object'?nonEmptyString((input as Record<string,unknown>).sourceUpdatedAtUtc)??undefined:undefined;return{...normalized,sourceUpdatedAtUtc:source}}
function normalizeDistribution(input:unknown):RuntimeDistributionCollection{if(!input||typeof input!=='object')return{state:'unsupported',message:'The host runtime does not expose professional distribution status.'};const c=input as Record<string,unknown>,state=normalizeCollectionState(c.state),message=nonEmptyString(c.message)??undefined;if(state!=='available')return{state,message};if(!c.value||typeof c.value!=='object')return{state:'error',message:'Distribution status is marked available without a value.'};const v=c.value as Record<string,unknown>,forbidden=['recipient','destination','reviewerId','primaryReviewerId','secondaryReviewerId','reviewText','opinion','body','content','message','send','retry','approve','approvalId','configuration','config'];if(forbidden.some(k=>Object.hasOwn(v,k)))return{state:'error',message:'Distribution status contains a forbidden field.'};const status=nonEmptyString(v.status),asOfUtc=nonEmptyString(v.asOfUtc);if(!status||!['Denied','Authorized','Error'].includes(status)||typeof v.allowed!=='boolean'||typeof v.withdrawn!=='boolean'||!asOfUtc||Number.isNaN(Date.parse(asOfUtc))||!Array.isArray(v.approvalRoles)||!v.approvalRoles.every(role=>role==='PrimaryReviewer'||role==='SecondaryReviewer')||!Array.isArray(v.reasonCodes)||!v.reasonCodes.every(reason=>typeof reason==='string'))return{state:'error',message:'Distribution status contains invalid fields.'};if(status==='Error')return{state:'error',message:'Distribution status could not be validated.'};if(v.allowed!== (status==='Authorized'))return{state:'error',message:'Distribution status violates fail-closed semantics.'};const nullable=(key:string)=>v[key]===null?null:nonEmptyString(v[key]),validUntilUtc=nullable('validUntilUtc');if(validUntilUtc!==null&&Number.isNaN(Date.parse(validUntilUtc)))return{state:'error',message:'Distribution validity is invalid.'};const hashes=['receiptHash','policyHash','contentFactsHash','consentScopeHash','auditCorrelationHash'] as const,hashValues=Object.fromEntries(hashes.map(key=>[key,nullable(key)])) as Record<(typeof hashes)[number],string|null>;if(hashes.some(key=>hashValues[key]!==null&&!/^[A-F0-9]{64}$/.test(hashValues[key]!)))return{state:'error',message:'Distribution hash metadata is invalid.'};const consentVersion=nullable('consentVersion'),suitabilityVersion=nullable('suitabilityVersion');if(consentVersion===undefined||suitabilityVersion===undefined)return{state:'error',message:'Distribution version metadata is invalid.'};return{state:'available',message,value:{status:status as RuntimeDistribution['status'],allowed:v.allowed,approvalRoles:v.approvalRoles as RuntimeDistribution['approvalRoles'],validUntilUtc,withdrawn:v.withdrawn,receiptHash:hashValues.receiptHash,policyHash:hashValues.policyHash,contentFactsHash:hashValues.contentFactsHash,consentScopeHash:hashValues.consentScopeHash,consentVersion,suitabilityVersion,auditCorrelationHash:hashValues.auditCorrelationHash,reasonCodes:v.reasonCodes as string[],asOfUtc}}}
function normalizeDiagnostic(input:unknown):RuntimeDiagnosticCollection{if(!input||typeof input!=='object')return{state:'unsupported'};const c=input as Record<string,unknown>,state=normalizeCollectionState(c.state);if(state!=='available'||c.value===null)return{state};if(!c.value||typeof c.value!=='object')return{state:'error'};const v=c.value as Record<string,unknown>,code=nonEmptyString(v.code),timeUtc=nonEmptyString(v.timeUtc),summary=nonEmptyString(v.summary);if(!code||!timeUtc||Number.isNaN(Date.parse(timeUtc))||!summary)return{state:'error'};return{state:'available',value:{code,timeUtc,summary}}}
function normalizeMemoryStatus(input:unknown):RuntimeMemoryStatusCollection{if(!input||typeof input!=='object')return{state:'unsupported'};const c=input as Record<string,unknown>,state=normalizeCollectionState(c.state),message=nonEmptyString(c.message)??undefined;if(state!=='available')return{state,message};if(!c.value||typeof c.value!=='object')return{state:'error'};const v=c.value as Record<string,unknown>,workingCount=finiteNumber(v.workingCount),episodicCount=finiteNumber(v.episodicCount),longTermCount=finiteNumber(v.longTermCount),last=v.lastRetrievedAtUtc===null?null:nonEmptyString(v.lastRetrievedAtUtc);if(workingCount===null||episodicCount===null||longTermCount===null||[workingCount,episodicCount,longTermCount].some(x=>x<0||!Number.isInteger(x))||(last!==null&&Number.isNaN(Date.parse(last))))return{state:'error'};return{state:'available',message,value:{workingCount,episodicCount,longTermCount,lastRetrievedAtUtc:last}}}
function normalizeMemoryRetrieval(input:unknown):RuntimeMemoryRetrieval|null{if(!input||typeof input!=='object')return null;const x=input as Record<string,unknown>,id=finiteNumber(x.id),tier=nonEmptyString(x.tier),occurredAtUtc=nonEmptyString(x.occurredAtUtc),result=nonEmptyString(x.result),source=nonEmptyString(x.source),resultCount=finiteNumber(x.resultCount);const nullable=(key:string)=>x[key]===null?null:nonEmptyString(x[key]);const symbol=nullable('symbol'),providerId=nullable('providerId'),strategyId=nullable('strategyId');if(id===null||!Number.isInteger(id)||!tier||!occurredAtUtc||Number.isNaN(Date.parse(occurredAtUtc))||!result||!source||resultCount===null||!Number.isInteger(resultCount)||resultCount<0||symbol===undefined||providerId===undefined||strategyId===undefined)return null;return{id,tier,symbol,providerId,strategyId,occurredAtUtc,result,source,resultCount}}

function normalizeLlmGovernance(input: unknown): RuntimeLlmGovernanceCollection {
  if (!input || typeof input !== 'object') {
    return { state: 'unsupported', message: 'The host runtime does not expose LLM governance metrics.' }
  }

  const collection = input as Record<string, unknown>
  const state = normalizeCollectionState(collection.state)
  const message = nonEmptyString(collection.message) ?? undefined
  if (state !== 'available') return { state, message }
  if (!collection.value || typeof collection.value !== 'object') {
    return { state: 'error', message: 'LLM governance is marked available without a value.' }
  }

  const value = collection.value as Record<string, unknown>
  const mode = nonEmptyString(value.mode)
  const remoteAllowed = typeof value.remoteAllowed === 'boolean' ? value.remoteAllowed : null
  const countKeys = ['calls', 'tokens', 'cacheHits', 'budgetBlocks', 'fallbacks', 'privacyBlocks', 'offlineCompletions'] as const
  const counts = Object.fromEntries(countKeys.map((key) => [key, finiteNumber(value[key])])) as Record<(typeof countKeys)[number], number | null>
  const costUsd = finiteNumber(value.costUsd)
  const invalidCount = countKeys.some((key) => counts[key] === null || counts[key]! < 0 || !Number.isInteger(counts[key]!))
  if (!mode || remoteAllowed === null || costUsd === null || costUsd < 0 || invalidCount) {
    return { state: 'error', message: 'LLM governance contains invalid or incomplete metrics.' }
  }

  const optionalString = (key: string) => nonEmptyString(value[key]) ?? undefined
  return {
    state: 'available',
    message,
    value: {
      mode,
      remoteAllowed,
      calls: counts.calls!,
      tokens: counts.tokens!,
      costUsd,
      cacheHits: counts.cacheHits!,
      budgetBlocks: counts.budgetBlocks!,
      fallbacks: counts.fallbacks!,
      privacyBlocks: counts.privacyBlocks!,
      offlineCompletions: counts.offlineCompletions!,
      topProvider: optionalString('topProvider'),
      topPurpose: optionalString('topPurpose'),
      topAgent: optionalString('topAgent'),
      topTool: optionalString('topTool'),
      lastCallAtUtc: optionalString('lastCallAtUtc'),
    },
  }
}

const pluginTypes = new Set<RuntimePluginType>(['exchange-adapter', 'data-source', 'notification'])
const pluginRiskLevels = new Set<RuntimePlugin['riskLevel']>(['low', 'medium', 'high'])
const pluginPermissions: Record<RuntimePluginType, Set<string>> = {
  'data-source': new Set(['market.read', 'account.read', 'data.read']),
  notification: new Set(['notify.emit', 'notify.webhook']),
  'exchange-adapter': new Set(['exchange.testnet.read', 'exchange.testnet.trade']),
}

function normalizePlugin(input: unknown): RuntimePlugin | null {
  if (!input || typeof input !== 'object') return null
  const value = input as Record<string, unknown>
  const id = nonEmptyString(value.id)
  const name = nonEmptyString(value.name)
  const type = nonEmptyString(value.type) as RuntimePluginType | null
  const version = nonEmptyString(value.version)
  const publisherId = nonEmptyString(value.publisherId)
  const publisherName = nonEmptyString(value.publisherName)
  const entryKind = nonEmptyString(value.entryKind)
  const permissions = Array.isArray(value.permissions) ? value.permissions.map(nonEmptyString) : []
  const compatibilityStatus = nonEmptyString(value.compatibilityStatus)
  const signatureStatus = nonEmptyString(value.signatureStatus)
  const riskLevel = nonEmptyString(value.riskLevel) as RuntimePlugin['riskLevel'] | null
  const runtimeStatus = nonEmptyString(value.runtimeStatus)
  const booleansValid = [value.enabled, value.defaultEnabled, value.active, value.testnetOnly].every((item) => typeof item === 'boolean')
  const permissionsValid = Array.isArray(value.permissions) && permissions.every((item): item is string => item !== null) &&
    new Set(permissions).size === permissions.length && type !== null && pluginTypes.has(type) && permissions.every((permission) => pluginPermissions[type].has(permission))
  const statusMessageValid = value.statusMessage === null || value.statusMessage === undefined || typeof value.statusMessage === 'string'
  if (!id || !name || !type || !pluginTypes.has(type) || !version || !/^[0-9]+\.[0-9]+\.[0-9]+$/.test(version) || !publisherId || !publisherName || entryKind !== 'wpe-contract' || !permissionsValid || !booleansValid || !runtimeStatus || !['running', 'stale', 'unavailable', 'not-configured'].includes(runtimeStatus) || !compatibilityStatus || !['compatible', 'incompatible'].includes(compatibilityStatus) || signatureStatus !== 'metadataPresent' || !riskLevel || !pluginRiskLevels.has(riskLevel) || !statusMessageValid) return null
  return {
    id,
    name,
    type,
    version,
    publisherId,
    publisherName,
    entryKind: 'wpe-contract',
    permissions,
    enabled: value.enabled as boolean,
    defaultEnabled: value.defaultEnabled as boolean,
    active: value.active as boolean,
    runtimeStatus: runtimeStatus as RuntimePlugin['runtimeStatus'],
    testnetOnly: value.testnetOnly as boolean,
    compatibilityStatus: compatibilityStatus as RuntimePlugin['compatibilityStatus'],
    signatureStatus: 'metadataPresent',
    riskLevel,
    statusMessage: nonEmptyString(value.statusMessage),
  }
}

function normalizePlugins(input: unknown): RuntimePluginCollection {
  if (!input || typeof input !== 'object') {
    return { state: 'unsupported', items: [], message: 'The host runtime does not expose a plugin registry.' }
  }
  const collection = input as Record<string, unknown>
  const state = normalizeCollectionState(collection.state)
  const message = nonEmptyString(collection.message) ?? undefined
  if (state !== 'available') return { state, items: [], message }
  if (!Array.isArray(collection.items)) return { state: 'error', items: [], message: 'Plugin registry is marked available without an items array.' }
  const items = collection.items.map(normalizePlugin)
  if (items.some((item) => item === null)) return { state: 'error', items: [], message: 'Plugin registry contains invalid or incomplete items.' }
  const ids = items.map((item) => item!.id)
  if (new Set(ids).size !== ids.length) return { state: 'error', items: [], message: 'Plugin registry contains duplicate plugin identifiers.' }
  return { state: 'available', items: items as RuntimePlugin[], message }
}

const auditEventCategories = new Set<RuntimeAuditEventCategory>(['decision', 'risk', 'execution', 'recovery', 'system'])

function normalizeAuditEvent(input: unknown): RuntimeAuditEventV1 | null {
  if (!input || typeof input !== 'object') return null
  const value = input as Record<string, unknown>
  const id = nonEmptyString(value.id)
  const timeUtc = nonEmptyString(value.timeUtc)
  const category = nonEmptyString(value.category) as RuntimeAuditEventCategory | null
  const source = nonEmptyString(value.source)
  const status = nonEmptyString(value.status)
  const summary = nonEmptyString(value.summary)
  const correlationIdValid = value.correlationId === undefined || value.correlationId === null || nonEmptyString(value.correlationId) !== null
  if (!id || !timeUtc || Number.isNaN(Date.parse(timeUtc)) || !category || !auditEventCategories.has(category) || !source || !status || !summary || !correlationIdValid) return null
  return {
    id,
    timeUtc,
    category,
    source,
    correlationId: value.correlationId === null ? null : nonEmptyString(value.correlationId) ?? undefined,
    status,
    summary,
  }
}

function normalizeAuditEvents(input: unknown, snapshotFresh: boolean | undefined): RuntimeAuditEventCollection {
  if (!input || typeof input !== 'object') {
    return { state: 'unsupported', items: [], message: 'The host runtime does not expose audit events.' }
  }
  const collection = input as Record<string, unknown>
  const state = normalizeCollectionState(collection.state)
  const message = nonEmptyString(collection.message) ?? undefined
  if (snapshotFresh === false && state === 'available') {
    return { state: 'stale', items: [], message: message ?? 'The audit snapshot is stale.' }
  }
  if (state !== 'available') return { state, items: [], message }
  if (!Array.isArray(collection.items)) return { state: 'error', items: [], message: 'Audit events are marked available without an items array.' }
  const items = collection.items.map(normalizeAuditEvent)
  if (items.some((item) => item === null)) return { state: 'error', items: [], message: 'Audit events contain invalid or incomplete items.' }
  const ids = items.map((item) => item!.id)
  if (new Set(ids).size !== ids.length) return { state: 'error', items: [], message: 'Audit events contain duplicate identifiers.' }
  return { state: 'available', items: items as RuntimeAuditEventV1[], message }
}

const validHash=(value:unknown):value is string=>typeof value==='string'&&/^[a-f0-9]{64}$/i.test(value)
const validTime=(value:unknown):value is string=>typeof value==='string'&&!Number.isNaN(Date.parse(value))
const strings=(value:unknown):string[]|null=>Array.isArray(value)&&value.every(item=>typeof item==='string')?value:null
function normalizeTeacherBlock(input:unknown):RuntimeTeacherBlock|null{if(!input||typeof input!=='object')return null;const v=input as Record<string,unknown>,e=strings(v.evidenceHashes);return nonEmptyString(v.blockId)&&nonEmptyString(v.kind)&&nonEmptyString(v.heading)&&typeof v.content==='string'&&e&&validHash(v.sha256)?{blockId:v.blockId as string,kind:v.kind as string,heading:v.heading as string,content:v.content as string,evidenceHashes:e,sha256:v.sha256}:null}
function normalizeTeacherLesson(input:unknown):RuntimeTeacherLesson|null{if(!input||typeof input!=='object')return null;const v=input as Record<string,unknown>,blocks=Array.isArray(v.blocks)?v.blocks.map(normalizeTeacherBlock):[];if(!nonEmptyString(v.lessonId)||!nonEmptyString(v.kind)||!nonEmptyString(v.timeZoneId)||!validTime(v.scheduledForUtc)||!validTime(v.generatedAtUtc)||!nonEmptyString(v.language)||!nonEmptyString(v.teachingLevel)||!nonEmptyString(v.personaVersion)||!Array.isArray(v.blocks)||blocks.some(x=>x===null)||!validHash(v.contentSha256)||v.executionAuthority!==false)return null;return{lessonId:v.lessonId as string,kind:v.kind as string,timeZoneId:v.timeZoneId as string,scheduledForUtc:v.scheduledForUtc,generatedAtUtc:v.generatedAtUtc,language:v.language as string,teachingLevel:v.teachingLevel as string,personaVersion:v.personaVersion as string,blocks:blocks as RuntimeTeacherBlock[],contentSha256:v.contentSha256,executionAuthority:false}}
function normalizeTeacherRecommendation(input:unknown):RuntimeTeacherRecommendation|null{if(!input||typeof input!=='object')return null;const v=input as Record<string,unknown>,e=strings(v.evidenceHashes),c=strings(v.confirmationConditions),i=strings(v.invalidationConditions),r=strings(v.materialRisks),version=finiteNumber(v.version);if(!nonEmptyString(v.recommendationId)||version===null||!Number.isInteger(version)||version<1||!nonEmptyString(v.instrument)||!nonEmptyString(v.assetClass)||!nonEmptyString(v.state)||!nonEmptyString(v.horizon)||!validTime(v.issuedAtUtc)||!validTime(v.expiresAtUtc)||typeof v.thesis!=='string'||!e||!e.every(validHash)||!c||!i||!r||v.executionAuthority!==false)return null;return{recommendationId:v.recommendationId as string,version,instrument:v.instrument as string,assetClass:v.assetClass as string,state:v.state as string,horizon:v.horizon as string,issuedAtUtc:v.issuedAtUtc,expiresAtUtc:v.expiresAtUtc,thesis:v.thesis,evidenceHashes:e,confirmationConditions:c,invalidationConditions:i,materialRisks:r,supersedesId:typeof v.supersedesId==='string'?v.supersedesId:null,executionAuthority:false}}
function normalizeTeacherCorrection(input:unknown):RuntimeTeacherCorrection|null{if(!input||typeof input!=='object')return null;const v=input as Record<string,unknown>,blocks=Array.isArray(v.replacementBlocks)?v.replacementBlocks.map(normalizeTeacherBlock):[];return nonEmptyString(v.correctionId)&&nonEmptyString(v.supersededLessonId)&&nonEmptyString(v.reasonCode)&&validTime(v.issuedAtUtc)&&Array.isArray(v.replacementBlocks)&&blocks.every(x=>x!==null)&&validHash(v.sha256)?{correctionId:v.correctionId as string,supersededLessonId:v.supersededLessonId as string,reasonCode:v.reasonCode as string,issuedAtUtc:v.issuedAtUtc,replacementBlocks:blocks as RuntimeTeacherBlock[],sha256:v.sha256}:null}
function normalizeTeacherOutcome(input:unknown):RuntimeTeacherOutcome|null{if(!input||typeof input!=='object')return null;const v=input as Record<string,unknown>,version=finiteNumber(v.recommendationVersion),numbers=['instrumentReturnPct','benchmarkReturnPct','relativeReturnPct','maximumFavorableExcursionPct','maximumAdverseExcursionPct'].map(k=>finiteNumber(v[k]));if(!nonEmptyString(v.outcomeId)||!nonEmptyString(v.recommendationId)||version===null||!Number.isInteger(version)||!nonEmptyString(v.instrument)||!nonEmptyString(v.benchmark)||!nonEmptyString(v.horizon)||!validTime(v.issuedAtUtc)||!validTime(v.evaluatedAtUtc)||numbers.some(x=>x===null)||typeof v.invalidatedBeforeHorizon!=='boolean'||!nonEmptyString(v.processState)||!validHash(v.canonicalSha256))return null;return{outcomeId:v.outcomeId as string,recommendationId:v.recommendationId as string,recommendationVersion:version,instrument:v.instrument as string,benchmark:v.benchmark as string,horizon:v.horizon as string,issuedAtUtc:v.issuedAtUtc,evaluatedAtUtc:v.evaluatedAtUtc,instrumentReturnPct:numbers[0]!,benchmarkReturnPct:numbers[1]!,relativeReturnPct:numbers[2]!,maximumFavorableExcursionPct:numbers[3]!,maximumAdverseExcursionPct:numbers[4]!,invalidatedBeforeHorizon:v.invalidatedBeforeHorizon,processState:v.processState as string,canonicalSha256:v.canonicalSha256}}

/** Normalize a host snapshot without allowing unknown contracts or partial merges. */
export function normalizeRuntimeEvent(input: unknown): WpeRuntimeState | null {
  if (!input || typeof input !== 'object') return null
  const value = input as Record<string, unknown>
  if (value.contractVersion !== RUNTIME_CONTRACT_VERSION) return null
  const freshness = value.freshness && typeof value.freshness === 'object' ? value.freshness as { fresh: boolean; ageSeconds: number; staleAfterSeconds: number } : undefined
  const generatedAtUtc=nonEmptyString(value.generatedAtUtc),sourceUpdatedAtUtc=nonEmptyString(value.sourceUpdatedAtUtc),environment=nonEmptyString(value.environment)
  if(!freshness||typeof freshness.fresh!=='boolean'||finiteNumber(freshness.ageSeconds)===null||freshness.ageSeconds<0||finiteNumber(freshness.staleAfterSeconds)===null||freshness.staleAfterSeconds<=0||!generatedAtUtc||!sourceUpdatedAtUtc||Number.isNaN(Date.parse(generatedAtUtc))||Number.isNaN(Date.parse(sourceUpdatedAtUtc))||environment!=='Testnet')return null
  const collections = ['account', 'positions', 'orders', 'risk', 'strategies', 'backtests', 'crossAssetResearch', 'distribution', 'telemetry', 'markets', 'publicMarkets', 'publicKlines', 'capabilities', 'llmGovernance', 'plugins', 'auditEvents', 'equityHistory', 'equityMarkets', 'equityBroker','historicalOrders','historicalEquity','historicalBacktests','historicalSkillCalls','historicalAuditEvents','historicalPostTradePnlDrift', 'connectionStatus', 'strategyRegistry', 'strategyLifecycleEvents','skillCalls','memoryStatus','recentMemoryRetrievals','agentOperations','agentHandoffs','teacherLessons','teacherRecommendations','teacherCorrections','teacherOutcomes','notificationStatus','notificationOutbox','telegramSubscribers','authorizationMode','automaticExecutions','pendingApprovals','securityStorage']
  const collectionStates = Object.fromEntries(collections.map((key) => [key, normalizeCollectionState((value[key] as { state?: unknown } | undefined)?.state)])) as NonNullable<WpeRuntimeState['collectionStates']>
  const positions = normalizeItems(value.positions, normalizePosition)
  const orders = normalizeItems(value.orders, normalizeOrder)
  const backtests = normalizeItems(value.backtests, normalizeBacktest)
  const equityHistory = normalizeItems(value.equityHistory, normalizeEquityPoint)
  const equityMarkets = normalizeItems(value.equityMarkets, normalizeEquityMarket)
  const publicMarkets = normalizePublicMarkets(value.publicMarkets)
  const publicKlines = normalizePublicKlines(value.publicKlines)
  const equityBroker=normalizeEquityBroker(value.equityBroker)
  const historicalOrders=normalizeHistoricalCollection(value.historicalOrders,normalizeHistoricalOrder)
  const historicalEquity=normalizeHistoricalCollection(value.historicalEquity,normalizeHistoricalEquity)
  const historicalBacktests=normalizeHistoricalCollection(value.historicalBacktests,normalizeBacktest)
  const historicalSkillCalls=normalizeHistoricalCollection(value.historicalSkillCalls,normalizeSkillCall)
  const historicalAuditEvents=normalizeHistoricalCollection(value.historicalAuditEvents,normalizeHistoricalAudit)
  const historicalPostTradePnlDrift=normalizeHistoricalCollection(value.historicalPostTradePnlDrift,normalizeHistoricalPostTradePnlDrift)
  const connectionStatus = normalizeConnectionStatus(value.connectionStatus)
  const providerId=connectionStatus.state==='available'&&connectionStatus.value?.environment==='Testnet'?connectionStatus.value.providerId:null
  const sourceTime=Date.parse(sourceUpdatedAtUtc),snapshotTime=Date.parse(generatedAtUtc),now=Date.now()
  const timestampsTrusted=sourceTime<=snapshotTime&&snapshotTime<=now+5000&&now-sourceTime<=freshness.staleAfterSeconds*1000
  const currentAuthority=connectionStatus.value?.ready===true&&connectionStatus.value.exchangeConnected===true&&connectionStatus.value.tradePermission===true
  const trusted=freshness.fresh===true&&timestampsTrusted&&providerId!==null&&currentAuthority
  const diagnostic=normalizeDiagnostic(value.diagnostic)
  const memoryStatus=normalizeMemoryStatus(value.memoryStatus)
  const recentMemoryRetrievals=normalizeItems(value.recentMemoryRetrievals,normalizeMemoryRetrieval)
  const strategyRegistry=normalizeItems(value.strategyRegistry,normalizeStrategyProfile)
  const strategyLifecycleEvents=normalizeItems(value.strategyLifecycleEvents,normalizeStrategyEvent)
  const skillCalls=normalizeItems(value.skillCalls,normalizeSkillCall)
  const agentOperations=normalizeItems(value.agentOperations,normalizeAgentOperation)
  const agentHandoffs=normalizeItems(value.agentHandoffs,normalizeAgentHandoff)
  const teacherLessons=normalizeItems(value.teacherLessons,normalizeTeacherLesson)
  const teacherRecommendations=normalizeItems(value.teacherRecommendations,normalizeTeacherRecommendation)
  const teacherCorrections=normalizeItems(value.teacherCorrections,normalizeTeacherCorrection)
  const teacherOutcomes=normalizeItems(value.teacherOutcomes,normalizeTeacherOutcome)
  const notificationStatus=normalizeNotificationStatus(value.notificationStatus)
  const notificationOutbox=normalizeItems(value.notificationOutbox,normalizeNotificationRow)
  const telegramSubscribers=normalizeItems(value.telegramSubscribers,normalizeTelegramSubscriber)
  const authorizationMode=normalizeAuthorizationMode(value.authorizationMode)
  const automaticExecutions=normalizeItems(value.automaticExecutions,normalizeAutomaticExecution)
  const securityStorage=normalizeSecurityStorage(value.securityStorage)
  const pendingApprovals=normalizePendingApprovals(value.pendingApprovals)
  const research=normalizeCrossAssetResearchCollection(value.crossAssetResearch)
  const distribution=normalizeDistribution(value.distribution)
  const llmGovernance = normalizeLlmGovernance(value.llmGovernance)
  const plugins = normalizePlugins(value.plugins)
  const auditEvents = normalizeAuditEvents(value.auditEvents, freshness?.fresh)
  collectionStates.positions = positions.state
  collectionStates.orders = orders.state
  collectionStates.backtests = backtests.state
  collectionStates.equityHistory = equityHistory.state
  collectionStates.equityMarkets = equityMarkets.state
  collectionStates.publicMarkets = publicMarkets.state
  collectionStates.publicKlines = publicKlines.state
  collectionStates.equityBroker=equityBroker.state
  collectionStates.historicalOrders=historicalOrders.state;collectionStates.historicalEquity=historicalEquity.state;collectionStates.historicalBacktests=historicalBacktests.state;collectionStates.historicalSkillCalls=historicalSkillCalls.state;collectionStates.historicalAuditEvents=historicalAuditEvents.state;collectionStates.historicalPostTradePnlDrift=historicalPostTradePnlDrift.state
  collectionStates.connectionStatus = connectionStatus.state
  collectionStates.strategyRegistry=strategyRegistry.state
  collectionStates.strategyLifecycleEvents=strategyLifecycleEvents.state
  collectionStates.skillCalls=skillCalls.state
  collectionStates.memoryStatus=memoryStatus.state
  collectionStates.recentMemoryRetrievals=recentMemoryRetrievals.state
  collectionStates.agentOperations=agentOperations.state
  collectionStates.agentHandoffs=agentHandoffs.state
  collectionStates.teacherLessons=teacherLessons.state;collectionStates.teacherRecommendations=teacherRecommendations.state;collectionStates.teacherCorrections=teacherCorrections.state;collectionStates.teacherOutcomes=teacherOutcomes.state
  collectionStates.notificationStatus=notificationStatus.state
  collectionStates.notificationOutbox=notificationOutbox.state
  collectionStates.telegramSubscribers=telegramSubscribers.state
  collectionStates.authorizationMode=authorizationMode.state
  collectionStates.automaticExecutions=automaticExecutions.state
  collectionStates.securityStorage=securityStorage.state
  collectionStates.pendingApprovals=pendingApprovals.state
  collectionStates.crossAssetResearch=research.state
  collectionStates.distribution=distribution.state
  collectionStates.llmGovernance = llmGovernance.state
  collectionStates.plugins = plugins.state
  collectionStates.auditEvents = auditEvents.state
  const legacy = (value.legacyFields && typeof value.legacyFields === 'object' ? value.legacyFields : value) as Record<string, unknown>
  const state: WpeRuntimeState = {
    ...legacy,
    contractVersion: value.contractVersion,
    freshness,
    collectionStates,
    positions: positions.items,
    orders: orders.items,
    llmGovernance,
    plugins,
    auditEvents,
    equityHistory: equityHistory.items,
    publicMarkets,
    publicKlines,
    equities:{state:equityMarkets.state,items:equityMarkets.items.map(item=>({instrumentId:item.instrumentId,symbol:item.instrumentId,displayName:'',venue:item.venueId,currency:item.currency,sessionState:item.sessionState,lastPrice:item.lastPrice,changePercent:null,observedAtUtc:item.observedAtUtc,providerId:item.providerId})),message:equityMarkets.message,asOfUtc:equityMarkets.items.length?equityMarkets.items.reduce((latest,item)=>item.observedAtUtc>latest?item.observedAtUtc:latest,equityMarkets.items[0].observedAtUtc):undefined},
    equityBroker,
    historicalOrders,historicalEquity,historicalBacktests,historicalSkillCalls,historicalAuditEvents,historicalPostTradePnlDrift,
    connectionStatus,
    diagnostic,
    memoryStatus,
    recentMemoryRetrievals:recentMemoryRetrievals.items,
    agentOperations:agentOperations.items,
    agentHandoffs:agentHandoffs.items,
    teacherLessons:teacherLessons.items,
    teacherRecommendations:teacherRecommendations.items,
    teacherCorrections:teacherCorrections.items,
    teacherOutcomes:teacherOutcomes.items,
    notificationStatus,
    notificationOutbox:notificationOutbox.items,
    telegramSubscribers:telegramSubscribers.items,
    authorizationMode,
    automaticExecutions:{state:automaticExecutions.state,items:automaticExecutions.items,message:automaticExecutions.message},
    securityStorage,
    pendingApprovals,
    research,
    distribution,
    strategyRegistry:strategyRegistry.items,
    strategyLifecycleEvents:strategyLifecycleEvents.items,
    skillCalls:skillCalls.items,
    collectionMessages: { positions: positions.message, orders: orders.message, backtests: backtests.message, equityHistory: equityHistory.message, equityMarkets:equityMarkets.message, equityBroker:equityBroker.message, connectionStatus: connectionStatus.message, strategyRegistry:strategyRegistry.message, strategyLifecycleEvents:strategyLifecycleEvents.message },
    backtests: backtests.items,
    runtimeFresh: trusted,
    runtimeAgeSeconds: freshness?.ageSeconds,
    lastUpdated: sourceUpdatedAtUtc,
    runtimeEventSequence: (value.eventSequence ?? legacy.runtimeEventSequence) as number | undefined,
    environment,
    runtimeProviderId:providerId??undefined,
    sourceTimestampUtc:sourceUpdatedAtUtc,
    snapshotTimestampUtc:generatedAtUtc,
    runtimeDiagnosticReason:trusted?undefined:providerId===null?'Provider or Testnet connection metadata unavailable.':!currentAuthority?'Current runtime authority is unavailable.':'Runtime timestamp is stale or invalid.',
    // A stale but structurally valid Testnet snapshot may request lifecycle control.
    // The native host re-checks current provider authority before starting; stopping
    // must remain available even when market/runtime evidence is stale.
    agentStartAllowed:legacy.agentIsRunning !== true,
    agentStopAllowed:legacy.agentIsRunning === true,
    previewMode: false,
  }
  if ((value.telemetry as { value?: unknown } | undefined)?.value) Object.assign(state, (value.telemetry as { value: object }).value)
  if ((value.account as { value?: unknown } | undefined)?.value) Object.assign(state, (value.account as { value: object }).value)
  if ((value.risk as { value?: unknown } | undefined)?.value) Object.assign(state, (value.risk as { value: object }).value)
  if ((value.strategies as { value?: unknown } | undefined)?.value) Object.assign(state, (value.strategies as { value: object }).value)
  const marketCollection = value.markets as { items?: unknown[] } | undefined
  if (Array.isArray(marketCollection?.items)) state.markets = marketCollection.items as RuntimeMarket[]
  const capabilityCollection = value.capabilities as { items?: unknown[] } | undefined
  if (Array.isArray(capabilityCollection?.items)) state.capabilities = capabilityCollection.items as RuntimeCapability[]
  return state
}

const RuntimeContext = createContext<WpeRuntimeState>({})

export function unavailableRuntimeState(hostConnected:boolean,reason:string):WpeRuntimeState{return{previewMode:false,runtimeFresh:false,hostConnected,agentStartAllowed:false,agentStopAllowed:false,status:'Runtime contract unsupported',runtimeDiagnosticReason:reason}}

export function RuntimeBridge({ children }: { children: React.ReactNode }) {
  const [state, setState] = useState<WpeRuntimeState>({})
  useEffect(() => {
    const hostBridge = (window as Window & { chrome?: { webview?: unknown } }).chrome?.webview
    let active = true
    if (!hostBridge) queueMicrotask(() => { if (active) setState(unavailableRuntimeState(false,'WPF runtime host is disconnected.')) })
    const handler = (event: Event) => {
      const detail = (event as CustomEvent<unknown>).detail
      const normalized = normalizeRuntimeEvent(detail)
      // A host event always replaces preview/previous state. Unknown contracts fail closed.
      setState(normalized ? {...normalized,hostConnected:true} : unavailableRuntimeState(true,'Runtime contract is malformed or unsupported.'))
    }
    window.addEventListener('wpe-runtime', handler)
    return () => {
      active = false
      window.removeEventListener('wpe-runtime', handler)
    }
  }, [])
  const value = useMemo(() => state, [state])
  return <RuntimeContext.Provider value={value}>{children}</RuntimeContext.Provider>
}

export function postRuntimeHostCommand(type:'agent-start'|'agent-stop'):boolean{
  const bridge=(window as Window&{chrome?:{webview?:{postMessage:(message:{type:string})=>void}}}).chrome?.webview
  if(!bridge)return false
  bridge.postMessage({type})
  return true
}

export function useWpeRuntime() {
  return useContext(RuntimeContext)
}

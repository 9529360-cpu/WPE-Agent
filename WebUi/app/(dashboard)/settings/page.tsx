'use client'

import { AlertTriangle, BellRing, BrainCircuit, Cable, ShieldCheck } from 'lucide-react'
import { PageHeader } from '@/components/shell/page-header'
import { Panel, PanelBody, PanelHeader } from '@/components/ui/panel'
import { RuntimeMetric, RuntimeUnavailable } from '@/components/runtime-state'
import { useWpeRuntime } from '@/components/runtime-bridge'
import { useI18n } from '@/lib/i18n/context'

type AutomaticExecution = {
  executionId: string
  status: string
  code: string
  attemptCount: number
  updatedAtUtc: string
}
type AutomaticExecutions = {
  state: 'available' | 'unsupported' | 'stale' | 'error'
  message?: string
  items: AutomaticExecution[]
}

const isolatedStatuses = new Set([
  'PolicyBlocked',
  'RiskBlocked',
  'CapabilityUnavailable',
  'MarketStale',
  'ArtifactInvalid',
  'FailedTerminal',
  'UnknownOutcome',
])

export default function SettingsPage() {
  const runtime = useWpeRuntime()
  const { t, formatDate } = useI18n()
  const governance = runtime.runtimeFresh && runtime.llmGovernance?.state === 'available' ? runtime.llmGovernance.value : undefined
  const connection = runtime.connectionStatus?.state === 'available' ? runtime.connectionStatus.value : undefined
  const connectionState = runtime.connectionStatus?.state ?? 'unsupported'
  const notification = runtime.notificationStatus?.state === 'available' ? runtime.notificationStatus.value : undefined
  const authorization = runtime.authorizationMode
  const approvals = runtime.pendingApprovals
  const automatic = (runtime as typeof runtime & { automaticExecutions?: AutomaticExecutions }).automaticExecutions
  const authorizationValue = authorization?.state === 'available' ? authorization.value as (typeof authorization.value & { enabledAtUtc?: string }) : undefined
  const autoEnabled = authorizationValue?.mode === 'Auto'
  const autoEnabledAt = autoEnabled ? authorizationValue?.enabledAtUtc : undefined
  const authorizationModeLabel = autoEnabled ? t('settings.modeAutoTestnet') : authorizationValue?.mode ? t('settings.modeLegacyReadOnly') : undefined
  const approvalStatusLabels = {
    Pending: t('settings.statusHistorical'),
    Revoked: t('settings.statusRevoked'),
    Expired: t('settings.statusExpired'),
    ArtifactUnavailable: t('settings.statusArtifactUnavailable'),
  }
  return <div className="flex flex-col gap-6 p-6">
    <PageHeader title={t('settings.title')} description={t('settings.description')} />
    {runtime.diagnostic?.value && <Panel><PanelBody className="grid gap-3 sm:grid-cols-3">
      <RuntimeMetric label={t('settings.diagnostic')} value={runtime.diagnostic.value.summary} />
      <RuntimeMetric label={t('settings.code')} value={runtime.diagnostic.value.code} />
      <RuntimeMetric label={t('settings.diagnosticTime')} value={formatDate(runtime.diagnostic.value.timeUtc)} />
    </PanelBody></Panel>}

    <Panel>
      <PanelHeader icon={<Cable className="size-4" />} title={t('settings.connection')} action={<span className="text-[10px] text-muted-foreground">{runtime.previewMode ? t('common.preview') : t('settings.readOnly')}</span>} />
      {connection ? <PanelBody className="space-y-4">
        {runtime.previewMode && <div className="rounded border border-warning/30 bg-warning/10 px-3 py-2 text-xs text-warning">{t('settings.previewHelp')}</div>}
        <div className="grid gap-3 sm:grid-cols-2 xl:grid-cols-4">
          <RuntimeMetric label={t('settings.activeConnection')} value={connection.displayName} />
          <RuntimeMetric label="Provider" value={connection.providerId} />
          <RuntimeMetric label={t('settings.environment')} value={connection.environment} />
          <RuntimeMetric label={t('settings.credential')} value={connection.credentialStatus} />
          <RuntimeMetric label={t('settings.adapter')} value={connection.adapterStatus} />
          <RuntimeMetric label={t('settings.lastCheck')} value={formatDate(connection.lastCheckedAtUtc)} />
          <RuntimeMetric label={t('settings.readiness')} value={connection.ready ? t('common.ready') : t('common.notReady')} />
          <RuntimeMetric label={t('settings.tradePermission')} value={connection.tradePermission === null ? t('common.unknown') : connection.tradePermission ? t('settings.enabled') : t('settings.disabled')} />
          <RuntimeMetric label={t('settings.withdrawPermission')} value={connection.withdrawPermission === null ? t('common.unknown') : connection.withdrawPermission ? t('settings.enabled') : t('settings.disabled')} />
        </div>
        {connection.withdrawPermission && <div className="flex items-start gap-2 rounded border border-danger/30 bg-danger/10 px-3 py-2 text-xs text-danger"><AlertTriangle className="mt-0.5 size-4 shrink-0" /><span>{connection.withdrawalWarning || t('settings.withdrawWarning')}</span></div>}
      </PanelBody> : <PanelBody className="p-5 text-sm text-muted-foreground">{connectionState === 'stale' ? t('settings.connectionStale') : connectionState === 'error' ? runtime.connectionStatus?.message || t('settings.connectionUnavailable') : runtime.connectionStatus?.message || t('settings.connectionMissing')}</PanelBody>}
    </Panel>

    <Panel>
      <PanelHeader icon={<ShieldCheck className="size-4" />} title={t('settings.authorization')} action={<span className="text-[10px] text-muted-foreground">{runtime.previewMode ? t('common.preview') : t('settings.readOnly')}</span>} />
      <PanelBody className="space-y-4">
        <p className="text-xs text-muted-foreground">{t('settings.authorizationHelp')}</p>
        {authorization?.state !== 'available' && <div className="rounded border border-warning/30 bg-warning/10 px-3 py-2 text-xs text-warning">{authorization?.state === 'stale' ? t('settings.authorizationStale') : authorization?.state === 'error' ? authorization.message || t('settings.authorizationError') : authorization?.message || t('settings.authorizationUnsupported')}</div>}
        <div className="grid gap-3 sm:grid-cols-2 xl:grid-cols-4">
          <RuntimeMetric label={t('settings.authorizationMode')} value={authorizationModeLabel} />
          <RuntimeMetric label={t('settings.autoEnabledAt')} value={autoEnabledAt ? formatDate(autoEnabledAt) : t('common.notProvided')} />
          <RuntimeMetric label={t('settings.riskGate')} value={runtime.riskApprovalStatus || t('common.unknown')} />
          <RuntimeMetric label={t('settings.mainnet')} value={t('settings.disabled')} />
        </div>
        {runtime.previewMode && <div className="rounded border border-warning/30 bg-warning/10 px-3 py-2 text-xs text-warning">{t('settings.previewAuthorization')}</div>}

        <div>
          <h3 className="mb-2 text-xs font-medium">{t('settings.automaticQueue')}</h3>
          {automatic?.state === 'available' ? automatic.items.length === 0 ? <p className="text-xs text-muted-foreground">{t('settings.automaticQueueEmpty')}</p> :
            <div className="max-w-full overflow-x-auto"><table className="w-full min-w-[720px] text-xs">
              <thead><tr><th className="pb-2 text-left">{t('settings.executionId')}</th><th className="pb-2 text-left">{t('common.status')}</th><th className="pb-2 text-left">{t('settings.isolationReason')}</th><th className="pb-2 text-right">{t('settings.attempts')}</th><th className="pb-2 text-left">{t('common.time')}</th></tr></thead>
              <tbody>{automatic.items.map(item => <tr key={item.executionId} className="border-t border-border"><td className="py-2 font-mono">{item.executionId}</td><td>{item.status}</td><td className="font-mono">{isolatedStatuses.has(item.status) ? item.code : t('common.none')}</td><td className="text-right">{item.attemptCount}</td><td>{formatDate(item.updatedAtUtc)}</td></tr>)}</tbody>
            </table></div> :
            <p className="text-xs text-muted-foreground">{automatic?.message || t('settings.automaticQueueUnavailable')}</p>}
        </div>

        <div>
          <h3 className="mb-2 text-xs font-medium">{t('settings.legacyApprovalHistory')}</h3>
          {approvals?.state === 'available' ? approvals.items.length === 0 ? <p className="text-xs text-muted-foreground">{t('settings.legacyHistoryEmpty')}</p> :
            <div className="max-w-full overflow-x-auto"><table className="w-full min-w-[900px] text-xs">
              <thead><tr><th className="pb-2 text-left">{t('settings.approvalId')}</th><th className="pb-2 text-left">{t('common.symbol')}</th><th className="pb-2 text-left">{t('common.status')}</th><th className="pb-2 text-left">{t('settings.reasonCode')}</th><th className="pb-2 text-left">{t('settings.created')}</th><th className="pb-2 text-left">{t('settings.expires')}</th></tr></thead>
              <tbody>{approvals.items.map(approval => <tr key={approval.approvalId} className="border-t border-border"><td className="py-2 font-mono">{approval.approvalId}</td><td>{approval.symbol || t('common.notProvided')}</td><td>{approvalStatusLabels[approval.status]}</td><td className="font-mono">{approval.reasonCode}</td><td>{formatDate(approval.createdAtUtc)}</td><td>{formatDate(approval.expiresAtUtc)}</td></tr>)}</tbody>
            </table></div> :
            <p className="text-xs text-muted-foreground">{approvals?.message || t('settings.legacyHistoryUnavailable')}</p>}
        </div>
      </PanelBody>
    </Panel>

    <Panel>
      <PanelHeader icon={<BellRing className="size-4" />} title={t('settings.notifications')} action={<span className="text-[10px] text-muted-foreground">{runtime.previewMode ? t('common.preview') : t('settings.readOnly')}</span>} />
      {notification ? <PanelBody className="grid gap-3 sm:grid-cols-2 xl:grid-cols-4">
        <RuntimeMetric label="Telegram" value={notification.telegramReady ? t('common.ready') : t('common.notReady')} />
        <RuntimeMetric label="WhatsApp" value={notification.whatsAppReady ? t('common.ready') : t('common.notReady')} />
        <RuntimeMetric label={t('settings.pending')} value={notification.pendingCount} />
        <RuntimeMetric label={t('settings.deadLetter')} value={notification.deadLetterCount} />
        <RuntimeMetric label="Allowed events" value={notification.eventKinds.length ? notification.eventKinds.join(', ') : t('common.none')} />
      </PanelBody> : <PanelBody className="p-5 text-sm text-muted-foreground">{runtime.notificationStatus?.message || t('settings.notificationHelp')}</PanelBody>}
    </Panel>

    {runtime.runtimeFresh ? <Panel><PanelBody className="grid gap-3 md:grid-cols-2 xl:grid-cols-4">
      <RuntimeMetric label={t('settings.environment')} value={runtime.environment} />
      <RuntimeMetric label={t('settings.configMode')} value={runtime.aiRuntimeMode} />
      <RuntimeMetric label={t('dashboard.exchange')} value={runtime.exchangeConnected ? t('common.connected') : t('common.notConnected')} />
      <RuntimeMetric label="Brain" value={runtime.brainConnected ? t('common.connected') : t('common.notConnected')} />
    </PanelBody></Panel> : <RuntimeUnavailable stale={Boolean(runtime.lastUpdated)} subject={t('settings.connection')} />}

    <Panel>
      <PanelHeader icon={<BrainCircuit className="size-4" />} title={t('settings.governance')} />
      {governance ? <PanelBody className="grid gap-3 sm:grid-cols-2 lg:grid-cols-4">
        <RuntimeMetric label={t('settings.governanceMode')} value={governance.mode} />
        <RuntimeMetric label={t('settings.remoteCalls')} value={governance.remoteAllowed ? t('settings.enabled') : t('settings.disabled')} />
        <RuntimeMetric label={t('settings.budgetBlocks')} value={governance.budgetBlocks} />
        <RuntimeMetric label={t('settings.privacyBlocks')} value={governance.privacyBlocks} />
      </PanelBody> : <PanelBody className="p-5 text-sm text-muted-foreground">{runtime.llmGovernance?.state === 'stale' ? t('settings.governanceStale') : runtime.llmGovernance?.state === 'error' ? runtime.llmGovernance.message || t('settings.governanceUnavailable') : t('settings.governanceMissing')}</PanelBody>}
    </Panel>
  </div>
}

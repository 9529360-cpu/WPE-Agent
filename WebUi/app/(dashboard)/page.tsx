'use client'

import { DecisionContext } from '@/components/dashboard/decision-context'
import { DecisionFlow } from '@/components/dashboard/decision-flow'
import { ExecutionCockpit } from '@/components/dashboard/execution-cockpit'
import { MarketOverview } from '@/components/dashboard/market-overview'
import { MarketSelector } from '@/components/dashboard/market-selector'
import { ResourceMonitor } from '@/components/dashboard/resource-monitor'
import { SystemHealth } from '@/components/dashboard/system-health'
import { useWpeRuntime } from '@/components/runtime-bridge'
import { RuntimeMetric, RuntimeUnavailable } from '@/components/runtime-state'
import { PageHeader } from '@/components/shell/page-header'
import { Panel, PanelBody } from '@/components/ui/panel'
import { useI18n } from '@/lib/i18n/context'

export default function DashboardPage() {
  const runtime = useWpeRuntime()
  const { t } = useI18n()
  return (
    <div className="flex flex-col gap-4">
      <PageHeader
        title={t('dashboard.title')}
        description={t('dashboard.description')}
        actions={runtime.previewMode ? <span className="rounded border border-warning/30 bg-warning/10 px-2 py-1 text-[11px] font-medium text-warning">{t('common.preview')}</span> : undefined}
      />
      {runtime.runtimeFresh ? (
        <Panel>
          <PanelBody className="grid gap-3 sm:grid-cols-2 lg:grid-cols-4">
            <RuntimeMetric label={t('dashboard.wallet')} value={runtime.walletBalance} />
            <RuntimeMetric label={t('dashboard.agentStatus')} value={runtime.status} />
            <RuntimeMetric label={t('dashboard.lastDecision')} value={runtime.lastDecision} />
            <RuntimeMetric label={t('dashboard.riskLoad')} value={runtime.riskLoad === undefined ? undefined : `${Math.round(runtime.riskLoad)}%`} />
          </PanelBody>
        </Panel>
      ) : <RuntimeUnavailable stale={Boolean(runtime.lastUpdated)} subject={t('dashboard.runtime')} />}
      <ExecutionCockpit />
      <div className="grid gap-4 xl:grid-cols-[minmax(0,1.2fr)_minmax(320px,420px)]">
        <div className="flex min-w-0 flex-col gap-4">
          <MarketSelector />
          <MarketOverview />
        </div>
        <DecisionContext />
      </div>
      <DecisionFlow />
      <ResourceMonitor />
      <SystemHealth />
    </div>
  )
}

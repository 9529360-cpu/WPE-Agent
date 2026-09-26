'use client'

import { AgentLiveStatus } from '@/components/dashboard/agent-live-status'
import { DecisionContext } from '@/components/dashboard/decision-context'
import { DecisionFlow } from '@/components/dashboard/decision-flow'
import { MarketOverview } from '@/components/dashboard/market-overview'
import { MarketSelector } from '@/components/dashboard/market-selector'
import { OrderFeed } from '@/components/dashboard/order-feed'
import { PortfolioPanel } from '@/components/dashboard/portfolio'
import { ResourceMonitor } from '@/components/dashboard/resource-monitor'
import { RiskSummary } from '@/components/dashboard/risk-summary'
import { SystemHealth } from '@/components/dashboard/system-health'
import { useWpeRuntime } from '@/components/runtime-bridge'
import { RuntimeMetric } from '@/components/runtime-state'
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
      <AgentLiveStatus />
      <Panel>
        <PanelBody className="grid gap-3 sm:grid-cols-2 lg:grid-cols-4">
          <RuntimeMetric label={t('dashboard.wallet')} value={runtime.runtimeFresh ? runtime.walletBalance : undefined} />
          <RuntimeMetric label={t('dashboard.availableBalance')} value={runtime.runtimeFresh ? runtime.availableBalance : undefined} />
          <RuntimeMetric label={t('dashboard.riskLoad')} value={runtime.runtimeFresh && runtime.riskLoad !== undefined ? `${Math.round(runtime.riskLoad)}%` : undefined} />
          <RuntimeMetric label={t('dashboard.maxDrawdown')} value={runtime.runtimeFresh ? runtime.maxDrawdown : undefined} />
        </PanelBody>
      </Panel>
      <MarketSelector />
      <MarketOverview />
      <DecisionFlow />
      <div className="grid gap-4 xl:grid-cols-[minmax(0,1fr)_minmax(320px,420px)]">
        <PortfolioPanel />
        <DecisionContext />
      </div>
      <div className="grid gap-4 lg:grid-cols-2"><OrderFeed /><RiskSummary /></div>
      <ResourceMonitor />
      <SystemHealth />
    </div>
  )
}

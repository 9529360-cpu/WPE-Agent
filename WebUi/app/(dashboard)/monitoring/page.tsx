'use client'

import { BrainCircuit, Gauge, ScrollText } from 'lucide-react'
import { useWpeRuntime } from '@/components/runtime-bridge'
import { RuntimeMetric, RuntimeUnavailable } from '@/components/runtime-state'
import { PageHeader } from '@/components/shell/page-header'
import { Panel, PanelBody, PanelHeader } from '@/components/ui/panel'
import { useI18n } from '@/lib/i18n/context'

export default function MonitoringPage() {
  const runtime = useWpeRuntime()
  const { t, formatDate, formatNumber, formatCurrency } = useI18n()
  const audit =
    runtime.auditEvents?.state === 'available'
      ? [...(runtime.auditEvents.items ?? [])].sort((a, b) => Date.parse(b.timeUtc) - Date.parse(a.timeUtc))
      : []
  const governance = runtime.llmGovernance?.state === 'available' ? runtime.llmGovernance.value : undefined
  const skillCalls =
    runtime.collectionStates?.skillCalls === 'available'
      ? [...(runtime.skillCalls ?? [])].sort((a, b) => Date.parse(b.occurredAtUtc) - Date.parse(a.occurredAtUtc)).slice(0, 12)
      : []

  return (
    <div className="flex min-w-0 flex-col gap-6 p-4 sm:p-6">
      <PageHeader
        title={t('monitoring.title')}
        description={t('monitoring.description')}
        actions={runtime.previewMode ? <span className="text-xs text-warning">{t('common.preview')}</span> : undefined}
      />

      {runtime.runtimeFresh ? (
        <Panel>
          <PanelHeader icon={<Gauge className="size-4" />} title={t('dashboard.systemHealth')} />
          <PanelBody className="grid gap-3 md:grid-cols-2 xl:grid-cols-4">
            <RuntimeMetric label="Agent" value={runtime.status} />
            <RuntimeMetric
              label={t('dashboard.exchange')}
              value={runtime.exchangeConnected ? t('common.connected') : t('common.notConnected')}
            />
            <RuntimeMetric label="Brain" value={runtime.brainConnected ? t('common.connected') : t('common.notConnected')} />
            <RuntimeMetric
              label={t('dashboard.riskGate')}
              value={runtime.riskReady ? t('common.ready') : t('common.notReady')}
            />
          </PanelBody>
        </Panel>
      ) : (
        <RuntimeUnavailable stale={Boolean(runtime.lastUpdated)} subject={t('monitoring.title')} />
      )}

      <Panel>
        <PanelHeader
          icon={<ScrollText className="size-4" />}
          title={t('monitoring.audit')}
          action={<span className="text-xs text-muted-foreground">{audit.length}</span>}
        />
        {runtime.auditEvents?.state !== 'available' ? (
          <PanelBody className="py-10 text-center text-sm text-muted-foreground">
            {t('monitoring.auditUnavailable')}
          </PanelBody>
        ) : !audit.length ? (
          <PanelBody className="py-10 text-center text-sm text-muted-foreground">{t('monitoring.auditEmpty')}</PanelBody>
        ) : (
          <div className="overflow-x-auto">
            <table className="w-full min-w-[760px] text-left text-xs">
              <thead>
                <tr>
                  {[t('common.time'), t('monitoring.category'), t('agents.source'), t('common.status'), t('monitoring.summary'), t('monitoring.correlation')].map(value => (
                    <th key={value} className="px-4 py-3 font-normal">
                      {value}
                    </th>
                  ))}
                </tr>
              </thead>
              <tbody>
                {audit.map(item => (
                  <tr key={item.id} className="border-t border-border">
                    <td className="px-4 py-3">{formatDate(item.timeUtc)}</td>
                    <td>{item.category}</td>
                    <td>{item.source}</td>
                    <td>{item.status}</td>
                    <td className="max-w-md break-words">{item.summary}</td>
                    <td className="font-mono">{item.correlationId ?? t('common.none')}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </Panel>

      {governance ? (
        <Panel>
          <PanelHeader icon={<BrainCircuit className="size-4" />} title={t('monitoring.llm')} />
          <PanelBody className="space-y-4">
            <div className="grid gap-3 sm:grid-cols-2 lg:grid-cols-4">
              <RuntimeMetric label={t('monitoring.calls')} value={formatNumber(governance.calls)} />
              <RuntimeMetric label={t('monitoring.tokens')} value={formatNumber(governance.tokens)} />
              <RuntimeMetric label={t('monitoring.cost')} value={formatCurrency(governance.costUsd)} />
              <RuntimeMetric label={t('monitoring.cacheHits')} value={formatNumber(governance.cacheHits)} />
              <RuntimeMetric label={t('monitoring.fallbacks')} value={formatNumber(governance.fallbacks)} />
              <RuntimeMetric label={t('monitoring.provider')} value={governance.topProvider} />
              <RuntimeMetric label={t('monitoring.purpose')} value={governance.topPurpose} />
              <RuntimeMetric
                label={t('monitoring.lastCall')}
                value={governance.lastCallAtUtc ? formatDate(governance.lastCallAtUtc) : t('common.none')}
              />
            </div>

            <div className="border-t border-border pt-4">
              <div className="mb-3 flex items-center justify-between gap-3">
                <h3 className="text-xs font-medium">{t('agents.skills')}</h3>
                <span className="text-xs text-muted-foreground">
                  {runtime.previewMode ? t('common.preview') : runtime.collectionStates?.skillCalls ?? t('common.unsupported')}
                </span>
              </div>

              {runtime.collectionStates?.skillCalls !== 'available' ? (
                <div className="py-8 text-center text-sm text-muted-foreground">
                  {t(runtime.collectionStates?.skillCalls === 'stale' ? 'agents.skillsStale' : 'agents.skillsUnavailable')}
                </div>
              ) : !skillCalls.length ? (
                <div className="py-8 text-center text-sm text-muted-foreground">{t('agents.noSkills')}</div>
              ) : (
                <div className="overflow-x-auto">
                  <table className="w-full min-w-[760px] text-left text-xs">
                    <thead className="text-muted-foreground">
                      <tr>
                        {[t('common.time'), t('agents.skill'), t('common.status'), t('agents.duration'), t('common.mode'), t('agents.remoteLlm'), t('agents.tokensCost')].map(value => (
                          <th key={value} className="pb-2 font-normal">
                            {value}
                          </th>
                        ))}
                      </tr>
                    </thead>
                    <tbody>
                      {skillCalls.map(call => (
                        <tr key={call.id} className="border-t border-border">
                          <td className="py-3">{formatDate(call.occurredAtUtc)}</td>
                          <td>{call.skill}</td>
                          <td>{call.status}</td>
                          <td>{call.durationMs} ms</td>
                          <td>{call.mode ?? t('common.unknown')}</td>
                          <td>
                            {call.remoteLlmUsed === null
                              ? t('common.unknown')
                              : call.remoteLlmUsed
                                ? t('agents.used')
                                : t('agents.local')}
                          </td>
                          <td>
                            {call.tokens ?? t('common.unknown')}
                            {call.costUsd === null ? '' : ` / $${call.costUsd.toFixed(4)}`}
                          </td>
                        </tr>
                      ))}
                    </tbody>
                  </table>
                </div>
              )}
            </div>
          </PanelBody>
        </Panel>
      ) : (
        <Panel>
          <PanelBody className="py-10 text-center text-sm text-muted-foreground">
            {t('settings.governanceMissing')}
          </PanelBody>
        </Panel>
      )}
    </div>
  )
}

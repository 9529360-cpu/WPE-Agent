'use client'

import { useState } from 'react'
import { BrainCircuit, Gauge, GitBranch, ScrollText } from 'lucide-react'
import { useWpeRuntime, type RuntimeAuditEventV1 } from '@/components/runtime-bridge'
import { RuntimeMetric, RuntimeUnavailable } from '@/components/runtime-state'
import { PageHeader } from '@/components/shell/page-header'
import { Panel, PanelBody, PanelHeader } from '@/components/ui/panel'
import { useI18n } from '@/lib/i18n/context'

function buildTraceGroups(events: RuntimeAuditEventV1[]) {
  const grouped = new Map<string, RuntimeAuditEventV1[]>()
  for (const event of events) {
    const correlationId = event.correlationId?.trim()
    if (!correlationId) continue
    const current = grouped.get(correlationId) ?? []
    current.push(event)
    grouped.set(correlationId, current)
  }

  return [...grouped.entries()]
    .map(([id, items]) => ({
      id,
      events: [...items].sort((left, right) => Date.parse(left.timeUtc) - Date.parse(right.timeUtc)),
    }))
    .sort((left, right) => {
      const leftTime = Date.parse(left.events[left.events.length - 1]?.timeUtc ?? '')
      const rightTime = Date.parse(right.events[right.events.length - 1]?.timeUtc ?? '')
      return rightTime - leftTime
    })
}

export default function MonitoringPage() {
  const runtime = useWpeRuntime()
  const { t, formatDate, formatNumber, formatCurrency } = useI18n()
  const [selectedCorrelation, setSelectedCorrelation] = useState<string | null>(null)
  const audit =
    runtime.auditEvents?.state === 'available'
      ? [...(runtime.auditEvents.items ?? [])].sort((a, b) => Date.parse(b.timeUtc) - Date.parse(a.timeUtc))
      : []
  const traceGroups = buildTraceGroups(audit)
  const activeTrace = traceGroups.find(group => group.id === selectedCorrelation) ?? traceGroups[0]
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
          icon={<GitBranch className="size-4" />}
          title={t('monitoring.correlation')}
          action={<span className="text-xs text-muted-foreground">{traceGroups.length}</span>}
        />
        {runtime.auditEvents?.state !== 'available' ? (
          <PanelBody className="py-10 text-center text-sm text-muted-foreground">
            {t('monitoring.auditUnavailable')}
          </PanelBody>
        ) : !traceGroups.length ? (
          <PanelBody className="py-10 text-center text-sm text-muted-foreground">{t('common.empty')}</PanelBody>
        ) : (
          <PanelBody className="grid gap-4 lg:grid-cols-[minmax(0,280px)_minmax(0,1fr)]">
            <div className="max-h-[420px] overflow-y-auto rounded-md border border-border">
              {traceGroups.map(group => {
                const latest = group.events[group.events.length - 1]
                const active = activeTrace?.id === group.id
                return (
                  <button
                    key={group.id}
                    type="button"
                    aria-pressed={active}
                    onClick={() => setSelectedCorrelation(group.id)}
                    className={`block w-full border-b border-border px-3 py-3 text-left last:border-b-0 ${active ? 'bg-accent text-foreground' : 'text-muted-foreground hover:bg-accent/50 hover:text-foreground'}`}
                  >
                    <div className="truncate font-mono text-xs" title={group.id}>{group.id}</div>
                    <div className="mt-1 flex items-center justify-between gap-2 text-[10px]">
                      <span>{t('common.records', { count: group.events.length })}</span>
                      <span>{latest ? formatDate(latest.timeUtc) : t('common.noEventTime')}</span>
                    </div>
                  </button>
                )
              })}
            </div>

            {activeTrace ? (
              <div className="min-w-0 rounded-md border border-border p-4">
                <div className="mb-4 flex flex-wrap items-center justify-between gap-2">
                  <code className="break-all text-xs">{activeTrace.id}</code>
                  <span className="text-xs text-muted-foreground">{t('common.records', { count: activeTrace.events.length })}</span>
                </div>
                <ol className="space-y-3">
                  {activeTrace.events.map((event, index) => (
                    <li key={event.id} className="grid grid-cols-[20px_minmax(0,1fr)] gap-3">
                      <div className="flex flex-col items-center" aria-hidden="true">
                        <span className="mt-1 size-2 rounded-full bg-info" />
                        {index < activeTrace.events.length - 1 ? <span className="mt-1 w-px flex-1 bg-border" /> : null}
                      </div>
                      <div className="min-w-0 pb-1">
                        <div className="flex flex-wrap items-center gap-x-2 gap-y-1 text-xs">
                          <span className="font-medium">{event.category}</span>
                          <span className="text-muted-foreground">{event.source}</span>
                          <span className="font-mono text-[10px] text-muted-foreground">{event.status}</span>
                          <span className="ml-auto text-[10px] text-muted-foreground">{formatDate(event.timeUtc)}</span>
                        </div>
                        <p className="mt-1 break-words text-xs leading-5 text-muted-foreground">{event.summary}</p>
                      </div>
                    </li>
                  ))}
                </ol>
              </div>
            ) : null}
          </PanelBody>
        )}
      </Panel>

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

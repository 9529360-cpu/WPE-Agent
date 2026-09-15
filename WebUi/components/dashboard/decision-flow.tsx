'use client'

import { Workflow } from 'lucide-react'
import {
  useWpeRuntime,
  type RuntimeAgentHandoff,
  type RuntimeAgentOperation,
} from '@/components/runtime-bridge'
import { RuntimeUnavailable } from '@/components/runtime-state'
import { Panel, PanelHeader } from '@/components/ui/panel'
import { StatusBadge } from '@/components/ui/status-badge'
import { getRoleDirectory } from '@/lib/agents/roles'
import { useI18n } from '@/lib/i18n/context'

const canonicalRoles = getRoleDirectory()

function statusToken(status: RuntimeAgentOperation['status'] | undefined) {
  if (status === 'running' || status === 'monitoring') return 'success'
  if (status === 'degraded') return 'warning'
  return 'muted'
}

function latestCanonicalHandoffs(handoffs: RuntimeAgentHandoff[]) {
  const byEdge = new Map<string, RuntimeAgentHandoff>()
  for (const handoff of [...handoffs].sort((left, right) => right.occurredAtUtc.localeCompare(left.occurredAtUtc))) {
    const key = `${handoff.sourceRoleId.toLowerCase()}->${handoff.targetRoleId.toLowerCase()}`
    if (!byEdge.has(key)) byEdge.set(key, handoff)
  }
  return byEdge
}

export function DecisionFlow() {
  const runtime = useWpeRuntime()
  const { t, formatDate } = useI18n()

  if (!runtime.runtimeFresh) {
    return <RuntimeUnavailable stale={Boolean(runtime.lastUpdated)} subject={t('dashboard.decisionFlow')} />
  }

  const operationsState = runtime.collectionStates?.agentOperations ?? 'unsupported'
  const handoffsState = runtime.collectionStates?.agentHandoffs ?? 'unsupported'
  const operations = new Map((runtime.agentOperations ?? []).map((operation) => [operation.roleId.toLowerCase(), operation]))
  const handoffs = handoffsState === 'available' ? latestCanonicalHandoffs(runtime.agentHandoffs ?? []) : new Map<string, RuntimeAgentHandoff>()

  if (operationsState !== 'available') {
    const message = operationsState === 'stale' ? t('agents.operationsStale') : t('agents.operationsUnavailable')
    return (
      <Panel className="h-full">
        <PanelHeader icon={<Workflow className="size-4" />} title={t('dashboard.decisionFlow')} action={<span className="text-[11px] text-muted-foreground">{operationsState}</span>} />
        <div className="p-4 text-sm text-muted-foreground">{message}</div>
      </Panel>
    )
  }

  return (
    <Panel className="h-full">
      <PanelHeader
        icon={<Workflow className="size-4" />}
        title={t('dashboard.decisionFlow')}
        action={<span className="font-mono text-[11px] text-muted-foreground">{runtime.status ?? 'IDLE'}</span>}
      />
      <ol className="p-4">
        {canonicalRoles.map((role, index) => {
          const operation = operations.get(role.id)
          const status = operation?.status ?? 'stopped'
          const nextRole = canonicalRoles[index + 1]
          const handoff = nextRole ? handoffs.get(`${role.id}->${nextRole.id}`) : undefined

          return (
            <li key={role.id} className="relative pb-5 last:pb-0">
              {nextRole ? <span className="absolute left-[11px] top-6 h-[calc(100%-4px)] border-l border-border" aria-hidden="true" /> : null}
              <div className="flex min-w-0 gap-3">
                <div className="relative z-10 mt-0.5 flex size-6 shrink-0 items-center justify-center rounded-full border border-border bg-card font-mono text-[10px] text-muted-foreground">
                  {index + 1}
                </div>
                <div className="min-w-0 flex-1">
                  <div className="flex flex-wrap items-center justify-between gap-2">
                    <span className="text-sm font-medium">{role.name}</span>
                    <StatusBadge token={statusToken(status)} label={t(`agents.${status}` as 'agents.stopped')} pulse={status === 'running'} />
                  </div>
                  <p className="mt-1 break-words text-xs text-muted-foreground">
                    {operation?.activity ?? t('agents.noActivity')}
                    {operation?.lastActivityAtUtc ? ` · ${formatDate(operation.lastActivityAtUtc)}` : ''}
                  </p>
                  {nextRole ? (
                    <div className="mt-2 border-l-2 border-border pl-3 text-xs text-muted-foreground">
                      {handoffsState !== 'available' ? (
                        t('agents.handoffUnsupported')
                      ) : handoff ? (
                        <>
                          <span className="font-medium text-foreground">{role.name} → {nextRole.name}</span>
                          <span className="break-words"> · {handoff.result}</span>
                          <span> · {formatDate(handoff.occurredAtUtc)}</span>
                        </>
                      ) : (
                        t('agents.noHandoffs')
                      )}
                    </div>
                  ) : null}
                </div>
              </div>
            </li>
          )
        })}
      </ol>
    </Panel>
  )
}

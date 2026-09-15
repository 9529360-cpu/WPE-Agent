'use client'

import { Workflow } from 'lucide-react'
import {
  useWpeRuntime,
  type RuntimeAgentHandoff,
  type RuntimeAgentOperation,
} from '@/components/runtime-bridge'
import { RuntimeUnavailable } from '@/components/runtime-state'
import { Panel, PanelBody, PanelHeader } from '@/components/ui/panel'
import { StatusBadge } from '@/components/ui/status-badge'
import { getRoleDirectory } from '@/lib/agents/roles'
import { useI18n } from '@/lib/i18n/context'

const canonicalRoles = getRoleDirectory()
const canonicalEdges = new Set(
  canonicalRoles.slice(0, -1).map((role, index) => `${role.id}->${canonicalRoles[index + 1].id}`),
)

function statusToken(status: RuntimeAgentOperation['status'] | undefined) {
  if (status === 'running' || status === 'monitoring') return 'success'
  if (status === 'degraded') return 'warning'
  return 'muted'
}

function latestCanonicalHandoff(handoffs: RuntimeAgentHandoff[]) {
  return [...handoffs]
    .filter((handoff) => canonicalEdges.has(`${handoff.sourceRoleId.toLowerCase()}->${handoff.targetRoleId.toLowerCase()}`))
    .sort((left, right) => right.occurredAtUtc.localeCompare(left.occurredAtUtc))[0]
}

export function DecisionFlow() {
  const runtime = useWpeRuntime()
  const { t, formatDate } = useI18n()

  if (!runtime.runtimeFresh) {
    return <RuntimeUnavailable stale={Boolean(runtime.lastUpdated)} subject={t('dashboard.decisionFlow')} />
  }

  const operationsState = runtime.collectionStates?.agentOperations ?? 'unsupported'
  const handoffsState = runtime.collectionStates?.agentHandoffs ?? 'unsupported'

  if (operationsState !== 'available') {
    return (
      <Panel>
        <PanelHeader
          icon={<Workflow className="size-4" />}
          title={t('dashboard.decisionFlow')}
          action={<span className="font-mono text-[10px] uppercase text-muted-foreground">{operationsState}</span>}
        />
        <PanelBody className="py-8 text-sm text-muted-foreground">
          {t(operationsState === 'stale' ? 'agents.operationsStale' : 'agents.operationsUnavailable')}
        </PanelBody>
      </Panel>
    )
  }

  const operations = new Map(
    (runtime.agentOperations ?? []).map((operation) => [operation.roleId.toLowerCase(), operation]),
  )
  const latestHandoff = handoffsState === 'available'
    ? latestCanonicalHandoff(runtime.agentHandoffs ?? [])
    : undefined
  const roleNames = new Map(canonicalRoles.map((role) => [role.id, role.name]))

  return (
    <Panel>
      <PanelHeader
        icon={<Workflow className="size-4" />}
        title={t('dashboard.decisionFlow')}
        action={<span className="font-mono text-[10px] text-muted-foreground">7 AGENTS · {runtime.status ?? 'IDLE'}</span>}
      />
      <PanelBody className="space-y-4">
        <ol className="grid gap-2 sm:grid-cols-2 lg:grid-cols-4 2xl:grid-cols-7">
          {canonicalRoles.map((role, index) => {
            const operation = operations.get(role.id)
            const status = operation?.status ?? 'stopped'
            return (
              <li key={role.id} className="min-w-0 rounded-md border border-border bg-card/50 p-3">
                <div className="flex items-start justify-between gap-2">
                  <div className="min-w-0">
                    <div className="font-mono text-[10px] text-muted-foreground">{String(index + 1).padStart(2, '0')}</div>
                    <div className="mt-1 truncate text-sm font-medium">{role.name}</div>
                  </div>
                  <StatusBadge
                    token={statusToken(status)}
                    label={t(`agents.${status}` as 'agents.stopped')}
                    pulse={status === 'running'}
                  />
                </div>
                <p className="mt-3 line-clamp-2 min-h-8 break-words text-xs leading-4 text-muted-foreground">
                  {operation?.activity ?? t('agents.noActivity')}
                </p>
                <div className="mt-3 flex flex-wrap items-center gap-x-2 gap-y-1 border-t border-border pt-2 font-mono text-[10px] text-muted-foreground">
                  <span>{operation?.mode ?? 'Local Only'}</span>
                  {operation?.lastActivityAtUtc ? <span>{formatDate(operation.lastActivityAtUtc)}</span> : null}
                </div>
              </li>
            )
          })}
        </ol>

        <div className="flex flex-wrap items-center gap-x-2 gap-y-1 border-t border-border pt-3 text-xs text-muted-foreground">
          <span className="font-medium text-foreground">{t('agents.recentHandoffs')}</span>
          {handoffsState !== 'available' ? (
            <span>{t('agents.handoffUnsupported')}</span>
          ) : latestHandoff ? (
            <>
              <span>
                {roleNames.get(latestHandoff.sourceRoleId.toLowerCase()) ?? latestHandoff.sourceRoleId}
                {' → '}
                {roleNames.get(latestHandoff.targetRoleId.toLowerCase()) ?? latestHandoff.targetRoleId}
              </span>
              <span className="break-words">· {latestHandoff.result}</span>
              <span>· {formatDate(latestHandoff.occurredAtUtc)}</span>
            </>
          ) : (
            <span>{t('agents.noHandoffs')}</span>
          )}
        </div>
      </PanelBody>
    </Panel>
  )
}

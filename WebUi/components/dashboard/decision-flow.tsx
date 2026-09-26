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
import { runtimeLabel } from '@/lib/runtime-labels'

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
  const { t, formatDate, locale } = useI18n()

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
  const handoffUnavailable = handoffsState === 'stale'
    ? t('common.stale')
    : handoffsState === 'error'
      ? t('common.error')
      : t('agents.handoffUnsupported')


  const roleCopy: Record<string,{name:string;activity:string}> | undefined = locale === 'zh_CN' ? {
    market:{name:'市场 Agent',activity:'读取实时行情、K 线和账户数据，更新市场证据。'},
    decision:{name:'决策 Agent',activity:'根据本地市场结构、触发与确认形成交易意图或继续观望。'},
    risk:{name:'风控 Agent',activity:'检查仓位、风险限制和交易资格，不满足条件就阻止执行。'},
    execution:{name:'执行 Agent',activity:'处理通过风控的订单，跟踪成交与保护单。'},
    recovery:{name:'恢复 Agent',activity:'核对交易所与本地账本，处理重启、超时和异常恢复。'},
    audit:{name:'审计 Agent',activity:'记录完整交易链路，验证每个环节的证据与状态。'},
  } : locale === 'zh_TW' ? {
    market:{name:'市場 Agent',activity:'讀取即時行情、K 線和帳戶資料，更新市場證據。'},
    decision:{name:'決策 Agent',activity:'根據本地市場結構、觸發與確認形成交易意圖或繼續觀望。'},
    risk:{name:'風控 Agent',activity:'檢查持倉、風險限制和交易資格，不符合條件就阻止執行。'},
    execution:{name:'執行 Agent',activity:'處理通過風控的訂單，追蹤成交與保護單。'},
    recovery:{name:'恢復 Agent',activity:'核對交易所與本地帳本，處理重啟、逾時和異常恢復。'},
    audit:{name:'稽核 Agent',activity:'記錄完整交易鏈路，驗證每個環節的證據與狀態。'},
  } : undefined
  const flowTitle = locale === 'zh_CN' ? '6 个运行 Agent 协同链路' : locale === 'zh_TW' ? '6 個運行 Agent 協同鏈路' : '6 RUNTIME AGENTS'

  return (
    <Panel>
      <PanelHeader
        icon={<Workflow className="size-4" />}
        title={t('dashboard.decisionFlow')}
        action={<span className="text-[10px] text-muted-foreground">{flowTitle} · {runtimeLabel(runtime.status,locale) ?? t('common.notProvided')}</span>}
      />
      <PanelBody className="space-y-4">
        <ol className="grid gap-2 sm:grid-cols-2 lg:grid-cols-4 2xl:grid-cols-7">
          {canonicalRoles.map((role, index) => {
            const operation = operations.get(role.id)
            const status = operation?.status
            return (
              <li key={role.id} className="min-w-0 rounded-md border border-border bg-card/50 p-3">
                <div className="flex items-start justify-between gap-2">
                  <div className="min-w-0">
                    <div className="font-mono text-[10px] text-muted-foreground">{String(index + 1).padStart(2, '0')}</div>
                    <div className="mt-1 truncate text-sm font-medium">{roleCopy?.[role.id]?.name ?? role.name}</div>
                  </div>
                  <StatusBadge
                    token={statusToken(status)}
                    label={status ? t(`agents.${status}` as 'agents.stopped') : t('common.unavailable')}
                    pulse={status === 'running'}
                  />
                </div>
                <p className="mt-3 line-clamp-2 min-h-8 break-words text-xs leading-4 text-muted-foreground">
                  {operation ? (locale.startsWith('zh_') ? (status==='degraded' ? (locale==='zh_CN'?'状态异常，请查看系统监控。':'狀態異常，請查看系統監控。') : roleCopy?.[role.id]?.activity) : operation.activity) ?? t('agents.noActivity') : t('common.unavailable')}
                </p>
                <div className="mt-3 flex flex-wrap items-center gap-x-2 gap-y-1 border-t border-border pt-2 font-mono text-[10px] text-muted-foreground">
                  <span>{operation?.mode ? (locale==='zh_CN'?'纯本地':locale==='zh_TW'?'純本地':operation.mode) : t('common.notProvided')}</span>
                  {operation?.lastActivityAtUtc ? <span>{formatDate(operation.lastActivityAtUtc)}</span> : null}
                </div>
              </li>
            )
          })}
        </ol>

        <div className="flex flex-wrap items-center gap-x-2 gap-y-1 border-t border-border pt-3 text-xs text-muted-foreground">
          <span className="font-medium text-foreground">{t('agents.recentHandoffs')}</span>
          {handoffsState !== 'available' ? (
            <span>{handoffUnavailable}</span>
          ) : latestHandoff ? (
            <>
              <span>
                {roleNames.get(latestHandoff.sourceRoleId.toLowerCase()) ?? latestHandoff.sourceRoleId}
                {' → '}
                {roleNames.get(latestHandoff.targetRoleId.toLowerCase()) ?? latestHandoff.targetRoleId}
              </span>
              <span className="break-words">· {locale==='zh_CN'?'交接完成':locale==='zh_TW'?'交接完成':latestHandoff.result}</span>
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

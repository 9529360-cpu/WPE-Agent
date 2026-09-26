'use client'

import { Activity, Bot, Clock3, Eye, Radio, ShieldCheck, WalletCards } from 'lucide-react'
import { useWpeRuntime } from '@/components/runtime-bridge'
import { Panel, PanelBody } from '@/components/ui/panel'
import { StatusBadge, StatusDot } from '@/components/ui/status-badge'
import { useI18n } from '@/lib/i18n/context'
import { decisionSummary, runtimeLabel, workflowLabel } from '@/lib/runtime-labels'
import { cn } from '@/lib/utils'

export function AgentLiveStatus() {
  const runtime = useWpeRuntime()
  const { locale, formatDate } = useI18n()
  const zh = locale === 'zh_CN'
  const zht = locale === 'zh_TW'
  const fresh = runtime.runtimeFresh === true
  const running = runtime.agentIsRunning === true
  const degraded = fresh && running && runtime.status === 'Degraded'
  const observer = running && runtime.agentControlAllowed === false
  const mode = runtime.authorizationMode?.value?.mode
  const decision = decisionSummary(runtime.lastDecision, locale)
  const positions = runtime.collectionStates?.positions === 'available' ? runtime.positions?.length ?? 0 : undefined
  const orders = runtime.collectionStates?.orders === 'available' ? runtime.orders?.length ?? 0 : undefined

  const copy = zh ? {
    running: 'Agent 正在运行',
    stopped: 'Agent 未运行',
    degraded: 'Agent 运行中，但有异常',
    runningBody: '后台交易内核正在持续读取行情、分析市场结构，并按确定性风控规则等待或执行交易。',
    stoppedBody: '当前没有检测到正在运行的交易 Agent。',
    degradedBody: '交易 Agent 仍在运行，但部分运行证据或依赖状态异常，请查看下方系统状态。',
    environment: '交易环境', mode: '交易模式', stage: '当前阶段', decision: '当前动作', next: '下次扫描',
    heartbeat: '运行心跳', positions: '持仓', orders: '挂单', risk: '风控', exchange: '交易所',
    observer: '桌面观察模式', observerHint: '交易由后台 Agent 独立运行，本窗口只负责观察，不会启动第二套交易循环。',
    fresh: '数据实时', stale: '数据已过期', connected: '已连接', disconnected: '未连接', ready: '就绪', notReady: '未就绪',
    unavailable: '不可用', secondsAgo: '秒前',
  } : zht ? {
    running: 'Agent 正在運行',
    stopped: 'Agent 未運行',
    degraded: 'Agent 運行中，但有異常',
    runningBody: '後台交易內核正在持續讀取行情、分析市場結構，並按確定性風控規則等待或執行交易。',
    stoppedBody: '目前沒有偵測到正在運行的交易 Agent。',
    degradedBody: '交易 Agent 仍在運行，但部分運行證據或依賴狀態異常，請查看下方系統狀態。',
    environment: '交易環境', mode: '交易模式', stage: '目前階段', decision: '目前動作', next: '下次掃描',
    heartbeat: '運行心跳', positions: '持倉', orders: '掛單', risk: '風控', exchange: '交易所',
    observer: '桌面觀察模式', observerHint: '交易由後台 Agent 獨立運行，本視窗只負責觀察，不會啟動第二套交易循環。',
    fresh: '資料即時', stale: '資料已過期', connected: '已連線', disconnected: '未連線', ready: '就緒', notReady: '未就緒',
    unavailable: '不可用', secondsAgo: '秒前',
  } : {
    running: 'Agent is running',
    stopped: 'Agent is stopped',
    degraded: 'Agent is running with issues',
    runningBody: 'The trading runtime is reading markets, analyzing structure, and waiting for or executing deterministic risk-approved actions.',
    stoppedBody: 'No active trading Agent is currently detected.',
    degradedBody: 'The Agent is still running, but one or more runtime dependencies are degraded.',
    environment: 'Environment', mode: 'Trading mode', stage: 'Current stage', decision: 'Current action', next: 'Next scan',
    heartbeat: 'Heartbeat', positions: 'Positions', orders: 'Open orders', risk: 'Risk', exchange: 'Exchange',
    observer: 'Desktop observer', observerHint: 'Trading is owned by the background Agent. This window is observation-only.',
    fresh: 'Live data', stale: 'Stale data', connected: 'Connected', disconnected: 'Disconnected', ready: 'Ready', notReady: 'Not ready',
    unavailable: 'Unavailable', secondsAgo: 's ago',
  }

  const title = !fresh || !running ? copy.stopped : degraded ? copy.degraded : copy.running
  const body = !fresh || !running ? copy.stoppedBody : degraded ? copy.degradedBody : copy.runningBody
  const tone = !fresh || !running ? 'muted' : degraded ? 'warning' : 'success'
  const age = Math.max(0, Math.round(runtime.runtimeAgeSeconds ?? 0))

  const metrics = [
    { icon: Radio, label: copy.environment, value: runtimeLabel(runtime.environment, locale) ?? copy.unavailable },
    { icon: Bot, label: copy.mode, value: runtimeLabel(mode, locale) ?? copy.unavailable },
    { icon: Activity, label: copy.stage, value: workflowLabel(runtime.workflowNode, locale) ?? copy.unavailable },
    { icon: ShieldCheck, label: copy.risk, value: runtime.riskReady ? copy.ready : copy.notReady },
    { icon: WalletCards, label: copy.positions, value: positions === undefined ? '—' : String(positions) },
    { icon: Radio, label: copy.orders, value: orders === undefined ? '—' : String(orders) },
  ]

  return (
    <Panel className={cn(
      'overflow-hidden',
      fresh && running && !degraded && 'border-success/30 bg-success/[0.035]',
      degraded && 'border-warning/30 bg-warning/[0.035]',
    )}>
      <PanelBody className="p-0">
        <div className="flex flex-col gap-5 p-5 lg:flex-row lg:items-start lg:justify-between">
          <div className="min-w-0 max-w-3xl">
            <div className="flex flex-wrap items-center gap-2">
              <StatusBadge token={tone} label={title} pulse={fresh && running && !degraded} className="px-2.5 py-1 text-sm" />
              <StatusBadge token={fresh ? 'success' : 'warning'} label={fresh ? \`${copy.fresh} · ${age}${copy.secondsAgo}\` : copy.stale} />
              {observer ? <StatusBadge token="info" label={copy.observer} /> : null}
            </div>
            <h2 className="mt-4 text-xl font-semibold tracking-tight text-foreground">{decision.title}</h2>
            <p className="mt-1 max-w-2xl text-sm leading-6 text-muted-foreground">
              {running && fresh ? decision.detail || body : body}
            </p>
            {observer ? (
              <div className="mt-3 flex items-start gap-2 rounded-lg border border-info/20 bg-info/5 px-3 py-2 text-xs leading-5 text-muted-foreground">
                <Eye className="mt-0.5 size-3.5 shrink-0 text-info" />
                <span>{copy.observerHint}</span>
              </div>
            ) : null}
          </div>

          <div className="grid min-w-0 grid-cols-2 gap-x-6 gap-y-3 text-xs sm:grid-cols-3 lg:min-w-[420px]">
            {metrics.map(({ icon: Icon, label, value }) => (
              <div key={label} className="min-w-0">
                <div className="flex items-center gap-1.5 text-muted-foreground"><Icon className="size-3.5" />{label}</div>
                <div className="mt-1 truncate text-sm font-medium text-foreground" title={value}>{value}</div>
              </div>
            ))}
          </div>
        </div>

        <div className="grid border-t border-border/80 bg-background/30 text-xs sm:grid-cols-2 lg:grid-cols-4">
          <div className="px-5 py-3">
            <div className="text-muted-foreground">{copy.decision}</div>
            <div className="mt-1 font-medium text-foreground">{decision.title}</div>
          </div>
          <div className="border-t border-border/80 px-5 py-3 sm:border-l sm:border-t-0">
            <div className="text-muted-foreground">{copy.exchange}</div>
            <div className="mt-1 flex items-center gap-1.5 font-medium text-foreground">
              <StatusDot token={runtime.exchangeConnected ? 'success' : 'danger'} />
              {runtime.exchangeConnected ? copy.connected : copy.disconnected}
            </div>
          </div>
          <div className="border-t border-border/80 px-5 py-3 lg:border-l lg:border-t-0">
            <div className="text-muted-foreground">{copy.next}</div>
            <div className="mt-1 font-medium text-foreground">{runtime.nextCycleAtUtc ? formatDate(runtime.nextCycleAtUtc) : copy.unavailable}</div>
          </div>
          <div className="border-t border-border/80 px-5 py-3 sm:border-l lg:border-t-0">
            <div className="flex items-center gap-1.5 text-muted-foreground"><Clock3 className="size-3.5" />{copy.heartbeat}</div>
            <div className="mt-1 font-medium text-foreground">{runtime.runtimeHeartbeatAtUtc ? formatDate(runtime.runtimeHeartbeatAtUtc) : copy.unavailable}</div>
          </div>
        </div>
      </PanelBody>
    </Panel>
  )
}

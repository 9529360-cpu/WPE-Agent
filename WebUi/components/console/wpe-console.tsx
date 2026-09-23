'use client'

import { useEffect, useMemo, useState, type ReactNode } from 'react'
import {
  postRuntimeHostCommand,
  useWpeRuntime,
  type RuntimeCollectionState,
  type WpeRuntimeState,
} from '@/components/runtime-bridge'
import { useI18n } from '@/lib/i18n/context'
import { localeMeta, locales, type Locale } from '@/lib/i18n/dictionaries'

type PageId = 'home' | 'agents' | 'teacher' | 'trading' | 'research' | 'risk' | 'monitoring' | 'settings'
type HostCommand = 'open-settings' | 'open-notification-settings' | 'agent-start' | 'agent-stop'
type Tone = 'neutral' | 'info' | 'success' | 'warning' | 'danger'

const AGENT_CHAIN = ['Market', 'Research', 'Strategy', 'Risk', 'Execution', 'Recovery', 'Audit'] as const

const AGENT_LABELS: Record<string, string> = {
  Market: '市场感知',
  Research: '研究分析',
  Strategy: '策略引擎',
  Risk: '风控中枢',
  Execution: '交易执行',
  Recovery: '异常恢复',
  Audit: '审计复盘',
}

const PAGE_META: Record<PageId, { title: string; description: string; glyph: string }> = {
  home: { title: '运行总览', description: '系统可信度、账户、风险与工作流状态', glyph: '总' },
  agents: { title: '智能体团队', description: '核心角色、协作链路与交接记录', glyph: '协' },
  teacher: { title: '金融导师', description: '市场讲解、研究候选、纪律提醒与结果复盘', glyph: '师' },
  trading: { title: '交易', description: '持仓、订单、待审核事项与历史记录', glyph: '交' },
  research: { title: '策略与研究', description: '市场分析、回测与只读研究证据', glyph: '策' },
  risk: { title: '风险', description: '风险准备、熔断、暴露与授权状态', glyph: '风' },
  monitoring: { title: '监控', description: '运行时、连接、通知、AI 使用和审计', glyph: '监' },
  settings: { title: '设置', description: '安全存储状态与宿主设置入口', glyph: '设' },
}

const NAV_GROUPS: Array<{ label?: string; items: PageId[] }> = [
  { items: ['home', 'agents', 'teacher'] },
  { label: '交易与研究', items: ['trading', 'research', 'risk'] },
  { label: '系统', items: ['monitoring', 'settings'] },
]

function isFiniteNumber(value: unknown): value is number {
  return typeof value === 'number' && Number.isFinite(value)
}

function formatNumber(value: unknown, digits = 2): string {
  if (!isFiniteNumber(value)) return '未提供'
  return new Intl.NumberFormat('zh-CN', {
    minimumFractionDigits: 0,
    maximumFractionDigits: digits,
  }).format(value)
}

function formatInteger(value: unknown): string {
  if (!isFiniteNumber(value)) return '未提供'
  return new Intl.NumberFormat('zh-CN', { maximumFractionDigits: 0 }).format(value)
}

function formatPercent(value: unknown, digits = 2): string {
  if (!isFiniteNumber(value)) return '未提供'
  return `${formatNumber(value, digits)}%`
}

function formatUtc(value: unknown): string {
  if (typeof value !== 'string' || !value.trim()) return '未提供'
  const date = new Date(value)
  if (Number.isNaN(date.getTime())) return '未提供'
  return new Intl.DateTimeFormat('zh-CN', {
    year: 'numeric',
    month: '2-digit',
    day: '2-digit',
    hour: '2-digit',
    minute: '2-digit',
    second: '2-digit',
    hour12: false,
  }).format(date)
}

function relativeFreshness(ageSeconds: unknown): string {
  if (!isFiniteNumber(ageSeconds)) return '时间未知'
  if (ageSeconds < 2) return '刚刚更新'
  if (ageSeconds < 60) return `${Math.max(0, Math.round(ageSeconds))} 秒前`
  return `${Math.round(ageSeconds / 60)} 分钟前`
}

function text(value: unknown, fallback = '未提供'): string {
  return typeof value === 'string' && value.trim() ? value.trim() : fallback
}

function booleanLabel(value: unknown, yes = '是', no = '否'): string {
  if (value === true) return yes
  if (value === false) return no
  return '未提供'
}

function raw(runtime: WpeRuntimeState): Record<string, unknown> {
  return runtime as unknown as Record<string, unknown>
}

function rawNumber(runtime: WpeRuntimeState, key: string): number | undefined {
  const value = raw(runtime)[key]
  return isFiniteNumber(value) ? value : undefined
}

function rawBoolean(runtime: WpeRuntimeState, key: string): boolean | undefined {
  const value = raw(runtime)[key]
  return typeof value === 'boolean' ? value : undefined
}

function collectionState(runtime: WpeRuntimeState, key: string, fallback?: RuntimeCollectionState): RuntimeCollectionState {
  const states = runtime.collectionStates as Record<string, RuntimeCollectionState | undefined> | undefined
  return states?.[key] ?? fallback ?? 'unsupported'
}

function stateLabel(state: RuntimeCollectionState): string {
  if (state === 'available') return '可用'
  if (state === 'stale') return '等待最新同步'
  if (state === 'error') return '读取错误'
  return '当前不支持'
}

function stateTone(state: RuntimeCollectionState): Tone {
  if (state === 'available') return 'success'
  if (state === 'stale') return 'warning'
  if (state === 'error') return 'danger'
  return 'neutral'
}

function agentStatusLabel(status: unknown): string {
  switch (status) {
    case 'running': return '运行中'
    case 'monitoring': return '持续监控'
    case 'waiting': return '待命中'
    case 'degraded': return '降级运行'
    case 'stopped': return '已停止'
    default: return text(status, '状态未知')
  }
}

function agentStatusTone(status: unknown): Tone {
  if (status === 'running') return 'success'
  if (status === 'monitoring') return 'success'
  if (status === 'waiting') return 'neutral'
  if (status === 'degraded') return 'warning'
  if (status === 'stopped') return 'neutral'
  return 'neutral'
}

function recommendationLabel(value: unknown): string {
  const labels: Record<string, string> = {
    research_candidate: '研究候选',
    priority_watch: '优先观察',
    wait_for_confirmation: '等待确认',
    avoid_high_risk: '高风险回避',
    expired: '已过期',
    unavailable: '当前不可用',
  }
  return labels[String(value)] ?? text(value)
}

function lessonKindLabel(value: unknown): string {
  const labels: Record<string, string> = {
    morning: '早间课程',
    afternoon: '午间课程',
    evening: '晚间课程',
    event: '事件课程',
    material_event: '重大事件课程',
    weekly: '周度课程',
    post_trade: '交易后复盘',
  }
  return labels[String(value).toLowerCase()] ?? text(value, '课程')
}

function modeLabel(value: unknown): string {
  const labels: Record<string, string> = {
    Research: '研究模式',
    Signal: '信号模式',
    Review: '审核模式',
    Auto: '自动模式',
  }
  return labels[String(value)] ?? text(value)
}

function runtimeValueLabel(value: unknown): string {
  const key = String(value ?? '').trim().toUpperCase()
  const labels: Record<string, string> = {
    READY: '就绪', RUNNING: '运行中', IDLE: '空闲', DEGRADED: '降级运行', BLOCKED: '已阻塞',
    OBSERVATION: '观察阶段', MARKET_CONNECTED: '市场已连接', CONNECTED: '已连接', DISCONNECTED: '未连接',
    UNKNOWN: '未知', UNAVAILABLE: '不可用', PENDING: '等待中', APPROVED: '已批准', REJECTED: '已拒绝',
  }
  return labels[key] ?? text(value)
}

function lifecycleLabel(value: unknown): string {
  const labels: Record<string, string> = {
    Draft: '草稿', Backtested: '已回测', Shadow: '影子观察', Active: '运行中', Degraded: '已降级', Retired: '已退役',
  }
  return labels[String(value)] ?? runtimeValueLabel(value)
}

function postHostCommand(type: HostCommand): boolean {
  if (type === 'agent-start' || type === 'agent-stop') return postRuntimeHostCommand(type)
  const bridge = (window as Window & {
    chrome?: { webview?: { postMessage: (message: { type: HostCommand }) => void } }
  }).chrome?.webview
  if (!bridge) return false
  bridge.postMessage({ type })
  return true
}

function Badge({ children, tone = 'neutral', title }: { children: ReactNode; tone?: Tone; title?: string }) {
  return <span className={`wpe-badge wpe-badge--${tone}`} title={title}>{children}</span>
}

function StatusMark({ tone = 'neutral' }: { tone?: Tone }) {
  return <span className={`wpe-status-mark wpe-status-mark--${tone}`} aria-hidden="true" />
}

function Panel({ title, description, action, children, className = '' }: {
  title?: string
  description?: string
  action?: ReactNode
  children: ReactNode
  className?: string
}) {
  return (
    <section className={`wpe-panel ${className}`}>
      {(title || description || action) && (
        <header className="wpe-panel__header">
          <div className="wpe-panel__heading">
            {title && <h2>{title}</h2>}
            {description && <p>{description}</p>}
          </div>
          {action && <div className="wpe-panel__action">{action}</div>}
        </header>
      )}
      <div className="wpe-panel__body">{children}</div>
    </section>
  )
}

function Metric({ label, value, note, tone = 'neutral' }: {
  label: string
  value: ReactNode
  note?: string
  tone?: Tone
}) {
  return (
    <div className={`wpe-metric wpe-metric--${tone}`}>
      <div className="wpe-metric__label">{label}</div>
      <div className="wpe-metric__value">{value}</div>
      {note && <div className="wpe-metric__note">{note}</div>}
    </div>
  )
}

function KeyValue({ label, value, mono = false, title }: {
  label: string
  value: ReactNode
  mono?: boolean
  title?: string
}) {
  return (
    <div className="wpe-kv">
      <dt>{label}</dt>
      <dd className={mono ? 'wpe-mono' : undefined} title={title}>{value}</dd>
    </div>
  )
}

function StateNotice({ state, message, emptyMessage }: {
  state: RuntimeCollectionState
  message?: string
  emptyMessage?: string
}) {
  const tone = stateTone(state)
  const body = state === 'stale'
    ? '正在等待下一次有效采样，期间不展示旧的可操作数值。'
    : state === 'unsupported'
      ? '当前运行环境尚未连接此能力，不显示模拟数据。'
      : state === 'error'
        ? text(message, '宿主未提供可展示的安全诊断。')
        : text(emptyMessage, '当前没有记录。')

  return (
    <div className={`wpe-state-notice wpe-state-notice--${tone}`} role="status">
      <StatusMark tone={tone} />
      <div>
        <strong>{state === 'available' ? '暂无记录' : stateLabel(state)}</strong>
        <p>{body}</p>
      </div>
    </div>
  )
}

function CollectionGate({ state, message, empty, emptyMessage, children }: {
  state: RuntimeCollectionState
  message?: string
  empty?: boolean
  emptyMessage?: string
  children: ReactNode
}) {
  if (state !== 'available') return <StateNotice state={state} message={message} />
  if (empty) return <StateNotice state="available" emptyMessage={emptyMessage} />
  return children
}

function DenseTable({ columns, rows, emptyMessage = '当前没有记录。' }: {
  columns: Array<{ key: string; label: string; align?: 'left' | 'right' }>
  rows: Array<Record<string, ReactNode>>
  emptyMessage?: string
}) {
  if (!rows.length) return <StateNotice state="available" emptyMessage={emptyMessage} />
  return (
    <div className="wpe-table-wrap" tabIndex={0} aria-label="可横向滚动的数据表格">
      <table className="wpe-table">
        <thead>
          <tr>{columns.map(column => <th key={column.key} className={column.align === 'right' ? 'is-right' : undefined}>{column.label}</th>)}</tr>
        </thead>
        <tbody>
          {rows.map((row, index) => (
            <tr key={String(row.__key ?? index)}>
              {columns.map(column => <td key={column.key} className={column.align === 'right' ? 'is-right' : undefined}>{row[column.key]}</td>)}
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  )
}

function Tabs({ value, onChange, items }: {
  value: string
  onChange: (value: string) => void
  items: Array<{ id: string; label: string }>
}) {
  return (
    <div className="wpe-tabs" role="tablist" aria-label="页面分区">
      {items.map(item => (
        <button
          key={item.id}
          type="button"
          role="tab"
          aria-selected={value === item.id}
          className={value === item.id ? 'is-active' : undefined}
          onClick={() => onChange(item.id)}
        >
          {item.label}
        </button>
      ))}
    </div>
  )
}

function SectionTitle({ title, description, side }: { title: string; description?: string; side?: ReactNode }) {
  return (
    <div className="wpe-section-title">
      <div>
        <h1>{title}</h1>
        {description && <p>{description}</p>}
      </div>
      {side && <div>{side}</div>}
    </div>
  )
}

function SourceTime({ value, label = '来源时间' }: { value: unknown; label?: string }) {
  const rawValue = typeof value === 'string' ? value : undefined
  return <span className="wpe-source-time" title={rawValue ? `${label}（UTC）：${rawValue}` : undefined}>{label}：{formatUtc(value)}</span>
}

function HashValue({ value }: { value: unknown }) {
  const stringValue = text(value)
  return <code className="wpe-hash" title={stringValue}>{stringValue}</code>
}

function EmptyCell() {
  return <span className="wpe-muted">未提供</span>
}

function GlobalStatusStrip({ runtime }: { runtime: WpeRuntimeState }) {
  const connected = runtime.hostConnected === true
  const fresh = Boolean(runtime.runtimeFresh ?? runtime.freshness?.fresh)
  const connectionReady = runtime.connectionStatus?.state === 'available' && runtime.connectionStatus.value?.ready === true
  const riskReady = runtime.riskReady === true
  return (
    <div className="wpe-global-strip" aria-label="系统关键状态">
      <div><StatusMark tone={connected ? 'success' : 'danger'} /><span>宿主{connected ? '已连接' : '未连接'}</span></div>
      <div><StatusMark tone={fresh ? 'success' : 'warning'} /><span>快照{fresh ? '新鲜' : '不可用'}</span></div>
      <div><StatusMark tone={connectionReady ? 'success' : 'warning'} /><span>提供方{connectionReady ? '就绪' : '未就绪'}</span></div>
      <div><StatusMark tone={riskReady ? 'success' : 'warning'} /><span>风险{riskReady ? '已准备' : '未准备'}</span></div>
    </div>
  )
}

function AgentChain({ runtime, compact = false, onSelect }: {
  runtime: WpeRuntimeState
  compact?: boolean
  onSelect?: (role: string) => void
}) {
  const operations = runtime.agentOperations ?? []
  type AgentOperation = NonNullable<WpeRuntimeState['agentOperations']>[number]
  const byRole = new Map<string, AgentOperation>(operations.map(operation => [operation.roleId.toLowerCase(), operation] as const))
  return (
    <div className={`wpe-agent-chain ${compact ? 'is-compact' : ''}`}>
      {AGENT_CHAIN.map((role, index) => {
        const operation = byRole.get(role.toLowerCase())
        const tone = agentStatusTone(operation?.status)
        return (
          <div className="wpe-agent-chain__item" key={role}>
            <button type="button" onClick={() => onSelect?.(role)} className="wpe-agent-node">
              <span className="wpe-agent-node__index">{index + 1}</span>
              <span className="wpe-agent-node__text">
                <strong>{AGENT_LABELS[role]}</strong>
                <small><StatusMark tone={tone} />{agentStatusLabel(operation?.status)}</small>
              </span>
            </button>
            {index < AGENT_CHAIN.length - 1 && <span className="wpe-agent-chain__arrow" aria-hidden="true">→</span>}
          </div>
        )
      })}
    </div>
  )
}

function RuntimeDisconnected({ runtime }: { runtime: WpeRuntimeState }) {
  return (
    <div className="wpe-startup" role="alert">
      <section className="wpe-startup__hero">
        <div className="wpe-startup__eyebrow"><StatusMark tone="warning" /> 本地运行时等待连接</div>
        <h1>控制台已就绪，正在等待 WPE 宿主</h1>
        <p>{text(runtime.runtimeDiagnosticReason, '请通过 WPF WebView2 宿主打开本界面。前端不会联网获取替代数据。')}</p>
        <div className="wpe-startup__actions">
          <button type="button" className="wpe-button wpe-button--primary" onClick={() => postHostCommand('open-settings')}>打开安全设置</button>
          <Badge tone="info">本地优先</Badge>
          <Badge>不使用替代数据</Badge>
        </div>
      </section>
      <div className="wpe-startup__checks">
        <div><span>01</span><strong>连接宿主</strong><p>运行快照只由本地 WPF 宿主注入。</p></div>
        <div><span>02</span><strong>确认环境</strong><p>测试网与实盘凭据、权限和状态完全隔离。</p></div>
        <div><span>03</span><strong>启动工作流</strong><p>核心智能体、风险门和审计链就绪后才允许运行。</p></div>
      </div>
    </div>
  )
}

function isMainnetEnvironment(runtime: WpeRuntimeState) {
  const environment = runtime.connectionStatus?.value?.environment ?? runtime.environment ?? ''
  return /mainnet|live|production|实盘/i.test(environment)
}

function EnvironmentControl({ runtime, compact = false }: { runtime: WpeRuntimeState; compact?: boolean }) {
  const mainnet = isMainnetEnvironment(runtime)
  return (
    <div className={`wpe-environment ${compact ? 'is-compact' : ''}`} aria-label={`当前环境：${mainnet ? '实盘' : '测试网'}`}>
      {!compact && <span>交易环境</span>}
      <button type="button" className={!mainnet ? 'is-active' : ''} onClick={() => postHostCommand('open-settings')}>测试网</button>
      <button type="button" className={mainnet ? 'is-active is-live' : ''} disabled title="当前版本尚未验收实盘交易">实盘未开放</button>
    </div>
  )
}

function ConfirmDialog({ command, runtime, onClose }: { command: 'agent-start' | 'agent-stop'; runtime: WpeRuntimeState; onClose: () => void }) {
  const isStart = command === 'agent-start'
  const mainnet = isMainnetEnvironment(runtime)
  return (
    <div className="wpe-dialog-backdrop" role="presentation">
      <div className="wpe-dialog" role="dialog" aria-modal="true" aria-labelledby="wpe-confirm-title">
        <h2 id="wpe-confirm-title">{isStart ? '启动 Agent？' : '停止 Agent？'}</h2>
        <p>
          {isStart
            ? `系统将在当前${mainnet ? '实盘' : '测试网'}环境下启动 Agent 工作流。所有交易行为仍必须经过策略、风险、执行、恢复与审计链路。`
            : '系统将停止新的 Agent 工作流程。已有执行、恢复和审计状态仍以宿主实际结果为准。'}
        </p>
        <div className="wpe-dialog__actions">
          <button type="button" className="wpe-button wpe-button--secondary" onClick={onClose}>取消</button>
          <button
            type="button"
            className={`wpe-button ${isStart ? 'wpe-button--primary' : 'wpe-button--danger'}`}
            onClick={() => { postHostCommand(command); onClose() }}
          >
            {isStart ? '确认启动' : '确认停止'}
          </button>
        </div>
      </div>
    </div>
  )
}

function Topbar({ runtime, page, onRequestAction }: {
  runtime: WpeRuntimeState
  page: PageId
  onRequestAction: (command: 'agent-start' | 'agent-stop') => void
}) {
  const { locale, setLocale } = useI18n()
  const meta = PAGE_META[page]
  const fresh = Boolean(runtime.runtimeFresh ?? runtime.freshness?.fresh)
  const age = runtime.runtimeAgeSeconds ?? runtime.freshness?.ageSeconds
  const stopAvailable = runtime.agentStopAllowed === true
  const startAvailable = runtime.agentStartAllowed === true
  const action: 'agent-start' | 'agent-stop' = runtime.agentIsRunning === true ? 'agent-stop' : 'agent-start'
  const enabled = action === 'agent-stop' ? stopAvailable : startAvailable
  const reason = runtime.hostConnected !== true
    ? 'WPF 宿主未连接'
    : !fresh
      ? '运行快照不新鲜'
      : text(runtime.runtimeDiagnosticReason, '当前提供方权限不足')

  return (
    <header className="wpe-topbar">
      <div className="wpe-topbar__title">
        <span className="wpe-topbar__glyph">{meta.glyph}</span>
        <div><strong>{meta.title}</strong><small>{meta.description}</small></div>
      </div>
      <div className="wpe-topbar__status">
        <label className="wpe-language-select" title="切换界面语言">
          <span>语言</span>
          <select value={locale} onChange={event => setLocale(event.target.value as Locale)} aria-label="切换界面语言">
            {locales.map(item => <option key={item} value={item}>{localeMeta[item].name}</option>)}
          </select>
        </label>
        <EnvironmentControl runtime={runtime} compact />
        <span className="wpe-topbar__provider">{text(runtime.runtimeProviderId, '提供方未确认')}</span>
        <span className={fresh ? 'wpe-freshness is-fresh' : 'wpe-freshness is-stale'}>
          <StatusMark tone={fresh ? 'success' : 'warning'} />{relativeFreshness(age)}
        </span>
        <button
          type="button"
          className={`wpe-button unsafe-action ${action === 'agent-stop' ? 'wpe-button--danger-outline' : 'wpe-button--primary'}`}
          disabled={!enabled}
          title={!enabled ? reason : undefined}
          onClick={() => onRequestAction(action)}
        >
          {action === 'agent-stop' ? '停止 Agent' : '启动 Agent'}
        </button>
      </div>
    </header>
  )
}

function Sidebar({ page, onChange, runtime }: { page: PageId; onChange: (page: PageId) => void; runtime: WpeRuntimeState }) {
  const connected = runtime.hostConnected === true
  return (
    <aside className="wpe-sidebar">
      <div className="wpe-brand">
        <div className="wpe-brand__mark">W</div>
        <div><strong>WPE Agent</strong><small>本地金融研究系统</small></div>
      </div>
      <nav className="wpe-nav" aria-label="主导航">
        {NAV_GROUPS.map((group, groupIndex) => (
          <div className="wpe-nav__group" key={group.label ?? groupIndex}>
            {group.label && <div className="wpe-nav__group-label">{group.label}</div>}
            {group.items.map(item => (
              <button
                key={item}
                type="button"
                aria-current={page === item ? 'page' : undefined}
                className={page === item ? 'is-active' : undefined}
                onClick={() => onChange(item)}
              >
                <span className="wpe-nav__glyph">{PAGE_META[item].glyph}</span>
                <span>{PAGE_META[item].title}</span>
              </button>
            ))}
          </div>
        ))}
      </nav>
      <div className="wpe-sidebar__footer">
        <EnvironmentControl runtime={runtime} compact />
        <div><StatusMark tone={connected ? 'success' : 'danger'} />{connected ? '宿主已连接' : '宿主未连接'}</div>
        <small>{relativeFreshness(runtime.runtimeAgeSeconds ?? runtime.freshness?.ageSeconds)}</small>
      </div>
    </aside>
  )
}

function MobileNav({ page, onChange, onMore }: { page: PageId; onChange: (page: PageId) => void; onMore: () => void }) {
  const primary: PageId[] = ['home', 'agents', 'teacher', 'trading']
  const moreActive = ['research', 'risk', 'monitoring', 'settings'].includes(page)
  return (
    <nav className="wpe-mobile-nav" aria-label="移动端导航">
      {primary.map(item => (
        <button key={item} type="button" className={page === item ? 'is-active' : undefined} onClick={() => onChange(item)}>
          <span>{PAGE_META[item].glyph}</span><small>{PAGE_META[item].title}</small>
        </button>
      ))}
      <button type="button" className={moreActive ? 'is-active' : undefined} onClick={onMore} aria-haspopup="menu">
        <span>•••</span><small>更多</small>
      </button>
    </nav>
  )
}

function MobileMoreMenu({ onSelect, onClose }: { onSelect: (page: PageId) => void; onClose: () => void }) {
  const items: PageId[] = ['research', 'risk', 'monitoring', 'settings']
  return (
    <div className="wpe-mobile-more-backdrop" role="presentation">
      <div className="wpe-mobile-more" role="menu" aria-label="更多页面">
        <div className="wpe-mobile-more__header"><strong>更多</strong><button type="button" onClick={onClose} aria-label="关闭更多菜单">关闭</button></div>
        <div className="wpe-mobile-more__grid">
          {items.map(item => (
            <button key={item} type="button" role="menuitem" onClick={() => { onSelect(item); onClose() }}>
              <span>{PAGE_META[item].glyph}</span><strong>{PAGE_META[item].title}</strong><small>{PAGE_META[item].description}</small>
            </button>
          ))}
        </div>
      </div>
    </div>
  )
}

function HomePage({ runtime, goTo }: { runtime: WpeRuntimeState; goTo: (page: PageId) => void }) {
  const accountState = collectionState(runtime, 'account')
  const riskState = collectionState(runtime, 'risk')
  const positionState = collectionState(runtime, 'positions')
  const orderState = collectionState(runtime, 'orders')
  const marketState = collectionState(runtime, 'publicMarkets')
  const auditState = collectionState(runtime, 'auditEvents', runtime.auditEvents?.state)
  const connectionState = collectionState(runtime, 'connectionStatus', runtime.connectionStatus?.state)
  const positions = runtime.positions ?? []
  const orders = runtime.orders ?? []
  const audits = runtime.auditEvents?.items ?? []
  const latestHandoff = [...(runtime.agentHandoffs ?? [])].sort((a, b) => b.occurredAtUtc.localeCompare(a.occurredAtUtc))[0]
  const markets = runtime.publicMarkets?.items ?? []

  return (
    <div className="wpe-page-stack">
      <SectionTitle title="运行总览" description="先确认系统可信度，再查看账户、风险和工作流。" />
      <GlobalStatusStrip runtime={runtime} />

      <div className="wpe-grid wpe-grid--4">
        <Metric label="钱包余额（账户计价）" value={accountState === 'available' ? formatNumber(runtime.walletBalance) : stateLabel(accountState)} />
        <Metric label="可用余额（账户计价）" value={accountState === 'available' ? formatNumber(runtime.availableBalance) : stateLabel(accountState)} />
        <Metric label="当日盈亏（账户计价）" value={riskState === 'available' ? formatNumber(runtime.dailyPnl) : stateLabel(riskState)} tone={isFiniteNumber(runtime.dailyPnl) ? runtime.dailyPnl > 0 ? 'success' : runtime.dailyPnl < 0 ? 'danger' : 'neutral' : 'neutral'} />
        <Metric label="最大回撤（%）" value={riskState === 'available' ? formatPercent(runtime.maxDrawdown) : stateLabel(riskState)} tone={isFiniteNumber(runtime.maxDrawdown) && runtime.maxDrawdown > 0 ? 'warning' : 'neutral'} />
      </div>

      <div className="wpe-grid wpe-grid--2-home">
        <Panel title="智能体协作链" description="七个核心角色按交易职责依次协作，金融导师独立于交易权限之外" action={<button className="wpe-link-button" type="button" onClick={() => goTo('agents')}>查看详情</button>}>
          <CollectionGate state={collectionState(runtime, 'agentOperations')} empty={!runtime.agentOperations?.length} emptyMessage="当前没有 Agent 运行记录。">
            <AgentChain runtime={runtime} compact />
            <div className="wpe-latest-handoff">
              <span>最近交接</span>
              {latestHandoff ? (
                <strong>{AGENT_LABELS[latestHandoff.sourceRoleId] ?? latestHandoff.sourceRoleId} → {AGENT_LABELS[latestHandoff.targetRoleId] ?? latestHandoff.targetRoleId}</strong>
              ) : <span className="wpe-muted">当前没有交接记录</span>}
              {latestHandoff && <SourceTime value={latestHandoff.occurredAtUtc} />}
            </div>
          </CollectionGate>
        </Panel>

        <Panel title="系统运行" description="宿主提供的当前状态">
          <dl className="wpe-kv-grid">
            <KeyValue label="智能体状态" value={runtimeValueLabel(runtime.status)} />
            <KeyValue label="授权模式" value={modeLabel(runtime.authorizationMode?.value?.mode)} />
            <KeyValue label="当前流程节点" value={runtimeValueLabel(runtime.workflowNode)} />
            <KeyValue label="实时状态" value={runtimeValueLabel(runtime.realtimeStatus)} />
            <KeyValue label="恢复状态" value={text(runtime.runtimeRecoveryStatus)} />
            <KeyValue label="运行事件序号" value={formatInteger(runtime.runtimeEventSequence)} />
          </dl>
        </Panel>
      </div>

      <div className="wpe-grid wpe-grid--2">
        <Panel title="持仓" action={<button className="wpe-link-button" type="button" onClick={() => goTo('trading')}>全部持仓</button>}>
          <CollectionGate state={positionState} message={runtime.collectionMessages?.positions} empty={!positions.length} emptyMessage="当前没有开放持仓。">
            <DenseTable
              columns={[
                { key: 'symbol', label: '品种' },
                { key: 'side', label: '方向' },
                { key: 'quantity', label: '数量', align: 'right' },
                { key: 'pnl', label: '未实现盈亏', align: 'right' },
              ]}
              rows={positions.slice(0, 5).map(position => ({
                __key: position.symbol,
                symbol: <strong>{position.symbol}</strong>,
                side: position.side,
                quantity: formatNumber(position.quantity, 6),
                pnl: <span className={position.unrealizedPnl > 0 ? 'wpe-positive' : position.unrealizedPnl < 0 ? 'wpe-negative' : undefined}>{formatNumber(position.unrealizedPnl)}</span>,
              }))}
            />
          </CollectionGate>
        </Panel>

        <Panel title="活跃订单" action={<button className="wpe-link-button" type="button" onClick={() => goTo('trading')}>全部订单</button>}>
          <CollectionGate state={orderState} message={runtime.collectionMessages?.orders} empty={!orders.length} emptyMessage="当前没有活跃订单。">
            <DenseTable
              columns={[
                { key: 'symbol', label: '品种' },
                { key: 'side', label: '方向' },
                { key: 'type', label: '类型' },
                { key: 'quantity', label: '数量', align: 'right' },
                { key: 'status', label: '状态' },
              ]}
              rows={orders.slice(0, 5).map(order => ({
                __key: order.orderId,
                symbol: <strong>{order.symbol}</strong>,
                side: order.side,
                type: order.type,
                quantity: formatNumber(order.quantity, 6),
                status: <Badge>{order.status}</Badge>,
              }))}
            />
          </CollectionGate>
        </Panel>
      </div>

      <div className="wpe-grid wpe-grid--2">
        <Panel title="实时行情" description="来自公开市场数据流；交易权限与品种能力由后台独立校验">
          <CollectionGate state={marketState} message={runtime.publicMarkets?.message} empty={!markets.length} emptyMessage="当前没有可用的实时行情。">
            <DenseTable
              columns={[
                { key: 'symbol', label: '品种' },
                { key: 'price', label: '最新价', align: 'right' },
                { key: 'change', label: '24h 涨跌', align: 'right' },
                { key: 'source', label: '数据源' },
                { key: 'updated', label: '更新时间' },
              ]}
              rows={markets.slice(0, 6).map(market => ({
                __key: market.symbol,
                symbol: <strong>{market.symbol}</strong>,
                price: formatNumber(market.price),
                change: <span className={typeof market.changePercent === 'number' && market.changePercent > 0 ? 'wpe-positive' : typeof market.changePercent === 'number' && market.changePercent < 0 ? 'wpe-negative' : undefined}>{formatPercent(market.changePercent)}</span>,
                source: market.source,
                updated: formatUtc(market.updatedAt),
              }))}
            />
          </CollectionGate>
        </Panel>

        <Panel title="数据源健康" description="连接、权限与最新检查时间">
          <CollectionGate state={connectionState} message={runtime.connectionStatus?.message} empty={!runtime.connectionStatus?.value} emptyMessage="当前没有连接状态。">
            {runtime.connectionStatus?.value && (
              <dl className="wpe-kv-grid">
                <KeyValue label="连接名称" value={runtime.connectionStatus.value.displayName} />
                <KeyValue label="Provider" value={runtime.connectionStatus.value.providerId} mono />
                <KeyValue label="环境" value={<Badge tone="info">{runtime.connectionStatus.value.environment}</Badge>} />
                <KeyValue label="适配器" value={runtime.connectionStatus.value.adapterStatus} />
                <KeyValue label="交易权限" value={booleanLabel(runtime.connectionStatus.value.tradePermission, '已授予', '未授予')} />
                <KeyValue label="最后检查" value={formatUtc(runtime.connectionStatus.value.lastCheckedAtUtc)} title={runtime.connectionStatus.value.lastCheckedAtUtc} />
              </dl>
            )}
          </CollectionGate>
        </Panel>
      </div>

      <Panel title="最近审计事件" action={<button className="wpe-link-button" type="button" onClick={() => goTo('monitoring')}>打开监控</button>}>
        <CollectionGate state={auditState} message={runtime.auditEvents?.message} empty={!audits.length} emptyMessage="当前没有审计事件。">
          <DenseTable
            columns={[
              { key: 'time', label: '时间' },
              { key: 'category', label: '类别' },
              { key: 'source', label: '来源' },
              { key: 'status', label: '状态' },
              { key: 'summary', label: '摘要' },
            ]}
            rows={audits.slice(0, 8).map(item => ({
              __key: item.id,
              time: <span title={item.timeUtc}>{formatUtc(item.timeUtc)}</span>,
              category: item.category,
              source: item.source,
              status: <Badge>{item.status}</Badge>,
              summary: <span className="wpe-cell-wrap">{item.summary}</span>,
            }))}
          />
        </CollectionGate>
      </Panel>
    </div>
  )
}

function AgentsPage({ runtime }: { runtime: WpeRuntimeState }) {
  const [selectedRole, setSelectedRole] = useState<string>('Market')
  const operationsState = collectionState(runtime, 'agentOperations')
  const handoffState = collectionState(runtime, 'agentHandoffs')
  const operations = runtime.agentOperations ?? []
  const handoffs = runtime.agentHandoffs ?? []
  const operation = operations.find(item => item.roleId.toLowerCase() === selectedRole.toLowerCase())
  const incoming = [...handoffs].filter(item => item.targetRoleId.toLowerCase() === selectedRole.toLowerCase()).sort((a, b) => b.occurredAtUtc.localeCompare(a.occurredAtUtc))[0]
  const outgoing = [...handoffs].filter(item => item.sourceRoleId.toLowerCase() === selectedRole.toLowerCase()).sort((a, b) => b.occurredAtUtc.localeCompare(a.occurredAtUtc))[0]
  const timeline = [...handoffs].sort((a, b) => b.occurredAtUtc.localeCompare(a.occurredAtUtc))

  return (
    <div className="wpe-page-stack">
      <SectionTitle
        title="智能体协作中心"
        description="Market → Research → Strategy → Risk → Execution → Recovery → Audit"
        side={<Badge tone="neutral">金融教师不属于交易链</Badge>}
      />
      <Panel title="固定交易链路" description="状态、活动和交接完全来自运行快照">
        <CollectionGate state={operationsState} empty={!operations.length} emptyMessage="当前没有 Agent 运行记录。">
          <AgentChain runtime={runtime} onSelect={setSelectedRole} />
        </CollectionGate>
      </Panel>

      <div className="wpe-agents-layout">
        <Panel title="Agent 列表">
          <CollectionGate state={operationsState} empty={!operations.length} emptyMessage="当前没有 Agent 运行记录。">
            <div className="wpe-agent-list">
              {AGENT_CHAIN.map(role => {
                const item = operations.find(value => value.roleId.toLowerCase() === role.toLowerCase())
                const active = role === selectedRole
                return (
                  <button key={role} type="button" className={active ? 'is-active' : undefined} onClick={() => setSelectedRole(role)}>
                    <span className="wpe-agent-list__role"><strong>{AGENT_LABELS[role]}</strong><small>{role}</small></span>
                    <Badge tone={agentStatusTone(item?.status)}>{agentStatusLabel(item?.status)}</Badge>
                  </button>
                )
              })}
            </div>
          </CollectionGate>
        </Panel>

        <Panel title={AGENT_LABELS[selectedRole] ?? selectedRole} description="当前活动、模式和最近交接">
          <CollectionGate state={operationsState} empty={!operation} emptyMessage="该 Agent 当前没有运行记录。">
            {operation && (
              <>
                <div className="wpe-grid wpe-grid--3">
                  <Metric label="状态" value={<Badge tone={agentStatusTone(operation.status)}>{agentStatusLabel(operation.status)}</Badge>} />
                  <Metric label="运行模式" value={operation.mode} />
                  <Metric label="最后活动" value={formatUtc(operation.lastActivityAtUtc)} note={operation.lastActivityAtUtc ?? undefined} />
                </div>
                <div className="wpe-agent-activity">
                  <span>当前活动</span>
                  <p>{text(operation.activity, '宿主未提供当前活动描述。')}</p>
                </div>
                <div className="wpe-grid wpe-grid--2">
                  <div className="wpe-handoff-box">
                    <span>最近输入交接</span>
                    {incoming ? (
                      <><strong>{AGENT_LABELS[incoming.sourceRoleId] ?? incoming.sourceRoleId} → {AGENT_LABELS[incoming.targetRoleId] ?? incoming.targetRoleId}</strong><p>{incoming.result}</p><SourceTime value={incoming.occurredAtUtc} /></>
                    ) : <p className="wpe-muted">当前没有输入交接。</p>}
                  </div>
                  <div className="wpe-handoff-box">
                    <span>最近输出交接</span>
                    {outgoing ? (
                      <><strong>{AGENT_LABELS[outgoing.sourceRoleId] ?? outgoing.sourceRoleId} → {AGENT_LABELS[outgoing.targetRoleId] ?? outgoing.targetRoleId}</strong><p>{outgoing.result}</p><SourceTime value={outgoing.occurredAtUtc} /></>
                    ) : <p className="wpe-muted">当前没有输出交接。</p>}
                  </div>
                </div>
              </>
            )}
          </CollectionGate>
        </Panel>
      </div>

      <Panel title="交接时间线" description="不将 result 字段自动解释为成功或失败">
        <CollectionGate state={handoffState} empty={!timeline.length} emptyMessage="当前没有 Agent 交接记录。">
          <div className="wpe-timeline">
            {timeline.slice(0, 30).map(item => (
              <div className="wpe-timeline__item" key={item.id}>
                <div className="wpe-timeline__rail"><span /></div>
                <div className="wpe-timeline__content">
                  <div><strong>{AGENT_LABELS[item.sourceRoleId] ?? item.sourceRoleId} → {AGENT_LABELS[item.targetRoleId] ?? item.targetRoleId}</strong><SourceTime value={item.occurredAtUtc} /></div>
                  <p>{item.result}</p>
                </div>
              </div>
            ))}
          </div>
        </CollectionGate>
      </Panel>
    </div>
  )
}

function TeacherLessonView({ lesson }: { lesson: NonNullable<WpeRuntimeState['teacherLessons']>[number] }) {
  return (
    <div className="wpe-teacher-document">
      <div className="wpe-teacher-document__meta">
        <Badge tone="info">{lessonKindLabel(lesson.kind)}</Badge>
        <span>教学等级：{lesson.teachingLevel}</span>
        <span>语言：{lesson.language}</span>
        <span>时区：{lesson.timeZoneId}</span>
      </div>
      <dl className="wpe-kv-grid wpe-kv-grid--compact">
        <KeyValue label="计划时间" value={formatUtc(lesson.scheduledForUtc)} title={lesson.scheduledForUtc} />
        <KeyValue label="生成时间" value={formatUtc(lesson.generatedAtUtc)} title={lesson.generatedAtUtc} />
        <KeyValue label="人设版本" value={lesson.personaVersion} mono />
        <KeyValue label="执行权限" value={<Badge tone="neutral">无</Badge>} />
      </dl>
      <div className="wpe-teacher-blocks">
        {lesson.blocks.map(block => (
          <article className="wpe-teacher-block" key={block.blockId}>
            <div className="wpe-teacher-block__header">
              <div><Badge>{block.kind}</Badge><h3>{block.heading}</h3></div>
            </div>
            <div className="wpe-prose">{block.content}</div>
            {(block.evidenceHashes.length > 0 || block.sha256) && (
              <details>
                <summary>证据与内容哈希</summary>
                <div className="wpe-hash-list">
                  {block.evidenceHashes.map(hash => <HashValue key={hash} value={hash} />)}
                  <HashValue value={block.sha256} />
                </div>
              </details>
            )}
          </article>
        ))}
      </div>
      <div className="wpe-authority-notice"><StatusMark tone="info" /><span>本内容仅用于研究和教学，不构成订单、批准或交易指令。</span></div>
    </div>
  )
}

function TeacherPage({ runtime }: { runtime: WpeRuntimeState }) {
  const [tab, setTab] = useState('current')
  const lessonState = collectionState(runtime, 'teacherLessons')
  const recommendationState = collectionState(runtime, 'teacherRecommendations')
  const correctionState = collectionState(runtime, 'teacherCorrections')
  const outcomeState = collectionState(runtime, 'teacherOutcomes')
  const notificationState = collectionState(runtime, 'notificationStatus', runtime.notificationStatus?.state)
  const lessons = [...(runtime.teacherLessons ?? [])].sort((a, b) => b.generatedAtUtc.localeCompare(a.generatedAtUtc))
  const recommendations = [...(runtime.teacherRecommendations ?? [])].sort((a, b) => b.issuedAtUtc.localeCompare(a.issuedAtUtc))
  const corrections = [...(runtime.teacherCorrections ?? [])].sort((a, b) => b.issuedAtUtc.localeCompare(a.issuedAtUtc))
  const outcomes = [...(runtime.teacherOutcomes ?? [])].sort((a, b) => b.evaluatedAtUtc.localeCompare(a.evaluatedAtUtc))

  return (
    <div className="wpe-page-stack">
      <SectionTitle title="金融教师" description="证据驱动的市场讲解、研究候选与历史复盘" side={<Badge tone="neutral">executionAuthority = false</Badge>} />
      <div className="wpe-authority-banner"><div><strong>金融教师没有交易权限</strong><p>教师可以解释事实、提出条件性研究候选和修正旧课程，但不能下单、审批、修改策略或绕过风险链。</p></div></div>
      <Tabs value={tab} onChange={setTab} items={[
        { id: 'current', label: '最近课程' },
        { id: 'archive', label: '课程档案' },
        { id: 'recommendations', label: '研究候选' },
        { id: 'corrections', label: '修正记录' },
        { id: 'outcomes', label: '结果复盘' },
        { id: 'notifications', label: '通知状态' },
      ]} />

      {tab === 'current' && (
        <Panel title="最近生成课程" description="按 generatedAtUtc 排序，仅展示宿主返回的完整课程">
          <CollectionGate state={lessonState} empty={!lessons.length} emptyMessage="当前没有可用课程。">
            {lessons[0] && <TeacherLessonView lesson={lessons[0]} />}
          </CollectionGate>
        </Panel>
      )}

      {tab === 'archive' && (
        <Panel title="课程档案">
          <CollectionGate state={lessonState} empty={!lessons.length} emptyMessage="当前没有课程档案。">
            <div className="wpe-archive-list">
              {lessons.map(lesson => (
                <details key={lesson.lessonId} className="wpe-archive-item">
                  <summary><span><strong>{lessonKindLabel(lesson.kind)}</strong><small>{formatUtc(lesson.generatedAtUtc)} · {lesson.teachingLevel}</small></span><Badge tone="neutral">无执行权限</Badge></summary>
                  <TeacherLessonView lesson={lesson} />
                </details>
              ))}
            </div>
          </CollectionGate>
        </Panel>
      )}

      {tab === 'recommendations' && (
        <Panel title="条件性研究候选" description="候选不是订单、买入信号或已批准交易">
          <CollectionGate state={recommendationState} empty={!recommendations.length} emptyMessage="当前没有研究候选。">
            <div className="wpe-recommendation-list">
              {recommendations.map(item => (
                <article className="wpe-recommendation" key={`${item.recommendationId}-${item.version}`}>
                  <header>
                    <div><h3>{item.instrument}</h3><span>{item.assetClass} · {item.horizon}</span></div>
                    <Badge tone={item.state === 'avoid_high_risk' ? 'danger' : item.state === 'expired' ? 'neutral' : 'info'}>{recommendationLabel(item.state)}</Badge>
                  </header>
                  <p className="wpe-thesis">{item.thesis}</p>
                  <dl className="wpe-kv-grid wpe-kv-grid--compact">
                    <KeyValue label="发布" value={formatUtc(item.issuedAtUtc)} title={item.issuedAtUtc} />
                    <KeyValue label="到期" value={formatUtc(item.expiresAtUtc)} title={item.expiresAtUtc} />
                    <KeyValue label="版本" value={item.version} />
                    <KeyValue label="执行权限" value={<Badge tone="neutral">无</Badge>} />
                  </dl>
                  <div className="wpe-condition-grid">
                    <div><h4>确认条件</h4>{item.confirmationConditions.length ? <ol>{item.confirmationConditions.map(value => <li key={value}>{value}</li>)}</ol> : <p className="wpe-muted">未提供</p>}</div>
                    <div><h4>失效条件</h4>{item.invalidationConditions.length ? <ol>{item.invalidationConditions.map(value => <li key={value}>{value}</li>)}</ol> : <p className="wpe-muted">未提供</p>}</div>
                    <div><h4>主要风险</h4>{item.materialRisks.length ? <ol>{item.materialRisks.map(value => <li key={value}>{value}</li>)}</ol> : <p className="wpe-muted">未提供</p>}</div>
                  </div>
                  {item.evidenceHashes.length > 0 && <details><summary>证据哈希</summary><div className="wpe-hash-list">{item.evidenceHashes.map(hash => <HashValue key={hash} value={hash} />)}</div></details>}
                </article>
              ))}
            </div>
          </CollectionGate>
        </Panel>
      )}

      {tab === 'corrections' && (
        <Panel title="追加式修正记录" description="修正会关联被替代课程，不覆盖历史记录">
          <CollectionGate state={correctionState} empty={!corrections.length} emptyMessage="当前没有课程修正。">
            <div className="wpe-correction-list">
              {corrections.map(item => (
                <article className="wpe-correction" key={item.correctionId}>
                  <header><div><h3>修正 {item.correctionId}</h3><span>替代课程：{item.supersededLessonId}</span></div><Badge tone="warning">{item.reasonCode}</Badge></header>
                  <SourceTime value={item.issuedAtUtc} label="发布时间" />
                  <div className="wpe-teacher-blocks">
                    {item.replacementBlocks.map(block => <div className="wpe-teacher-block" key={block.blockId}><h4>{block.heading}</h4><div className="wpe-prose">{block.content}</div></div>)}
                  </div>
                  <HashValue value={item.sha256} />
                </article>
              ))}
            </div>
          </CollectionGate>
        </Panel>
      )}

      {tab === 'outcomes' && (
        <Panel title="结果复盘" description="相对基准的点时评价，不把幸运结果等同于正确流程">
          <CollectionGate state={outcomeState} empty={!outcomes.length} emptyMessage="当前没有结果复盘记录。">
            <DenseTable
              columns={[
                { key: 'instrument', label: '标的' },
                { key: 'benchmark', label: '基准' },
                { key: 'horizon', label: '周期' },
                { key: 'instrumentReturn', label: '标的收益（%）', align: 'right' },
                { key: 'relativeReturn', label: '相对收益（%）', align: 'right' },
                { key: 'mae', label: '最大不利波动（%）', align: 'right' },
                { key: 'state', label: '流程状态' },
                { key: 'time', label: '评价时间' },
              ]}
              rows={outcomes.map(item => ({
                __key: item.outcomeId,
                instrument: <strong>{item.instrument}</strong>,
                benchmark: item.benchmark,
                horizon: item.horizon,
                instrumentReturn: formatPercent(item.instrumentReturnPct),
                relativeReturn: <span className={item.relativeReturnPct > 0 ? 'wpe-positive' : item.relativeReturnPct < 0 ? 'wpe-negative' : undefined}>{formatPercent(item.relativeReturnPct)}</span>,
                mae: formatPercent(item.maximumAdverseExcursionPct),
                state: <Badge>{item.processState}</Badge>,
                time: <span title={item.evaluatedAtUtc}>{formatUtc(item.evaluatedAtUtc)}</span>,
              }))}
            />
          </CollectionGate>
        </Panel>
      )}

      {tab === 'notifications' && (
        <Panel title="教师通知状态" description="偏好只能在 WPF 安全设置窗口中修改">
          <CollectionGate state={notificationState} message={runtime.notificationStatus?.message} empty={!runtime.notificationStatus?.value} emptyMessage="当前没有通知状态。">
            {runtime.notificationStatus?.value && <NotificationStatusView runtime={runtime} />}
          </CollectionGate>
        </Panel>
      )}
    </div>
  )
}

function PositionsTable({ runtime }: { runtime: WpeRuntimeState }) {
  const state = collectionState(runtime, 'positions')
  const items = runtime.positions ?? []
  return (
    <CollectionGate state={state} message={runtime.collectionMessages?.positions} empty={!items.length} emptyMessage="当前没有开放持仓。">
      <DenseTable columns={[
        { key: 'symbol', label: '交易品种' },
        { key: 'side', label: '方向' },
        { key: 'quantity', label: '数量', align: 'right' },
        { key: 'entry', label: '入场价格', align: 'right' },
        { key: 'pnl', label: '未实现盈亏', align: 'right' },
      ]} rows={items.map(item => ({
        __key: `${item.symbol}-${item.side}`,
        symbol: <strong>{item.symbol}</strong>,
        side: item.side,
        quantity: formatNumber(item.quantity, 8),
        entry: formatNumber(item.entryPrice, 8),
        pnl: <span className={item.unrealizedPnl > 0 ? 'wpe-positive' : item.unrealizedPnl < 0 ? 'wpe-negative' : undefined}>{formatNumber(item.unrealizedPnl)}</span>,
      }))} />
    </CollectionGate>
  )
}

function OrdersTable({ runtime }: { runtime: WpeRuntimeState }) {
  const state = collectionState(runtime, 'orders')
  const items = runtime.orders ?? []
  return (
    <CollectionGate state={state} message={runtime.collectionMessages?.orders} empty={!items.length} emptyMessage="当前没有活跃订单。">
      <DenseTable columns={[
        { key: 'id', label: '订单编号' },
        { key: 'symbol', label: '品种' },
        { key: 'side', label: '方向' },
        { key: 'type', label: '类型' },
        { key: 'quantity', label: '数量', align: 'right' },
        { key: 'price', label: '价格', align: 'right' },
        { key: 'status', label: '状态' },
      ]} rows={items.map(item => ({
        __key: item.orderId,
        id: <HashValue value={item.orderId} />,
        symbol: <strong>{item.symbol}</strong>,
        side: item.side,
        type: item.type,
        quantity: formatNumber(item.quantity, 8),
        price: item.price === null ? <EmptyCell /> : formatNumber(item.price, 8),
        status: <Badge>{item.status}</Badge>,
      }))} />
    </CollectionGate>
  )
}

function AutomaticExecutionsTable({ runtime }: { runtime: WpeRuntimeState }) {
  const state = collectionState(runtime, 'automaticExecutions', runtime.automaticExecutions?.state)
  const items = runtime.automaticExecutions?.items ?? []
  return (
    <CollectionGate state={state} message={runtime.automaticExecutions?.message} empty={!items.length} emptyMessage="当前没有自动执行记录。策略与风险门仍会持续运行，只有合格信号才进入执行队列。">
      <DenseTable columns={[
        { key: 'id', label: '执行编号' },
        { key: 'status', label: '状态' },
        { key: 'code', label: '结果代码' },
        { key: 'attempts', label: '尝试次数', align: 'right' },
        { key: 'updated', label: '更新时间' },
      ]} rows={items.map(item => ({
        __key: item.executionId,
        id: <HashValue value={item.executionId} />,
        status: <Badge tone={item.status === 'Succeeded' ? 'success' : item.status.includes('Blocked') || item.status.includes('Failed') ? 'danger' : 'warning'}>{item.status}</Badge>,
        code: item.code,
        attempts: formatInteger(item.attemptCount),
        updated: <span title={item.updatedAtUtc}>{formatUtc(item.updatedAtUtc)}</span>,
      }))} />
    </CollectionGate>
  )
}

function HistoricalOrdersTable({ runtime }: { runtime: WpeRuntimeState }) {
  const collection = runtime.historicalOrders
  const state = collectionState(runtime, 'historicalOrders', collection?.state)
  const items = collection?.items ?? []
  return (
    <CollectionGate state={state} message={collection?.message} empty={!items.length} emptyMessage="当前没有历史订单记录。">
      <DenseTable columns={[
        { key: 'sequence', label: '序号', align: 'right' },
        { key: 'time', label: '发生时间' },
        { key: 'symbol', label: '品种' },
        { key: 'side', label: '方向' },
        { key: 'action', label: '动作' },
        { key: 'quantity', label: '数量', align: 'right' },
        { key: 'price', label: '均价', align: 'right' },
        { key: 'status', label: '状态' },
      ]} rows={items.map(item => ({
        __key: item.sequence,
        sequence: item.sequence,
        time: <span title={item.occurredAtUtc}>{formatUtc(item.occurredAtUtc)}</span>,
        symbol: <strong>{item.symbol}</strong>,
        side: item.side,
        action: item.action,
        quantity: formatNumber(item.quantity, 8),
        price: item.averagePrice === null ? <EmptyCell /> : formatNumber(item.averagePrice, 8),
        status: <Badge>{item.status}</Badge>,
      }))} />
    </CollectionGate>
  )
}

function TradingPage({ runtime }: { runtime: WpeRuntimeState }) {
  const [tab, setTab] = useState('positions')
  return (
    <div className="wpe-page-stack">
      <SectionTitle title="交易" description="只读投影：不能直接下单、撤单、批准或修改策略" side={<Badge tone="info">TESTNET ONLY</Badge>} />
      <div className="wpe-readonly-banner"><StatusMark tone="info" /><span>所有交易行为由宿主、风险门和执行链控制。本页面不发送交易指令。</span></div>
      <Tabs value={tab} onChange={setTab} items={[
        { id: 'positions', label: '持仓' },
        { id: 'orders', label: '活跃订单' },
        { id: 'execution', label: '自动执行' },
        { id: 'history', label: '历史订单' },
        { id: 'recovery', label: '恢复状态' },
      ]} />
      {tab === 'positions' && <Panel title="开放持仓"><PositionsTable runtime={runtime} /></Panel>}
      {tab === 'orders' && <Panel title="活跃订单"><OrdersTable runtime={runtime} /></Panel>}
      {tab === 'execution' && <Panel title="自动执行队列" description="真实展示策略信号通过风险门后进入执行链的结果，不从订单记录推断"><AutomaticExecutionsTable runtime={runtime} /></Panel>}
      {tab === 'history' && <Panel title="历史订单"><HistoricalOrdersTable runtime={runtime} /></Panel>}
      {tab === 'recovery' && (
        <Panel title="恢复与运行状态">
          <dl className="wpe-kv-grid">
            <KeyValue label="运行恢复状态" value={text(runtime.runtimeRecoveryStatus)} />
            <KeyValue label="授权模式" value={modeLabel(runtime.authorizationMode?.value?.mode)} />
          </dl>
          <div className="wpe-inline-note">恢复 Agent 只在执行状态不确定、订单超时或重启对账时采取动作；正常监控不等于停止运行。</div>
        </Panel>
      )}
    </div>
  )
}

function BacktestsTable({ runtime }: { runtime: WpeRuntimeState }) {
  const state = collectionState(runtime, 'backtests')
  const items = runtime.backtests ?? []
  return (
    <CollectionGate state={state} message={runtime.collectionMessages?.backtests} empty={!items.length} emptyMessage="当前没有回测结果。">
      <DenseTable columns={[
        { key: 'id', label: '回测 ID' },
        { key: 'strategy', label: '策略 / 版本' },
        { key: 'symbol', label: '品种' },
        { key: 'coverage', label: '覆盖天数', align: 'right' },
        { key: 'trades', label: '交易次数', align: 'right' },
        { key: 'return', label: '样本外收益（%）', align: 'right' },
        { key: 'drawdown', label: '最大回撤（%）', align: 'right' },
        { key: 'sharpe', label: 'Sharpe', align: 'right' },
        { key: 'status', label: '状态' },
        { key: 'time', label: '完成时间' },
      ]} rows={items.map(item => ({
        __key: item.backtestId,
        id: <HashValue value={item.backtestId} />,
        strategy: `${item.strategyId} / ${item.strategyVersion}`,
        symbol: <strong>{item.symbol}</strong>,
        coverage: formatInteger(item.coverageDays),
        trades: formatInteger(item.trades),
        return: formatPercent(item.outOfSampleReturn),
        drawdown: formatPercent(item.maxDrawdown),
        sharpe: formatNumber(item.sharpe),
        status: <Badge>{item.status}</Badge>,
        time: item.completedAtUtc ? <span title={item.completedAtUtc}>{formatUtc(item.completedAtUtc)}</span> : <EmptyCell />,
      }))} />
    </CollectionGate>
  )
}

function ResearchEvidence({ runtime }: { runtime: WpeRuntimeState }) {
  const collection = runtime.research
  const state = collectionState(runtime, 'crossAssetResearch', collection?.state)
  const items = collection?.items ?? []
  return (
    <CollectionGate state={state} message={collection?.message} empty={!items.length} emptyMessage="当前没有跨资产研究证据。">
      <div className="wpe-research-list">
        {items.map(item => (
          <article className="wpe-research-card" key={item.researchId}>
            <header>
              <div><h3>{item.instrumentId}</h3><span>{item.assetClass} · {item.strategyId}</span></div>
              <Badge tone={item.passed ? 'success' : 'warning'}>{item.passed ? '已通过' : '未通过'}</Badge>
            </header>
            <dl className="wpe-kv-grid wpe-kv-grid--compact">
              <KeyValue label="研究状态" value={item.status} />
              <KeyValue label="生命周期" value={lifecycleLabel(item.lifecycle)} />
              <KeyValue label="策略版本" value={item.strategyVersion} mono />
              <KeyValue label="评估时间" value={formatUtc(item.evaluatedAtUtc)} title={item.evaluatedAtUtc} />
            </dl>
            {item.metrics ? (
              <div className="wpe-grid wpe-grid--4">
                <Metric label="样本数" value={formatInteger(item.metrics.observations)} />
                <Metric label="交易次数" value={formatInteger(item.metrics.trades)} />
                <Metric label="样本外收益（%）" value={formatPercent(item.metrics.outOfSampleReturn)} />
                <Metric label="最大回撤（%）" value={formatPercent(item.metrics.maximumDrawdown)} />
                <Metric label="Sharpe" value={formatNumber(item.metrics.sharpe)} />
                <Metric label="Walk-forward" value={formatNumber(item.metrics.walkForwardScore)} />
                <Metric label="Monte Carlo 亏损概率（%）" value={formatPercent(item.metrics.monteCarloLossProbability)} />
                <Metric label="总收益（%）" value={formatPercent(item.metrics.totalReturn)} />
              </div>
            ) : <div className="wpe-inline-note">当前研究未提供可展示的指标，不显示零值。</div>}
            <div className="wpe-grid wpe-grid--2">
              <div className="wpe-detail-box"><h4>时间对齐</h4><dl className="wpe-kv-grid wpe-kv-grid--compact"><KeyValue label="是否要求" value={booleanLabel(item.alignment.required)} /><KeyValue label="场所数量" value={formatInteger(item.alignment.venueCount)} /><KeyValue label="对齐点数" value={formatInteger(item.alignment.alignmentPointCount)} /><KeyValue label="允许前向填充" value={booleanLabel(item.alignment.forwardFillAllowed)} /></dl></div>
              <div className="wpe-detail-box"><h4>多重检验</h4>{item.multipleTesting ? <dl className="wpe-kv-grid wpe-kv-grid--compact"><KeyValue label="试验次数" value={formatInteger(item.multipleTesting.trialCount)} /><KeyValue label="修正方法" value={item.multipleTesting.correctionMethod} /><KeyValue label="名义 Alpha" value={formatNumber(item.multipleTesting.nominalAlpha, 6)} /><KeyValue label="保留集未触碰" value={booleanLabel(item.multipleTesting.holdoutUntouched)} /></dl> : <p className="wpe-muted">未提供</p>}</div>
            </div>
            {item.reasonCodes.length > 0 && <div className="wpe-reason-list"><h4>原因代码</h4>{item.reasonCodes.map(reason => <Badge key={reason}>{reason}</Badge>)}</div>}
            <details><summary>证据与版本哈希</summary><div className="wpe-hash-list"><HashValue value={item.artifactHash} /><HashValue value={item.datasetHash} /><HashValue value={item.parameterHash} /></div></details>
          </article>
        ))}
      </div>
    </CollectionGate>
  )
}

function ResearchPage({ runtime }: { runtime: WpeRuntimeState }) {
  const [tab, setTab] = useState('backtests')
  return (
    <div className="wpe-page-stack">
      <SectionTitle title="研究与验证" description="回测和可审计研究证据；这些结果不拥有自动交易决策权" />
      <Tabs value={tab} onChange={setTab} items={[
        { id: 'backtests', label: '回测' },
        { id: 'research', label: '跨资产研究' },
      ]} />
      {tab === 'backtests' && <Panel title="回测结果"><BacktestsTable runtime={runtime} /></Panel>}
      {tab === 'research' && <Panel title="跨资产研究证据"><ResearchEvidence runtime={runtime} /></Panel>}
    </div>
  )
}
function RiskPage({ runtime }: { runtime: WpeRuntimeState }) {
  const state = collectionState(runtime, 'risk')
  const circuitBreaker = rawBoolean(runtime, 'circuitBreakerActive')
  const var99 = rawNumber(runtime, 'portfolioVaR99')
  const cvar99 = rawNumber(runtime, 'portfolioCVaR99')
  const concentration = rawNumber(runtime, 'portfolioConcentration')
  const correlation = rawNumber(runtime, 'portfolioCorrelation')
  return (
    <div className="wpe-page-stack">
      <SectionTitle title="风险" description="风险门、熔断器、暴露和授权模式" />
      {circuitBreaker === true && <div className="wpe-risk-banner" role="alert"><StatusMark tone="danger" /><div><strong>熔断器已触发</strong><p>当前风险状态不允许被前端绕过。所有启动与交易动作仍由宿主再次验证。</p></div></div>}
      <CollectionGate state={state} empty={false}>
        <div className="wpe-grid wpe-grid--4">
          <Metric label="风险准备" value={<Badge tone={runtime.riskReady ? 'success' : 'warning'}>{runtime.riskReady ? '已准备' : '未准备'}</Badge>} />
          <Metric label="熔断器" value={circuitBreaker === undefined ? '未提供' : <Badge tone={circuitBreaker ? 'danger' : 'success'}>{circuitBreaker ? '已触发' : '正常'}</Badge>} />
          <Metric label="风险负载（%）" value={formatPercent(runtime.riskLoad)} tone={isFiniteNumber(runtime.riskLoad) && runtime.riskLoad >= 80 ? 'danger' : isFiniteNumber(runtime.riskLoad) && runtime.riskLoad >= 60 ? 'warning' : 'neutral'} />
          <Metric label="授权模式" value={modeLabel(runtime.authorizationMode?.value?.mode)} />
        </div>
        <div className="wpe-grid wpe-grid--4">
          <Metric label="当日盈亏（账户计价）" value={formatNumber(runtime.dailyPnl)} tone={isFiniteNumber(runtime.dailyPnl) ? runtime.dailyPnl > 0 ? 'success' : runtime.dailyPnl < 0 ? 'danger' : 'neutral' : 'neutral'} />
          <Metric label="最大回撤（%）" value={formatPercent(runtime.maxDrawdown)} />
          <Metric label="VaR 99%" value={var99 === undefined ? '未提供' : formatNumber(var99)} />
          <Metric label="CVaR 99%" value={cvar99 === undefined ? '未提供' : formatNumber(cvar99)} />
          <Metric label="组合集中度" value={concentration === undefined ? '未提供' : formatNumber(concentration)} />
          <Metric label="组合相关性" value={correlation === undefined ? '未提供' : formatNumber(correlation)} />
          <Metric label="最近执行状态" value={text(runtime.automaticExecutions?.items?.[0]?.status)} />
          <Metric label="最近结果代码" value={text(runtime.automaticExecutions?.items?.[0]?.code)} />
        </div>
        <Panel title="风险结论" className="wpe-panel--nested">
          <div className="wpe-risk-summary">{text(runtime.riskSummary, '宿主未提供风险结论。')}</div>
        </Panel>
        <Panel title="自动执行记录" description="只读展示风险门后的真实执行结果" className="wpe-panel--nested"><AutomaticExecutionsTable runtime={runtime} /></Panel>
      </CollectionGate>
    </div>
  )
}

function NotificationStatusView({ runtime }: { runtime: WpeRuntimeState }) {
  const value = runtime.notificationStatus?.value
  if (!value) return null
  return (
    <>
      <div className="wpe-grid wpe-grid--4">
        <Metric label="通知总开关" value={<Badge tone={value.enabled ? 'success' : 'neutral'}>{value.enabled ? '已启用' : '已关闭'}</Badge>} />
        <Metric label="Telegram" value={<Badge tone={value.telegramReady ? 'success' : 'warning'}>{value.telegramReady ? '已就绪' : value.telegramStored ? '已保存未就绪' : '未配置'}</Badge>} />
        <Metric label="WhatsApp" value={<Badge tone={value.whatsAppReady ? 'success' : 'warning'}>{value.whatsAppReady ? '已就绪' : value.whatsAppStored ? '已保存未就绪' : '未配置'}</Badge>} />
        <Metric label="安静时间" value={value.quietHoursEnabled ? `${value.quietHoursStart}–${value.quietHoursEnd}` : '未启用'} note={value.quietHoursEnabled ? value.quietHoursTimeZone : undefined} />
      </div>
      <div className="wpe-grid wpe-grid--4">
        <Metric label="待发送" value={formatInteger(value.pendingCount)} />
        <Metric label="重试中" value={formatInteger(value.retryingCount)} tone={value.retryingCount > 0 ? 'warning' : 'neutral'} />
        <Metric label="已发送" value={formatInteger(value.sentCount)} />
        <Metric label="死信" value={formatInteger(value.deadLetterCount)} tone={value.deadLetterCount > 0 ? 'danger' : 'neutral'} />
      </div>
      {value.eventKinds.length > 0 && <div className="wpe-reason-list"><h4>通知事件类型</h4>{value.eventKinds.map(kind => <Badge key={kind}>{kind}</Badge>)}</div>}
    </>
  )
}

function NotificationOutbox({ runtime }: { runtime: WpeRuntimeState }) {
  const state = collectionState(runtime, 'notificationOutbox')
  const items = runtime.notificationOutbox ?? []
  return (
    <CollectionGate state={state} empty={!items.length} emptyMessage="当前通知发件箱为空。">
      <DenseTable columns={[
        { key: 'id', label: 'ID', align: 'right' },
        { key: 'channel', label: '渠道' },
        { key: 'kind', label: '类型' },
        { key: 'state', label: '状态' },
        { key: 'attempts', label: '尝试次数', align: 'right' },
        { key: 'next', label: '下次尝试' },
        { key: 'updated', label: '更新时间' },
        { key: 'diagnostic', label: '诊断代码' },
      ]} rows={items.map(item => ({
        __key: item.id,
        id: item.id,
        channel: item.channel,
        kind: item.kind,
        state: <Badge tone={item.uiState === 'DeadLetter' ? 'danger' : item.uiState === 'Retrying' ? 'warning' : item.uiState === 'Sent' ? 'success' : 'neutral'}>{item.uiState}</Badge>,
        attempts: `${item.attempts}/${item.maxAttempts}`,
        next: <span title={item.nextAttemptAtUtc}>{formatUtc(item.nextAttemptAtUtc)}</span>,
        updated: <span title={item.updatedAtUtc}>{formatUtc(item.updatedAtUtc)}</span>,
        diagnostic: item.diagnosticCode ?? <EmptyCell />,
      }))} />
    </CollectionGate>
  )
}

function RuntimeMonitoring({ runtime }: { runtime: WpeRuntimeState }) {
  const fresh = Boolean(runtime.runtimeFresh ?? runtime.freshness?.fresh)
  return (
    <>
      <div className="wpe-grid wpe-grid--4">
        <Metric label="宿主连接" value={<Badge tone={runtime.hostConnected ? 'success' : 'danger'}>{runtime.hostConnected ? '已连接' : '未连接'}</Badge>} />
        <Metric label="快照新鲜度" value={<Badge tone={fresh ? 'success' : 'warning'}>{fresh ? '新鲜' : '不可用'}</Badge>} note={relativeFreshness(runtime.runtimeAgeSeconds ?? runtime.freshness?.ageSeconds)} />
        <Metric label="契约版本" value={text(runtime.contractVersion)} />
        <Metric label="环境" value={<Badge tone="info">{text(runtime.environment)}</Badge>} />
      </div>
      <dl className="wpe-kv-grid">
        <KeyValue label="Provider" value={text(runtime.runtimeProviderId)} mono />
        <KeyValue label="快照时间" value={formatUtc(runtime.snapshotTimestampUtc)} title={runtime.snapshotTimestampUtc} />
        <KeyValue label="数据源时间" value={formatUtc(runtime.sourceTimestampUtc)} title={runtime.sourceTimestampUtc} />
        <KeyValue label="心跳时间" value={formatUtc(runtime.runtimeHeartbeatAtUtc)} title={runtime.runtimeHeartbeatAtUtc} />
        <KeyValue label="快照年龄（秒）" value={formatNumber(runtime.runtimeAgeSeconds ?? runtime.freshness?.ageSeconds)} />
        <KeyValue label="过期阈值（秒）" value={formatNumber(runtime.freshness?.staleAfterSeconds)} />
        <KeyValue label="启动允许" value={booleanLabel(runtime.agentStartAllowed, '允许', '不允许')} />
        <KeyValue label="停止允许" value={booleanLabel(runtime.agentStopAllowed, '允许', '不允许')} />
      </dl>
      {runtime.runtimeDiagnosticReason && <div className="wpe-diagnostic"><strong>运行诊断</strong><p>{runtime.runtimeDiagnosticReason}</p></div>}
    </>
  )
}

function ConnectionMonitoring({ runtime }: { runtime: WpeRuntimeState }) {
  const state = collectionState(runtime, 'connectionStatus', runtime.connectionStatus?.state)
  const value = runtime.connectionStatus?.value
  return (
    <CollectionGate state={state} message={runtime.connectionStatus?.message} empty={!value} emptyMessage="当前没有连接状态。">
      {value && (
        <>
          <div className="wpe-grid wpe-grid--4">
            <Metric label="连接就绪" value={<Badge tone={value.ready ? 'success' : 'warning'}>{value.ready ? '已就绪' : '未就绪'}</Badge>} />
            <Metric label="交易所连接" value={<Badge tone={value.exchangeConnected ? 'success' : 'warning'}>{value.exchangeConnected ? '已连接' : '未连接'}</Badge>} />
            <Metric label="交易权限" value={<Badge tone={value.tradePermission ? 'success' : 'warning'}>{booleanLabel(value.tradePermission, '已授予', '未授予')}</Badge>} />
            <Metric label="凭据状态" value={value.credentialStatus} />
          </div>
          <dl className="wpe-kv-grid">
            <KeyValue label="连接 ID" value={value.connectionId} mono />
            <KeyValue label="名称" value={value.displayName} />
            <KeyValue label="Provider" value={value.providerId} mono />
            <KeyValue label="环境" value={<Badge tone="info">{value.environment}</Badge>} />
            <KeyValue label="Adapter" value={value.adapterStatus} />
            <KeyValue label="提现权限" value={booleanLabel(value.withdrawPermission, '已授予', '未授予')} />
            <KeyValue label="最后检查" value={formatUtc(value.lastCheckedAtUtc)} title={value.lastCheckedAtUtc} />
          </dl>
          {value.withdrawalWarning && <div className="wpe-risk-banner"><StatusMark tone="warning" /><div><strong>权限提醒</strong><p>{value.withdrawalWarning}</p></div></div>}
        </>
      )}
    </CollectionGate>
  )
}

function AiMonitoring({ runtime }: { runtime: WpeRuntimeState }) {
  const collection = runtime.llmGovernance
  const state = collectionState(runtime, 'llmGovernance', collection?.state)
  const value = collection?.value
  return (
    <CollectionGate state={state} message={collection?.message} empty={!value} emptyMessage="当前没有 AI 使用统计。">
      {value && (
        <>
          <div className="wpe-authority-notice"><StatusMark tone="info" /><span>AI 仅参与研究和表达辅助，不拥有交易执行权限。</span></div>
          <div className="wpe-grid wpe-grid--4">
            <Metric label="运行模式" value={value.mode} />
            <Metric label="允许远程模型" value={booleanLabel(value.remoteAllowed)} />
            <Metric label="调用次数" value={formatInteger(value.calls)} />
            <Metric label="Token" value={formatInteger(value.tokens)} />
            <Metric label="成本（USD）" value={formatNumber(value.costUsd, 4)} />
            <Metric label="缓存命中" value={formatInteger(value.cacheHits)} />
            <Metric label="预算阻断" value={formatInteger(value.budgetBlocks)} tone={value.budgetBlocks > 0 ? 'warning' : 'neutral'} />
            <Metric label="隐私阻断" value={formatInteger(value.privacyBlocks)} tone={value.privacyBlocks > 0 ? 'warning' : 'neutral'} />
          </div>
          <dl className="wpe-kv-grid">
            <KeyValue label="主要 Provider" value={text(value.topProvider)} />
            <KeyValue label="主要用途" value={text(value.topPurpose)} />
            <KeyValue label="主要 Agent" value={text(value.topAgent)} />
            <KeyValue label="主要工具" value={text(value.topTool)} />
            <KeyValue label="离线完成" value={formatInteger(value.offlineCompletions)} />
            <KeyValue label="回退次数" value={formatInteger(value.fallbacks)} />
            <KeyValue label="最后调用" value={formatUtc(value.lastCallAtUtc)} title={value.lastCallAtUtc} />
          </dl>
        </>
      )}
    </CollectionGate>
  )
}

function SkillsMonitoring({ runtime }: { runtime: WpeRuntimeState }) {
  const state = collectionState(runtime, 'skillCalls')
  const items = runtime.skillCalls ?? []
  return (
    <CollectionGate state={state} empty={!items.length} emptyMessage="当前没有技能调用记录。">
      <DenseTable columns={[
        { key: 'time', label: '时间' },
        { key: 'skill', label: '技能' },
        { key: 'status', label: '状态' },
        { key: 'duration', label: '耗时（ms）', align: 'right' },
        { key: 'mode', label: '模式' },
        { key: 'remote', label: '远程模型' },
        { key: 'tokens', label: 'Token', align: 'right' },
        { key: 'cost', label: '成本（USD）', align: 'right' },
      ]} rows={items.map(item => ({
        __key: item.id,
        time: <span title={item.occurredAtUtc}>{formatUtc(item.occurredAtUtc)}</span>,
        skill: <strong>{item.skill}</strong>,
        status: <Badge>{item.status}</Badge>,
        duration: formatInteger(item.durationMs),
        mode: item.mode ?? <EmptyCell />,
        remote: item.remoteLlmUsed === null ? <EmptyCell /> : booleanLabel(item.remoteLlmUsed),
        tokens: item.tokens === null ? <EmptyCell /> : formatInteger(item.tokens),
        cost: item.costUsd === null ? <EmptyCell /> : formatNumber(item.costUsd, 4),
      }))} />
    </CollectionGate>
  )
}

function MemoryMonitoring({ runtime }: { runtime: WpeRuntimeState }) {
  const state = collectionState(runtime, 'memoryStatus', runtime.memoryStatus?.state)
  const value = runtime.memoryStatus?.value
  const retrievalState = collectionState(runtime, 'recentMemoryRetrievals')
  const retrievals = runtime.recentMemoryRetrievals ?? []
  return (
    <div className="wpe-page-stack wpe-page-stack--tight">
      <CollectionGate state={state} message={runtime.memoryStatus?.message} empty={!value} emptyMessage="当前没有内存状态。">
        {value && <div className="wpe-grid wpe-grid--4"><Metric label="工作记忆" value={formatInteger(value.workingCount)} /><Metric label="情景记忆" value={formatInteger(value.episodicCount)} /><Metric label="长期记忆" value={formatInteger(value.longTermCount)} /><Metric label="最后检索" value={formatUtc(value.lastRetrievedAtUtc)} /></div>}
      </CollectionGate>
      <CollectionGate state={retrievalState} empty={!retrievals.length} emptyMessage="当前没有最近记忆检索。">
        <DenseTable columns={[
          { key: 'time', label: '时间' },
          { key: 'tier', label: '层级' },
          { key: 'symbol', label: '品种' },
          { key: 'strategy', label: '策略' },
          { key: 'source', label: '来源' },
          { key: 'result', label: '结果' },
          { key: 'count', label: '数量', align: 'right' },
        ]} rows={retrievals.map(item => ({
          __key: item.id,
          time: <span title={item.occurredAtUtc}>{formatUtc(item.occurredAtUtc)}</span>,
          tier: item.tier,
          symbol: item.symbol ?? <EmptyCell />,
          strategy: item.strategyId ?? <EmptyCell />,
          source: item.source,
          result: <span className="wpe-cell-wrap">{item.result}</span>,
          count: formatInteger(item.resultCount),
        }))} />
      </CollectionGate>
    </div>
  )
}

function AuditMonitoring({ runtime }: { runtime: WpeRuntimeState }) {
  const collection = runtime.auditEvents
  const state = collectionState(runtime, 'auditEvents', collection?.state)
  const items = collection?.items ?? []
  return (
    <CollectionGate state={state} message={collection?.message} empty={!items.length} emptyMessage="当前没有审计事件。">
      <DenseTable columns={[
        { key: 'time', label: '时间' },
        { key: 'category', label: '类别' },
        { key: 'source', label: '来源' },
        { key: 'correlation', label: '关联 ID' },
        { key: 'status', label: '状态' },
        { key: 'summary', label: '摘要' },
      ]} rows={items.map(item => ({
        __key: item.id,
        time: <span title={item.timeUtc}>{formatUtc(item.timeUtc)}</span>,
        category: item.category,
        source: item.source,
        correlation: item.correlationId ? <HashValue value={item.correlationId} /> : <EmptyCell />,
        status: <Badge>{item.status}</Badge>,
        summary: <span className="wpe-cell-wrap">{item.summary}</span>,
      }))} />
    </CollectionGate>
  )
}

function PluginsMonitoring({ runtime }: { runtime: WpeRuntimeState }) {
  const collection = runtime.plugins
  const state = collectionState(runtime, 'plugins', collection?.state)
  const items = collection?.items ?? []
  return (
    <CollectionGate state={state} message={collection?.message} empty={!items.length} emptyMessage="当前没有插件记录。">
      <DenseTable columns={[
        { key: 'name', label: '名称' },
        { key: 'type', label: '类型' },
        { key: 'version', label: '版本' },
        { key: 'publisher', label: '发布方' },
        { key: 'enabled', label: '运行状态' },
        { key: 'testnet', label: '仅 Testnet' },
        { key: 'compatibility', label: '兼容性' },
        { key: 'risk', label: '风险等级' },
        { key: 'message', label: '状态说明' },
      ]} rows={items.map(item => ({
        __key: item.id,
        name: <strong>{item.name}</strong>,
        type: item.type,
        version: item.version,
        publisher: item.publisherName,
        enabled: <Badge tone={item.active ? 'success' : item.runtimeStatus === 'stale' || item.runtimeStatus === 'unavailable' ? 'warning' : 'neutral'}>{item.runtimeStatus === 'running' ? '运行中' : item.runtimeStatus === 'stale' ? '状态过期' : item.runtimeStatus === 'unavailable' ? '连接不可用' : '未配置'}</Badge>,
        testnet: booleanLabel(item.testnetOnly),
        compatibility: <Badge tone={item.compatibilityStatus === 'compatible' ? 'success' : 'danger'}>{item.compatibilityStatus}</Badge>,
        risk: item.riskLevel,
        message: item.active ? '当前 Testnet 连接正在使用此适配器' : item.statusMessage ?? (item.runtimeStatus === 'not-configured' ? '尚未配置为当前交易连接' : <EmptyCell />),
      }))} />
    </CollectionGate>
  )
}

function MonitoringPage({ runtime }: { runtime: WpeRuntimeState }) {
  const [tab, setTab] = useState('runtime')
  return (
    <div className="wpe-page-stack">
      <SectionTitle title="监控" description="运行时、连接、通知、AI 使用、内存和审计" />
      <Tabs value={tab} onChange={setTab} items={[
        { id: 'runtime', label: '运行状态' },
        { id: 'connection', label: '数据连接' },
        { id: 'notifications', label: '通知' },
        { id: 'ai', label: 'AI 使用' },
        { id: 'skills', label: '技能调用' },
        { id: 'memory', label: '内存' },
        { id: 'audit', label: '审计事件' },
        { id: 'plugins', label: '插件与能力' },
      ]} />
      {tab === 'runtime' && <Panel title="宿主与运行快照"><RuntimeMonitoring runtime={runtime} /></Panel>}
      {tab === 'connection' && <Panel title="Provider 连接状态"><ConnectionMonitoring runtime={runtime} /></Panel>}
      {tab === 'notifications' && (
        <div className="wpe-page-stack wpe-page-stack--tight">
          <Panel title="通知准备状态"><CollectionGate state={collectionState(runtime, 'notificationStatus', runtime.notificationStatus?.state)} message={runtime.notificationStatus?.message} empty={!runtime.notificationStatus?.value} emptyMessage="当前没有通知状态。"><NotificationStatusView runtime={runtime} /></CollectionGate></Panel>
          <Panel title="通知发件箱"><NotificationOutbox runtime={runtime} /></Panel>
        </div>
      )}
      {tab === 'ai' && <Panel title="AI 使用与治理"><AiMonitoring runtime={runtime} /></Panel>}
      {tab === 'skills' && <Panel title="技能调用"><SkillsMonitoring runtime={runtime} /></Panel>}
      {tab === 'memory' && <Panel title="本地记忆状态"><MemoryMonitoring runtime={runtime} /></Panel>}
      {tab === 'audit' && <Panel title="审计事件"><AuditMonitoring runtime={runtime} /></Panel>}
      {tab === 'plugins' && <Panel title="插件与能力"><PluginsMonitoring runtime={runtime} /></Panel>}
    </div>
  )
}

function SettingsPage({ runtime }: { runtime: WpeRuntimeState }) {
  const [telegramView, setTelegramView] = useState<'direct' | 'subscribers'>('direct')
  const collection = runtime.securityStorage
  const state = collectionState(runtime, 'securityStorage', collection?.state)
  const value = collection?.value
  const connection = runtime.connectionStatus?.value
  const notification = runtime.notificationStatus?.value
  const telegramSubscribers = runtime.telegramSubscribers ?? []
  const subscriberCounts = {
    pending: telegramSubscribers.filter(x => x.state === 'Pending').length,
    approved: telegramSubscribers.filter(x => x.state === 'Approved').length,
    disabled: telegramSubscribers.filter(x => x.state === 'Disabled').length,
  }
  return (
    <div className="wpe-page-stack">
      <SectionTitle title="设置" description="密钥和敏感配置由 WPF 安全设置窗口管理" />
      <Panel title="交易所与运行环境" description="同一交易所可分别配置测试网和实盘；凭据、权限、持仓与订单状态互不混用">
        <div className="wpe-environment-card">
          <div>
            <span className="wpe-environment-card__label">当前生效环境</span>
            <EnvironmentControl runtime={runtime} />
          </div>
          <div className="wpe-environment-card__explain">
            <strong>{isMainnetEnvironment(runtime) ? '实盘模式' : '测试网模式'}</strong>
            <p>{isMainnetEnvironment(runtime) ? '真实资金环境。启动前仍需凭据、交易权限、风控策略与二次确认全部通过。' : '用于连接验证、策略联调和模拟交易。测试通过后可在安全设置中配置并切换实盘。'}</p>
          </div>
          <button type="button" className="wpe-button wpe-button--primary" onClick={() => postHostCommand('open-settings')}>管理交易所连接</button>
        </div>
      </Panel>
      <div className="wpe-settings-layout">
        <Panel title="安全存储" description="React 只读取非敏感状态，不保存密钥">
          <CollectionGate state={state} message={collection?.message} empty={!value} emptyMessage="当前没有安全存储状态。">
            {value && (
              <>
                <div className="wpe-grid wpe-grid--3">
                  <Metric label="状态" value={<Badge tone={value.state === 'Failed' ? 'danger' : value.state === 'Ready' || value.state === 'Committed' ? 'success' : 'neutral'}>{value.state}</Badge>} />
                  <Metric label="记录数量" value={formatInteger(value.recordCount)} />
                  <Metric label="Envelope 版本" value={formatInteger(value.envelopeVersion)} />
                </div>
                <dl className="wpe-kv-grid">
                  <KeyValue label="原因代码" value={value.reasonCode} />
                  <KeyValue label="证据哈希" value={value.evidenceSha256 ? <HashValue value={value.evidenceSha256} /> : <EmptyCell />} />
                </dl>
              </>
            )}
          </CollectionGate>
          <div className="wpe-settings-action">
            <div><strong>打开宿主安全设置</strong><p>凭据、通知偏好和提供方配置不会进入 React 状态。</p></div>
            <button type="button" className="wpe-button wpe-button--primary" onClick={() => postHostCommand('open-settings')}>打开安全设置窗口</button>
          </div>
        </Panel>

        <Panel title="当前连接摘要">
          <dl className="wpe-kv-grid">
            <KeyValue label="Provider" value={text(connection?.providerId)} mono />
            <KeyValue label="环境" value={connection?.environment ? <Badge tone="info">{connection.environment}</Badge> : '未提供'} />
            <KeyValue label="连接就绪" value={booleanLabel(connection?.ready)} />
            <KeyValue label="交易权限" value={booleanLabel(connection?.tradePermission, '已授予', '未授予')} />
            <KeyValue label="远程 AI" value={booleanLabel(runtime.llmGovernance?.value?.remoteAllowed, '允许', '不允许')} />
            <KeyValue label="本地运行模式" value={text(runtime.aiRuntimeEffectiveMode ?? runtime.aiRuntimeMode)} />
          </dl>
        </Panel>
      </div>
      <Panel title="Telegram 通知" description="两版通知方案共用同一个本地加密 Bot；订阅分发版需要新增授权登记后端，当前用于确认交互方案">
        <div className="wpe-telegram-switch" role="tablist" aria-label="Telegram 通知方案">
          <button type="button" role="tab" aria-selected={telegramView === 'direct'} className={telegramView === 'direct' ? 'is-active' : ''} onClick={() => setTelegramView('direct')}>版本 A · 直达通知</button>
          <button type="button" role="tab" aria-selected={telegramView === 'subscribers'} className={telegramView === 'subscribers' ? 'is-active' : ''} onClick={() => setTelegramView('subscribers')}>版本 B · 订阅分发</button>
        </div>
        {telegramView === 'direct' ? <div className="wpe-notification-quick">
          <div className="wpe-notification-quick__identity">
            <span className="wpe-notification-quick__mark">TG</span>
            <div>
              <strong>{notification?.telegramReady ? 'Telegram 已就绪' : notification?.telegramStored ? 'Telegram 配置待验证' : '尚未配置 Telegram'}</strong>
              <p>{notification?.telegramReady ? '已具备发送条件，可在安全设置中测试发送或调整事件类型。' : '打开安全设置，填写 Bot Token 和接收目标后进行测试发送。'}</p>
            </div>
          </div>
          <div className="wpe-notification-quick__stats">
            <span><small>待发送</small><strong>{formatInteger(notification?.pendingCount)}</strong></span>
            <span><small>重试中</small><strong>{formatInteger(notification?.retryingCount)}</strong></span>
            <span><small>发送失败</small><strong>{formatInteger(notification?.deadLetterCount)}</strong></span>
          </div>
          <button type="button" className="wpe-button wpe-button--primary" onClick={() => postHostCommand('open-notification-settings')}>{notification?.telegramStored ? '管理 Telegram' : '配置 Telegram'}</button>
        </div> : <div className="wpe-subscriber-design">
          <div className="wpe-subscriber-design__intro">
            <span className="wpe-notification-quick__mark">TG</span>
            <div><strong>Bot 订阅者管理</strong><p>用户向 Bot 发送 /start 后进入待批准列表；只有本机明确批准的人才能收到所选通知，取消授权立即停止发送。</p></div>
            <Badge tone={collectionState(runtime, 'telegramSubscribers') === 'available' ? 'success' : 'warning'}>{collectionState(runtime, 'telegramSubscribers') === 'available' ? '本地注册表已接通' : '注册表不可用'}</Badge>
          </div>
          <div className="wpe-grid wpe-grid--3">
            <Metric label="待批准" value={formatInteger(subscriberCounts.pending)} />
            <Metric label="已授权" value={formatInteger(subscriberCounts.approved)} />
            <Metric label="已停用" value={formatInteger(subscriberCounts.disabled)} />
          </div>
          <div className="wpe-subscriber-flow" aria-label="订阅授权流程">
            <span><b>01</b> 关注 Bot / 发送 start</span><span><b>02</b> 本机核对并批准</span><span><b>03</b> 选择可接收事件</span><span><b>04</b> 加密保存并审计</span>
          </div>
          <CollectionGate state={collectionState(runtime, 'telegramSubscribers')} empty={!telegramSubscribers.length} emptyMessage="当前没有订阅者。用户向 Bot 发送 /start 后会自动出现在这里。">
            <DenseTable columns={[
              { key: 'id', label: '订阅者' },
              { key: 'type', label: '会话类型' },
              { key: 'status', label: '状态' },
              { key: 'events', label: '事件范围' },
              { key: 'updated', label: '更新时间' },
            ]} rows={telegramSubscribers.map(item => ({
              __key: item.subscriberId,
              id: <HashValue value={item.subscriberId} />,
              type: item.chatType,
              status: <Badge tone={item.state === 'Approved' ? 'success' : item.state === 'Pending' ? 'warning' : 'neutral'}>{item.state === 'Approved' ? '已授权' : item.state === 'Pending' ? '待批准' : '已停用'}</Badge>,
              events: item.eventKinds.length ? item.eventKinds.join('、') : '未授权事件',
              updated: <span title={item.updatedAtUtc}>{formatUtc(item.updatedAtUtc)}</span>,
            }))} />
          </CollectionGate>
          <div className="wpe-settings-action">
            <div><strong>安全规则</strong><p>“关注 Bot”只产生待批准申请，不自动获得交易、账户或风险通知；Bot Token 和完整 Chat ID 永不进入 Web UI。</p></div>
            <button type="button" className="wpe-button" onClick={() => postHostCommand('open-notification-settings')}>打开本机通知管理</button>
          </div>
        </div>}
      </Panel>
    </div>
  )
}

export function WpeConsole() {
  const runtime = useWpeRuntime()
  const [page, setPage] = useState<PageId>('home')
  const [confirmCommand, setConfirmCommand] = useState<'agent-start' | 'agent-stop' | null>(null)
  const [mobileMoreOpen, setMobileMoreOpen] = useState(false)
  const connected = runtime.hostConnected === true
  useEffect(() => {
    window.scrollTo({ top: 0, left: 0, behavior: 'auto' })
  }, [page])
  const mainContent = useMemo(() => {
    switch (page) {
      case 'agents': return <AgentsPage runtime={runtime} />
      case 'teacher': return <TeacherPage runtime={runtime} />
      case 'trading': return <TradingPage runtime={runtime} />
      case 'research': return <ResearchPage runtime={runtime} />
      case 'risk': return <RiskPage runtime={runtime} />
      case 'monitoring': return <MonitoringPage runtime={runtime} />
      case 'settings': return <SettingsPage runtime={runtime} />
      default: return <HomePage runtime={runtime} goTo={setPage} />
    }
  }, [page, runtime])

  return (
    <div className="wpe-app-shell">
      <Sidebar page={page} onChange={setPage} runtime={runtime} />
      <div className="wpe-app-main">
        <Topbar runtime={runtime} page={page} onRequestAction={setConfirmCommand} />
        <main className="wpe-content" id="main-content">
          {!connected ? <RuntimeDisconnected runtime={runtime} /> : mainContent}
        </main>
      </div>
      <MobileNav page={page} onChange={setPage} onMore={() => setMobileMoreOpen(true)} />
      {mobileMoreOpen && <MobileMoreMenu onSelect={setPage} onClose={() => setMobileMoreOpen(false)} />}
      {confirmCommand && <ConfirmDialog command={confirmCommand} runtime={runtime} onClose={() => setConfirmCommand(null)} />}
    </div>
  )
}

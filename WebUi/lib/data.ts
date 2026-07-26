// Mock data powering the Helios AI Agent Trading OS demo UI.

export type AgentStatus =
  | 'thinking'
  | 'analyzing'
  | 'executing'
  | 'waiting'
  | 'error'
  | 'completed'
  | 'offline'

export const statusMeta: Record<
  AgentStatus,
  { label: string; token: string }
> = {
  thinking: { label: '思考中', token: 'ai' },
  analyzing: { label: '分析中', token: 'info' },
  executing: { label: '执行中', token: 'success' },
  waiting: { label: '等待中', token: 'warning' },
  error: { label: '异常', token: 'danger' },
  completed: { label: '已完成', token: 'muted' },
  offline: { label: '离线', token: 'muted' },
}

export type Agent = {
  id: string
  name: string
  role: string
  status: AgentStatus
  model: string
  pnl24h: number
  winRate: number
  trades: number
  thought: string
  spark: number[]
}

export const agents: Agent[] = [
  {
    id: 'agt-01',
    name: 'Helios-Alpha',
    role: '趋势捕捉 · 主力',
    status: 'executing',
    model: 'gpt-5.2',
    pnl24h: 12840,
    winRate: 68.4,
    trades: 142,
    thought: '正在执行 BTC 多头分批建仓，剩余 2 笔委托单待成交…',
    spark: [12, 14, 13, 16, 18, 17, 21, 24, 22, 26, 29, 31],
  },
  {
    id: 'agt-02',
    name: 'Vega-Scout',
    role: '市场情绪扫描',
    status: 'analyzing',
    model: 'claude-4.5',
    pnl24h: 4210,
    winRate: 61.2,
    trades: 88,
    thought: '正在计算 ETH 资金费率与永续溢价，检测多空力量变化…',
    spark: [8, 9, 7, 10, 12, 11, 13, 12, 14, 15, 14, 16],
  },
  {
    id: 'agt-03',
    name: 'Orion-Arb',
    role: '跨所套利',
    status: 'thinking',
    model: 'gpt-5.2',
    pnl24h: 2180,
    winRate: 74.9,
    trades: 306,
    thought: '正在评估 Binance / OKX 之间 SOL 价差是否覆盖手续费…',
    spark: [20, 19, 21, 20, 22, 23, 22, 24, 23, 25, 26, 25],
  },
  {
    id: 'agt-04',
    name: 'Nyx-Hedge',
    role: '对冲与风险平衡',
    status: 'waiting',
    model: 'gemini-2.5',
    pnl24h: -640,
    winRate: 58.1,
    trades: 54,
    thought: '等待波动率突破阈值后再开启对冲头寸…',
    spark: [16, 15, 16, 14, 15, 13, 14, 13, 12, 13, 12, 11],
  },
  {
    id: 'agt-05',
    name: 'Atlas-DCA',
    role: '定投与网格',
    status: 'completed',
    model: 'claude-4.5',
    pnl24h: 1890,
    winRate: 66.0,
    trades: 210,
    thought: '本轮网格任务已完成，等待下一次调度…',
    spark: [10, 11, 12, 12, 13, 14, 15, 15, 16, 17, 18, 18],
  },
  {
    id: 'agt-06',
    name: 'Echo-Sentinel',
    role: '异常监测',
    status: 'error',
    model: 'gpt-5.2',
    pnl24h: 0,
    winRate: 0,
    trades: 12,
    thought: '交易所 WebSocket 连接超时，正在尝试重连…',
    spark: [14, 13, 12, 14, 10, 8, 9, 7, 8, 6, 7, 5],
  },
]

export type Market = {
  symbol: string
  name: string
  price: number
  change: number
  volume: string
  volatility: number
  sentiment: number // 0-100 bullish
  spark: number[]
}

export const markets: Market[] = [
  {
    symbol: 'BTC',
    name: 'Bitcoin',
    price: 96420.35,
    change: 2.84,
    volume: '38.2B',
    volatility: 42,
    sentiment: 72,
    spark: [92, 93, 92, 94, 95, 94, 96, 97, 96, 98, 97, 96.4],
  },
  {
    symbol: 'ETH',
    name: 'Ethereum',
    price: 3412.8,
    change: 1.62,
    volume: '18.6B',
    volatility: 51,
    sentiment: 64,
    spark: [33, 34, 33, 34, 35, 34, 35, 34, 35, 34, 35, 34.1],
  },
  {
    symbol: 'SOL',
    name: 'Solana',
    price: 214.62,
    change: -1.34,
    volume: '5.1B',
    volatility: 63,
    sentiment: 48,
    spark: [22, 21, 22, 20, 21, 20, 21, 20, 21, 20, 21, 21.4],
  },
  {
    symbol: 'BNB',
    name: 'BNB',
    price: 682.14,
    change: 0.42,
    volume: '2.3B',
    volatility: 34,
    sentiment: 55,
    spark: [67, 68, 67, 68, 69, 68, 68, 69, 68, 68, 69, 68.2],
  },
]

export type OrderStatus = 'pending' | 'filled' | 'cancelled' | 'failed'

export const orderStatusMeta: Record<
  OrderStatus,
  { label: string; token: string }
> = {
  pending: { label: '挂单中', token: 'warning' },
  filled: { label: '已成交', token: 'success' },
  cancelled: { label: '已取消', token: 'muted' },
  failed: { label: '失败', token: 'danger' },
}

export type Order = {
  id: string
  time: string
  symbol: string
  side: 'buy' | 'sell'
  type: string
  price: number
  amount: number
  status: OrderStatus
  agent: string
  strategy: string
  slippage: number
  fee: number
  pnl: number
}

export const orders: Order[] = [
  { id: 'ORD-7841', time: '14:32:08', symbol: 'BTC/USDT', side: 'buy', type: '限价', price: 96380, amount: 0.42, status: 'filled', agent: 'Helios-Alpha', strategy: '趋势突破 v3', slippage: 0.02, fee: 16.2, pnl: 340 },
  { id: 'ORD-7840', time: '14:31:55', symbol: 'ETH/USDT', side: 'sell', type: '市价', price: 3414, amount: 3.1, status: 'pending', agent: 'Vega-Scout', strategy: '情绪反转', slippage: 0.05, fee: 10.6, pnl: 0 },
  { id: 'ORD-7839', time: '14:30:12', symbol: 'SOL/USDT', side: 'buy', type: '限价', price: 213.4, amount: 42, status: 'filled', agent: 'Orion-Arb', strategy: '跨所套利', slippage: 0.01, fee: 8.9, pnl: 128 },
  { id: 'ORD-7838', time: '14:28:47', symbol: 'BTC/USDT', side: 'sell', type: '止损', price: 95200, amount: 0.18, status: 'cancelled', agent: 'Nyx-Hedge', strategy: '动态对冲', slippage: 0, fee: 0, pnl: 0 },
  { id: 'ORD-7837', time: '14:27:03', symbol: 'BNB/USDT', side: 'buy', type: '限价', price: 681, amount: 12, status: 'failed', agent: 'Echo-Sentinel', strategy: '异常捕捉', slippage: 0, fee: 0, pnl: 0 },
  { id: 'ORD-7836', time: '14:25:19', symbol: 'ETH/USDT', side: 'buy', type: '限价', price: 3402, amount: 5.4, status: 'filled', agent: 'Atlas-DCA', strategy: '网格定投', slippage: 0.03, fee: 18.4, pnl: 210 },
  { id: 'ORD-7835', time: '14:22:41', symbol: 'SOL/USDT', side: 'sell', type: '市价', price: 215.8, amount: 30, status: 'filled', agent: 'Orion-Arb', strategy: '跨所套利', slippage: 0.04, fee: 6.5, pnl: 96 },
  { id: 'ORD-7834', time: '14:20:08', symbol: 'BTC/USDT', side: 'buy', type: '限价', price: 96010, amount: 0.25, status: 'filled', agent: 'Helios-Alpha', strategy: '趋势突破 v3', slippage: 0.02, fee: 12.0, pnl: 180 },
]

export type DecisionStep = {
  id: string
  label: string
  detail: string
  status: 'done' | 'active' | 'pending'
  time: string
}

export const decisionFlow: DecisionStep[] = [
  { id: 's1', label: '市场数据接入', detail: '聚合 4 家交易所行情、订单簿与资金费率', status: 'done', time: '14:31:40' },
  { id: 's2', label: 'Agent 分析', detail: 'Helios-Alpha 识别 BTC 4H 级别趋势突破形态', status: 'done', time: '14:31:52' },
  { id: 's3', label: '策略生成', detail: '生成分批建仓策略：3 笔限价单，间距 0.3%', status: 'done', time: '14:32:01' },
  { id: 's4', label: '风险评估', detail: '风控引擎校验仓位、杠杆与最大回撤约束', status: 'active', time: '14:32:05' },
  { id: 's5', label: '订单执行', detail: '路由至 Binance，等待成交确认', status: 'pending', time: '—' },
  { id: 's6', label: '成交确认', detail: '回填成交价、滑点与手续费', status: 'pending', time: '—' },
  { id: 's7', label: '复盘总结', detail: '生成本轮交易复盘与经验记忆', status: 'pending', time: '—' },
]

export const thoughtStream: { time: string; text: string; token: string }[] = [
  { time: '14:32:06', text: '风控引擎介入：当前组合杠杆 1.8x，低于上限 3x，允许建仓。', token: 'success' },
  { time: '14:32:03', text: '正在计算 BTC 资金费率 0.011%，永续溢价温和，多头成本可控。', token: 'info' },
  { time: '14:31:58', text: '检测到 BTC 突破 96,200 关键阻力，成交量放大 34%。', token: 'ai' },
  { time: '14:31:44', text: '正在分析 BTC 4H 趋势结构，MACD 金叉确认动能。', token: 'ai' },
  { time: '14:31:30', text: '⚠ SOL 出现异常波动，短时振幅 2.1%，已通知对冲 Agent。', token: 'warning' },
]

export type PortfolioPoint = { t: string; v: number }
export const equityCurve: PortfolioPoint[] = [
  { t: '周一', v: 100 },
  { t: '周二', v: 103 },
  { t: '周三', v: 101 },
  { t: '周四', v: 106 },
  { t: '周五', v: 111 },
  { t: '周六', v: 109 },
  { t: '周日', v: 118 },
]

export const holdings = [
  { symbol: 'BTC', alloc: 44, value: 542000, pnl: 8.4 },
  { symbol: 'ETH', alloc: 26, value: 320000, pnl: 4.1 },
  { symbol: 'SOL', alloc: 14, value: 172000, pnl: -1.2 },
  { symbol: 'BNB', alloc: 9, value: 110000, pnl: 0.8 },
  { symbol: 'USDT', alloc: 7, value: 86000, pnl: 0 },
]

export type Resource = {
  label: string
  value: number
  unit: string
  cap: number
  token: string
}
export const resources: Resource[] = [
  { label: 'LLM Token', value: 1.24, unit: 'M', cap: 2, token: 'ai' },
  { label: 'API 请求', value: 42.6, unit: 'K/min', cap: 60, token: 'info' },
  { label: 'GPU 使用率', value: 61, unit: '%', cap: 100, token: 'warning' },
  { label: 'CPU', value: 38, unit: '%', cap: 100, token: 'success' },
  { label: '内存', value: 12.4, unit: 'GB', cap: 32, token: 'info' },
  { label: '当日成本', value: 284, unit: '$', cap: 500, token: 'ai' },
]

export type Service = {
  name: string
  status: 'healthy' | 'degraded' | 'down'
  latency: number
  uptime: string
}
export const services: Service[] = [
  { name: 'Exchange API', status: 'healthy', latency: 42, uptime: '99.98%' },
  { name: 'WebSocket', status: 'degraded', latency: 210, uptime: '99.62%' },
  { name: 'Database', status: 'healthy', latency: 8, uptime: '99.99%' },
  { name: 'Memory Store', status: 'healthy', latency: 3, uptime: '100%' },
  { name: 'Scheduler', status: 'healthy', latency: 12, uptime: '99.97%' },
  { name: 'Event Bus', status: 'healthy', latency: 6, uptime: '99.99%' },
  { name: 'Strategy Engine', status: 'healthy', latency: 24, uptime: '99.95%' },
  { name: 'LLM Service', status: 'degraded', latency: 880, uptime: '99.40%' },
]

export const serviceStatusMeta = {
  healthy: { label: '正常', token: 'success' },
  degraded: { label: '降级', token: 'warning' },
  down: { label: '中断', token: 'danger' },
} as const

export type RiskAlert = {
  level: 'high' | 'medium' | 'low'
  title: string
  detail: string
  time: string
}
export const riskAlerts: RiskAlert[] = [
  { level: 'high', title: 'SOL 波动率突增', detail: '15 分钟振幅达 2.1%，已触发对冲预案', time: '2 分钟前' },
  { level: 'medium', title: '组合回撤接近阈值', detail: '当前回撤 -4.2%，阈值 -6%', time: '11 分钟前' },
  { level: 'low', title: '资金费率偏高', detail: 'BTC 永续资金费率 0.011%', time: '26 分钟前' },
]

export const riskMetrics = {
  maxDrawdown: -4.2,
  currentRisk: 38,
  riskScore: 'B+',
  leverage: 1.8,
  sharpe: 2.14,
  var95: -2.8,
}

export type Strategy = {
  id: string
  name: string
  type: string
  status: 'running' | 'paused' | 'draft'
  agents: number
  pnl30d: number
  winRate: number
  sharpe: number
  spark: number[]
}
export const strategyStatusMeta: Record<
  Strategy['status'],
  { label: string; token: string }
> = {
  running: { label: '运行中', token: 'success' },
  paused: { label: '已暂停', token: 'warning' },
  draft: { label: '草稿', token: 'muted' },
}

export const strategies: Strategy[] = [
  { id: 'st-1', name: '趋势突破 v3', type: '动量', status: 'running', agents: 2, pnl30d: 18.4, winRate: 68, sharpe: 2.3, spark: [10, 12, 11, 14, 16, 15, 19] },
  { id: 'st-2', name: '跨所套利', type: '套利', status: 'running', agents: 1, pnl30d: 9.2, winRate: 75, sharpe: 3.1, spark: [8, 9, 9, 10, 11, 11, 12] },
  { id: 'st-3', name: '情绪反转', type: '均值回归', status: 'paused', agents: 1, pnl30d: 4.6, winRate: 61, sharpe: 1.4, spark: [6, 7, 6, 8, 7, 8, 9] },
  { id: 'st-4', name: '网格定投', type: '网格', status: 'running', agents: 1, pnl30d: 6.1, winRate: 66, sharpe: 1.8, spark: [5, 6, 7, 7, 8, 9, 10] },
  { id: 'st-5', name: '动态对冲', type: '对冲', status: 'draft', agents: 0, pnl30d: 0, winRate: 0, sharpe: 0, spark: [7, 7, 7, 7, 7, 7, 7] },
]

export type Backtest = {
  id: string
  strategy: string
  period: string
  totalReturn: number
  maxDrawdown: number
  sharpe: number
  winRate: number
  trades: number
  status: 'completed' | 'running'
}
export const backtests: Backtest[] = [
  { id: 'bt-1', strategy: '趋势突破 v3', period: '2024-01 ~ 2024-12', totalReturn: 142.6, maxDrawdown: -18.2, sharpe: 2.3, winRate: 64, trades: 1240, status: 'completed' },
  { id: 'bt-2', strategy: '跨所套利', period: '2024-06 ~ 2024-12', totalReturn: 38.4, maxDrawdown: -4.1, sharpe: 3.4, winRate: 78, trades: 4820, status: 'completed' },
  { id: 'bt-3', strategy: '情绪反转', period: '2024-01 ~ 2024-12', totalReturn: 51.2, maxDrawdown: -22.6, sharpe: 1.5, winRate: 58, trades: 640, status: 'running' },
]

export const backtestCurve = [
  100, 104, 102, 110, 118, 115, 124, 132, 128, 140, 152, 148, 160, 172, 168, 182, 195, 210, 205, 224, 242,
]
export const backtestBenchmark = [
  100, 101, 100, 103, 105, 104, 107, 110, 108, 112, 115, 113, 118, 121, 119, 124, 128, 132, 130, 136, 142,
]

export const backtestStatusMeta: Record<
  Backtest['status'],
  { label: string; token: string }
> = {
  completed: { label: '已完成', token: 'success' },
  running: { label: '运行中', token: 'info' },
}

export const backtestMonthly: { m: string; v: number }[] = [
  { m: '1月', v: 4.2 },
  { m: '2月', v: -1.8 },
  { m: '3月', v: 6.4 },
  { m: '4月', v: 2.1 },
  { m: '5月', v: -3.2 },
  { m: '6月', v: 8.6 },
  { m: '7月', v: 5.1 },
  { m: '8月', v: -0.9 },
  { m: '9月', v: 7.2 },
  { m: '10月', v: 3.4 },
  { m: '11月', v: 9.8 },
  { m: '12月', v: 4.6 },
]

export const backtestStats = {
  profitFactor: 2.14,
  avgTrade: 0.42,
  volatility: 18.6,
  sortino: 3.1,
}

export type Plugin = {
  id: string
  name: string
  category: string
  description: string
  installed: boolean
  enabled: boolean
  author: string
  calls: string
}
export const plugins: Plugin[] = [
  { id: 'pl-1', name: 'TradingView 信号', category: '数据源', description: '接入 TradingView 指标与图表信号', installed: true, enabled: true, author: 'Helios Labs', calls: '12.4K' },
  { id: 'pl-2', name: '链上数据 (Glassnode)', category: '数据源', description: '实时链上指标：活跃地址、交易所净流', installed: true, enabled: true, author: 'Glassnode', calls: '3.1K' },
  { id: 'pl-3', name: '新闻情绪分析', category: 'AI 工具', description: '抓取新闻与社媒并进行情绪打分', installed: true, enabled: false, author: 'Vega AI', calls: '8.9K' },
  { id: 'pl-4', name: 'Telegram 通知', category: '通知', description: '将交易与告警推送到 Telegram', installed: true, enabled: true, author: 'Community', calls: '620' },
  { id: 'pl-5', name: '智能止损引擎', category: '风控', description: '基于波动率自适应调整止损位', installed: false, enabled: false, author: 'Nyx Labs', calls: '—' },
  { id: 'pl-6', name: 'DeFi 收益聚合', category: '执行', description: '闲置资金自动配置到 DeFi 收益协议', installed: false, enabled: false, author: 'Atlas DeFi', calls: '—' },
]

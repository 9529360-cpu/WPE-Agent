import type { Locale } from '@/lib/i18n/dictionaries'

const zhCn: Record<string,string> = {
  Running:'运行中', Monitoring:'持续监控', Waiting:'等待', Stopped:'已停止', Idle:'空闲', Degraded:'降级', Blocked:'阻塞',
  Hold:'观望', OpenLong:'开多', OpenShort:'开空', CloseLong:'平多', CloseShort:'平空', ReduceLong:'减多', ReduceShort:'减空',
  Auto:'自动交易', Review:'审查模式', Research:'研究模式', Signal:'信号模式',
  Testnet:'测试网', FuturesTestnet:'合约测试网', ProviderTestnet:'测试网',
  Available:'可用', available:'可用', Stale:'已过期', stale:'已过期', Unknown:'未知', unknown:'未知',
  Ready:'就绪', ready:'就绪', Error:'错误', error:'错误',
  Bullish:'多头', Bearish:'空头', Range:'震荡', Mixed:'混合',
  BullishImpulse:'多头推进', BearishImpulse:'空头推进', BullishPullback:'多头回踩', BearishPullback:'空头反抽',
  Balance:'震荡平衡', Compression:'收敛', BullishReversalAttempt:'多头反转尝试', BearishReversalAttempt:'空头反转尝试',
  TrendPullbackLong:'趋势回踩做多', TrendPullbackShort:'趋势反抽做空',
  RangeReversionLong:'区间低位回归做多', RangeReversionShort:'区间高位回归做空',
  BreakoutRetestLong:'突破回踩做多', BreakoutRetestShort:'跌破反抽做空',
  None:'无',
  'Local Only':'纯本地',
}

const zhTw: Record<string,string> = {
  Running:'運行中', Monitoring:'持續監控', Waiting:'等待', Stopped:'已停止', Idle:'閒置', Degraded:'降級', Blocked:'阻塞',
  Hold:'觀望', OpenLong:'開多', OpenShort:'開空', CloseLong:'平多', CloseShort:'平空', ReduceLong:'減多', ReduceShort:'減空',
  Auto:'自動交易', Review:'審查模式', Research:'研究模式', Signal:'訊號模式',
  Testnet:'測試網', FuturesTestnet:'合約測試網', ProviderTestnet:'測試網',
  Available:'可用', available:'可用', Stale:'已過期', stale:'已過期', Unknown:'未知', unknown:'未知',
  Ready:'就緒', ready:'就緒', Error:'錯誤', error:'錯誤',
  Bullish:'多頭', Bearish:'空頭', Range:'震盪', Mixed:'混合',
  BullishImpulse:'多頭推進', BearishImpulse:'空頭推進', BullishPullback:'多頭回踩', BearishPullback:'空頭反抽',
  Balance:'震盪平衡', Compression:'收斂', BullishReversalAttempt:'多頭反轉嘗試', BearishReversalAttempt:'空頭反轉嘗試',
  TrendPullbackLong:'趨勢回踩做多', TrendPullbackShort:'趨勢反抽做空',
  RangeReversionLong:'區間低位回歸做多', RangeReversionShort:'區間高位回歸做空',
  BreakoutRetestLong:'突破回踩做多', BreakoutRetestShort:'跌破反抽做空',
  None:'無',
  'Local Only':'純本地',
}

const workflowZhCn: Array<[RegExp,string]> = [
  [/evidence/i,'采集市场证据'],
  [/market/i,'分析市场结构'],
  [/decision/i,'生成交易决策'],
  [/risk/i,'执行风险检查'],
  [/execution/i,'检查/执行订单'],
  [/position/i,'管理持仓'],
  [/recovery/i,'恢复与对账'],
  [/audit/i,'记录审计'],
]

const workflowZhTw: Array<[RegExp,string]> = [
  [/evidence/i,'採集市場證據'],
  [/market/i,'分析市場結構'],
  [/decision/i,'生成交易決策'],
  [/risk/i,'執行風險檢查'],
  [/execution/i,'檢查/執行訂單'],
  [/position/i,'管理持倉'],
  [/recovery/i,'恢復與對帳'],
  [/audit/i,'記錄稽核'],
]

export function runtimeLabel(value:string|undefined|null,locale:Locale){
  if(!value)return undefined
  if(locale==='zh_CN')return zhCn[value]??value
  if(locale==='zh_TW')return zhTw[value]??value
  return value
}

export function workflowLabel(value:string|undefined|null,locale:Locale){
  if(!value)return undefined
  const mappings=locale==='zh_CN'?workflowZhCn:locale==='zh_TW'?workflowZhTw:undefined
  if(!mappings)return value
  return mappings.find(([pattern])=>pattern.test(value))?.[1]??runtimeLabel(value,locale)
}

export function decisionSummary(value:string|undefined|null,locale:Locale){
  const decision=runtimeLabel(value,locale)
  if(locale==='zh_CN'){
    if(!value||value==='Hold')return {title:'观望',detail:'正在持续扫描市场，目前没有出现同时满足结构、触发、确认和风控条件的交易机会。'}
    if(value==='OpenLong')return {title:'准备开多',detail:'本地交易脑检测到已确认的多头结构，正在进入风险与执行链路。'}
    if(value==='OpenShort')return {title:'准备开空',detail:'本地交易脑检测到已确认的空头结构，正在进入风险与执行链路。'}
    if(value==='CloseLong')return {title:'准备平多',detail:'多头持仓出现退出条件，系统正在按风险降低路径处理。'}
    if(value==='CloseShort')return {title:'准备平空',detail:'空头持仓出现退出条件，系统正在按风险降低路径处理。'}
  }
  if(locale==='zh_TW'){
    if(!value||value==='Hold')return {title:'觀望',detail:'正在持續掃描市場，目前沒有同時滿足結構、觸發、確認與風控條件的交易機會。'}
  }
  return {title:decision??value??'—',detail:''}
}

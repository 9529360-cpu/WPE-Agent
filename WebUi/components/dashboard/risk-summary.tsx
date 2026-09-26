'use client'
import {ShieldAlert} from 'lucide-react'
import {Panel,PanelBody,PanelHeader} from '@/components/ui/panel'
import {RuntimeMetric,RuntimeUnavailable} from '@/components/runtime-state'
import {useWpeRuntime} from '@/components/runtime-bridge'
import {useI18n} from '@/lib/i18n/context'
import {runtimeLabel} from '@/lib/runtime-labels'

export function RiskSummary(){
 const r=useWpeRuntime(),{t,locale}=useI18n()
 const zh=locale==='zh_CN',zht=locale==='zh_TW'
 const summary=zh?(r.riskReady?'风险检查正常，系统会在每次交易前重新验证。':'风控当前未就绪，系统不会放行新的风险。'):zht?(r.riskReady?'風險檢查正常，系統會在每次交易前重新驗證。':'風控目前未就緒，系統不會放行新的風險。'):r.riskSummary
 return <Panel>
  <PanelHeader icon={<ShieldAlert className="size-4"/>} title={t('dashboard.riskSummary')}/>
  {r.runtimeFresh?
   <PanelBody className="grid gap-3 sm:grid-cols-2">
    <RuntimeMetric label={t('dashboard.riskGate')} value={runtimeLabel(r.riskApprovalStatus,locale)??(r.riskReady?t('common.ready'):t('common.notReady'))}/>
    <RuntimeMetric label={t('dashboard.riskLoad')} value={r.riskLoad===undefined?undefined:`${Math.round(r.riskLoad)}%`}/>
    <RuntimeMetric label={t('dashboard.maxDrawdown')} value={r.maxDrawdown===undefined?undefined:`${r.maxDrawdown.toFixed(2)}%`}/>
    <RuntimeMetric label={t('dashboard.riskSummary')} value={summary}/>
   </PanelBody>:
   <div className="p-4"><RuntimeUnavailable stale={Boolean(r.lastUpdated)} subject={t('risk.metrics')}/></div>}
 </Panel>
}
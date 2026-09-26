'use client'
import {ServerCog} from 'lucide-react'
import {Panel,PanelBody,PanelHeader} from '@/components/ui/panel'
import {RuntimeMetric,RuntimeUnavailable} from '@/components/runtime-state'
import {useWpeRuntime} from '@/components/runtime-bridge'
import {useI18n} from '@/lib/i18n/context'
import {runtimeLabel} from '@/lib/runtime-labels'

export function SystemHealth(){
 const r=useWpeRuntime(),{t,locale}=useI18n()
 const agentLabel=locale==='zh_CN'?'Agent 运行状态':locale==='zh_TW'?'Agent 運行狀態':'Agent'
 return <Panel>
  <PanelHeader icon={<ServerCog className="size-4"/>} title={t('dashboard.systemHealth')}/>
  {r.runtimeFresh?
   <PanelBody className="grid gap-3 sm:grid-cols-2 lg:grid-cols-4">
    <RuntimeMetric label={agentLabel} value={runtimeLabel(r.status,locale)}/>
    <RuntimeMetric label={t('dashboard.exchange')} value={r.exchangeConnected?t('common.healthy'):t('common.notConnected')}/>
    <RuntimeMetric label={t('dashboard.brain')} value={r.brainConnected?t('common.healthy'):t('common.notConnected')}/>
    <RuntimeMetric label={t('dashboard.risk')} value={r.riskReady?t('common.ready'):t('common.notReady')}/>
   </PanelBody>:
   <div className="p-4"><RuntimeUnavailable stale={Boolean(r.lastUpdated)} subject={t('dashboard.systemHealth')}/></div>}
 </Panel>
}
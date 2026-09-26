'use client'
import {useMemo,useState} from 'react'
import {ListFilter} from 'lucide-react'
import {useWpeRuntime} from '@/components/runtime-bridge'
import {RuntimeUnavailable} from '@/components/runtime-state'
import {Panel,PanelBody,PanelHeader} from '@/components/ui/panel'
import {useI18n} from '@/lib/i18n/context'
import {runtimeLabel} from '@/lib/runtime-labels'

export function MarketSelector(){
 const runtime=useWpeRuntime(),{t,locale}=useI18n()
 const[exchange,setExchange]=useState(''),[provider,setProvider]=useState(''),[symbol,setSymbol]=useState('')
 const markets=useMemo(()=>runtime.markets??[],[runtime.markets])
 const exchanges=[...new Set(markets.map(x=>x.exchangeId))]
 const providers=[...new Set(markets.filter(x=>!exchange||x.exchangeId===exchange).map(x=>x.providerId))]
 const symbols=markets.filter(x=>(!exchange||x.exchangeId===exchange)&&(!provider||x.providerId===provider))
 const selected=symbols.find(x=>x.symbol===symbol)
 const capability=selected?runtime.capabilities?.find(x=>x.exchangeId===selected.exchangeId&&x.providerId===selected.providerId&&x.canonicalSymbol===selected.symbol):undefined
 const zh=locale==='zh_CN',zht=locale==='zh_TW'
 const exchangeLabel=zh?'交易所':zht?'交易所':'Exchange'
 const providerLabel=zh?'数据提供方':zht?'資料提供方':'Provider'
 const capabilityLabel=zh?'能力状态':zht?'能力狀態':'Capability'
 const testnetLabel=zh?'测试网':zht?'測試網':'Testnet'
 const tradeLabel=zh?'交易权限':zht?'交易權限':'Trade'
 const allowed=zh?'允许':zht?'允許':'Allowed'
 const blocked=zh?'禁止':zht?'禁止':'Blocked'
 const available=zh?'可用':zht?'可用':'Available'
 const unavailable=zh?'不可用':zht?'不可用':'Unavailable'
 if(!runtime.runtimeFresh||!markets.length)return <RuntimeUnavailable stale={Boolean(runtime.lastUpdated)} subject={t('dashboard.marketSelector')}/>
 return <Panel>
  <PanelHeader icon={<ListFilter className="size-4"/>} title={t('dashboard.marketSelector')} action={<span className="text-[11px] text-muted-foreground">{t('settings.readOnly')}</span>}/>
  <PanelBody className="grid gap-3 sm:grid-cols-3">
   <label className="grid gap-1 text-xs text-muted-foreground">{exchangeLabel}<select className="h-9 min-w-0 rounded-md border border-border bg-background px-2 text-sm text-foreground" value={exchange} onChange={e=>{setExchange(e.target.value);setProvider('');setSymbol('')}}><option value="">{t('common.select')}</option>{exchanges.map(x=><option key={x}>{x}</option>)}</select></label>
   <label className="grid gap-1 text-xs text-muted-foreground">{providerLabel}<select className="h-9 min-w-0 rounded-md border border-border bg-background px-2 text-sm text-foreground" value={provider} onChange={e=>{setProvider(e.target.value);setSymbol('')}}><option value="">{t('common.select')}</option>{providers.map(x=><option key={x}>{x}</option>)}</select></label>
   <label className="grid gap-1 text-xs text-muted-foreground">{t('common.symbol')}<select className="h-9 min-w-0 rounded-md border border-border bg-background px-2 text-sm text-foreground" value={symbol} onChange={e=>setSymbol(e.target.value)}><option value="">{t('common.select')}</option>{symbols.map(x=><option key={`${x.exchangeId}:${x.symbol}`} value={x.symbol}>{x.symbol}</option>)}</select></label>
   {selected&&<div className="break-words rounded-md border border-border bg-panel/40 p-3 text-xs text-muted-foreground sm:col-span-3">
    <span className="font-medium text-foreground">{selected.symbol}</span>
    <span> · {exchangeLabel} {selected.exchangeId}</span>
    <span> · {providerLabel} {selected.providerId}</span>
    <span> · {capabilityLabel} {runtimeLabel(capability?.status,locale)??unavailable}</span>
    <span> · {testnetLabel} {capability?.testnetAvailable?available:unavailable}</span>
    <span> · {tradeLabel} {capability?.canTrade?allowed:blocked}</span>
   </div>}
  </PanelBody>
 </Panel>
}
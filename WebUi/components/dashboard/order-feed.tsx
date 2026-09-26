'use client'
import Link from 'next/link'
import {Receipt} from 'lucide-react'
import {Button} from '@/components/ui/button'
import {Panel,PanelBody,PanelHeader} from '@/components/ui/panel'
import {useWpeRuntime} from '@/components/runtime-bridge'
import {useI18n} from '@/lib/i18n/context'
import {runtimeLabel} from '@/lib/runtime-labels'

export function OrderFeed(){
 const runtime=useWpeRuntime(),{t,formatNumber,locale}=useI18n()
 const state=runtime.runtimeFresh===false&&runtime.collectionStates?.orders==='available'?'stale':runtime.collectionStates?.orders??'unsupported'
 const orders=runtime.orders??[]
 return <Panel>
  <PanelHeader icon={<Receipt className="size-4"/>} title={t('dashboard.openOrders')} action={<Button variant="ghost" size="xs" nativeButton={false} render={<Link href="/orders"/>}>{t('dashboard.viewAll')}</Button>}/>
  <PanelBody>
   {state!=='available'?
    <div className="py-5 text-center text-xs text-muted-foreground">{t(state==='stale'?'orders.staleTitle':state==='error'?'orders.errorTitle':'orders.unsupportedTitle')}</div>:
    !orders.length?
     <div className="py-5 text-center text-xs text-muted-foreground">{t('dashboard.noOrders')}</div>:
     <div className="space-y-2">{orders.slice(0,4).map(order=>
      <div key={order.orderId} className="grid grid-cols-[minmax(0,1fr)_auto] gap-3 rounded-lg border border-border p-3 text-xs">
       <div className="min-w-0">
        <div className="truncate font-medium">{order.symbol} · {runtimeLabel(order.type,locale)??order.type}</div>
        <div className="mt-1 truncate text-muted-foreground">{runtimeLabel(order.side,locale)??order.side} · {order.orderId}</div>
       </div>
       <div className="text-right">
        <div>{runtimeLabel(order.status,locale)??order.status}</div>
        <div className="mt-1 text-muted-foreground">{formatNumber(order.quantity,{maximumFractionDigits:8})}</div>
       </div>
      </div>)}</div>}
  </PanelBody>
 </Panel>
}
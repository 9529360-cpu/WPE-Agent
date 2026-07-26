'use client'

import { CandlestickChart } from 'lucide-react'
import { Panel, PanelBody, PanelHeader } from '@/components/ui/panel'
import { RuntimeUnavailable } from '@/components/runtime-state'
import { useWpeRuntime } from '@/components/runtime-bridge'
import { useI18n } from '@/lib/i18n/context'
import { cn } from '@/lib/utils'

export function MarketOverview() {
  const runtime = useWpeRuntime()
  const { t, formatNumber } = useI18n()
  const market = runtime.publicMarkets
  const klines = runtime.publicKlines?.items ?? []
  const state = market?.state ?? runtime.publicKlines?.state

  if ((!market || market.items.length === 0) && klines.length === 0) {
    return <Panel><PanelHeader icon={<CandlestickChart className="size-4" />} title={t('dashboard.marketOverview')} action={<span className="text-[11px] text-muted-foreground">{t('common.unknown')}</span>} /><div className="p-4"><RuntimeUnavailable subject={t('dashboard.marketOverview')} /></div></Panel>
  }

  return <Panel>
    <PanelHeader
      icon={<CandlestickChart className="size-4" />}
      title={t('dashboard.marketOverview')}
      action={<span className="text-[11px] text-muted-foreground">{state === 'available' ? t('common.available') : state === 'stale' ? t('common.stale') : t('common.unknown')}</span>}
    />
    <PanelBody className="grid gap-3 sm:grid-cols-2">
      {(market?.items ?? []).map(item => {
        const status = item.state === 'Stale' || item.stale ? t('common.stale') : item.state === 'Unknown' ? t('common.unknown') : t('common.available')
        return <div key={item.symbol} className="min-w-0 border-l-2 border-border pl-3">
          <div className="flex items-center justify-between gap-3">
            <span className="text-sm font-semibold">{item.symbol}</span>
            <span className={cn('text-[11px] font-medium', item.stale || item.state !== 'Available' ? 'text-warning' : 'text-success')}>{status}</span>
          </div>
          <div className="mt-2 grid grid-cols-3 gap-2 text-xs">
            <div><div className="text-muted-foreground">{t('common.price')}</div><div className="mt-0.5 font-medium tabular-nums">{item.price === null ? t('common.unknown') : formatNumber(item.price, { maximumFractionDigits: 8 })}</div></div>
            <div><div className="text-muted-foreground">{t('common.change')}</div><div className={cn('mt-0.5 font-medium tabular-nums', (item.changePercent ?? 0) >= 0 ? 'text-success' : 'text-danger')}>{item.changePercent === null ? t('common.unknown') : `${item.changePercent >= 0 ? '+' : ''}${formatNumber(item.changePercent, { maximumFractionDigits: 2 })}%`}</div></div>
            <div><div className="text-muted-foreground">Volume</div><div className="mt-0.5 font-medium tabular-nums">{item.volume === null ? t('common.unknown') : formatNumber(item.volume, { maximumFractionDigits: 2 })}</div></div>
          </div>
          <div className="mt-2 break-words text-[10px] text-muted-foreground">{item.source} · {item.updatedAt ? new Date(item.updatedAt).toLocaleString() : t('common.noEventTime')}</div>
        </div>
      })}
      {klines.length > 0 && <div className="border-t border-border pt-3 sm:col-span-2">
        <div className="mb-2 text-xs font-medium text-muted-foreground">Kline snapshots</div>
        <div className="grid gap-2 sm:grid-cols-2">
          {klines.map(item => <div key={`${item.symbol}:${item.interval}`} className="grid grid-cols-[minmax(0,1fr)_auto] gap-x-3 gap-y-1 border-l-2 border-border pl-3 text-xs">
            <div className="font-medium">{item.symbol} · {item.interval}</div>
            <div className={cn('font-medium', item.stale || item.state !== 'Available' ? 'text-warning' : 'text-success')}>{item.stale || item.state === 'Stale' ? t('common.stale') : item.state === 'Unknown' ? t('common.unknown') : t('common.available')}</div>
            <div className="text-muted-foreground">Close: <span className="text-foreground">{item.close === null ? t('common.unknown') : formatNumber(item.close, { maximumFractionDigits: 8 })}</span> · Volume: <span className="text-foreground">{item.volume === null ? t('common.unknown') : formatNumber(item.volume, { maximumFractionDigits: 2 })}</span></div>
            <div className="text-right text-[10px] text-muted-foreground">{item.updatedAt ? new Date(item.updatedAt).toLocaleString() : t('common.noEventTime')}</div>
            <div className="break-words text-[10px] text-muted-foreground sm:col-span-2">{item.source}</div>
          </div>)}
        </div>
      </div>}
    </PanelBody>
  </Panel>
}

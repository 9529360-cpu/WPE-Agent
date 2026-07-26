'use client'

import { AlertTriangle, WalletCards } from 'lucide-react'
import { PageHeader } from '@/components/shell/page-header'
import { useWpeRuntime, type RuntimeCollectionState, type RuntimePosition } from '@/components/runtime-bridge'
import { RuntimeMetric } from '@/components/runtime-state'
import { Panel, PanelBody, PanelHeader } from '@/components/ui/panel'
import { useI18n } from '@/lib/i18n/context'

function collectionState(fresh: boolean | undefined, state: RuntimeCollectionState | undefined): RuntimeCollectionState {
  return fresh === false && state === 'available' ? 'stale' : state ?? 'unsupported'
}

function PositionCard({ position }: { position: RuntimePosition }) {
  const { t, formatNumber } = useI18n()
  const long = /long|buy/i.test(position.side)
  return (
    <article className="rounded-lg border border-border p-4">
      <div className="flex min-w-0 items-start justify-between gap-3">
        <div className="min-w-0">
          <h2 className="break-words font-medium">{position.symbol}</h2>
          <span className={long ? 'text-success' : 'text-danger'}>{position.side}</span>
        </div>
        <div className={position.unrealizedPnl >= 0 ? 'text-success' : 'text-danger'}>
          {formatNumber(position.unrealizedPnl, { maximumFractionDigits: 2 })} USDT
        </div>
      </div>
      <dl className="mt-4 grid grid-cols-2 gap-3 border-t border-border pt-3 text-xs">
        <div><dt className="text-muted-foreground">{t('common.quantity')}</dt><dd>{formatNumber(position.quantity, { maximumFractionDigits: 8 })}</dd></div>
        <div><dt className="text-muted-foreground">{t('dashboard.entryPrice')}</dt><dd>{formatNumber(position.entryPrice, { maximumFractionDigits: 8 })}</dd></div>
      </dl>
    </article>
  )
}

export default function PositionsPage() {
  const runtime = useWpeRuntime()
  const { t } = useI18n()
  const state = collectionState(runtime.runtimeFresh, runtime.collectionStates?.positions)
  const positions = state === 'available' ? runtime.positions ?? [] : []

  return (
    <div className="flex min-w-0 flex-col gap-5">
      <PageHeader title="Positions" description="Read-only positions from the current trusted host snapshot" />
      <Panel>
        <PanelBody className="grid gap-3 sm:grid-cols-3">
          <RuntimeMetric label="Collection" value={state} />
          <RuntimeMetric label={t('dashboard.wallet')} value={state === 'available' ? runtime.walletBalance : undefined} />
          <RuntimeMetric label="Open positions" value={state === 'available' ? positions.length : undefined} />
        </PanelBody>
      </Panel>
      {state !== 'available' ? (
        <Panel>
          <PanelBody className="flex min-h-48 flex-col items-center justify-center p-8 text-center">
            <AlertTriangle className="mb-3 size-5 text-warning" />
            <div className="text-sm font-medium">{t(state === 'stale' ? 'dashboard.positionsStale' : state === 'error' ? 'dashboard.positionsError' : 'dashboard.positionsMissing')}</div>
            <p className="mt-2 max-w-xl text-xs text-muted-foreground">{runtime.collectionMessages?.positions || 'No previous position rows are shown when current host evidence is unavailable.'}</p>
          </PanelBody>
        </Panel>
      ) : positions.length === 0 ? (
        <Panel><PanelBody className="flex min-h-48 items-center justify-center text-sm text-muted-foreground">{t('dashboard.noPositions')}</PanelBody></Panel>
      ) : (
        <Panel>
          <PanelHeader icon={<WalletCards className="size-4" />} title={`Open positions · ${positions.length}`} />
          <PanelBody className="grid gap-3 md:grid-cols-2 xl:grid-cols-3">{positions.map((position, index) => <PositionCard key={`${position.symbol}-${position.side}-${index}`} position={position} />)}</PanelBody>
        </Panel>
      )}
    </div>
  )
}

'use client'

import { EquityMarketView, type EquityMarketReadModel } from '@/components/equities/equity-market-view'
import { useWpeRuntime, type WpeRuntimeState } from '@/components/runtime-bridge'
import { PageHeader } from '@/components/shell/page-header'
import { StatusBadge } from '@/components/ui/status-badge'

type RuntimeWithEquities = WpeRuntimeState & { equities?: EquityMarketReadModel }

export default function EquitiesPage() {
  const runtime = useWpeRuntime() as RuntimeWithEquities
  const model = runtime.equities ?? {
    state: 'unsupported' as const,
    items: [],
    message: 'The current runtime contract does not expose an equity market read model.',
  }

  return (
    <div className="flex flex-col gap-5">
      <PageHeader
        title="Global equities"
        description="Provider-neutral, host-authoritative market observations"
        actions={runtime.previewMode ? <StatusBadge token="warning" label="Preview" /> : undefined}
      />
      <EquityMarketView model={model} />
    </div>
  )
}

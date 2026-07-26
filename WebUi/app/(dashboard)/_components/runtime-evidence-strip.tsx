'use client'

import { useWpeRuntime } from '@/components/runtime-bridge'
import { useI18n } from '@/lib/i18n/context'
import { cn } from '@/lib/utils'

export function RuntimeEvidenceStrip() {
  const runtime = useWpeRuntime()
  const { formatDate } = useI18n()
  const fresh = runtime.runtimeFresh === true
  const source = runtime.runtimeProviderId ?? 'provider unavailable'
  const environment = runtime.environment ?? 'environment unavailable'
  const sourceTime = runtime.sourceTimestampUtc ? formatDate(runtime.sourceTimestampUtc) : 'source unavailable'
  const snapshotTime = runtime.snapshotTimestampUtc ? formatDate(runtime.snapshotTimestampUtc) : 'snapshot unavailable'
  const freshness = fresh ? `fresh ${Math.round(runtime.runtimeAgeSeconds ?? 0)}s` : runtime.runtimeDiagnosticReason ?? 'runtime unavailable'

  return (
    <div
      className="mb-4 flex min-w-0 flex-wrap items-center gap-x-3 gap-y-1 border-b border-border pb-3 font-mono text-[10px] text-muted-foreground"
      role="status"
      aria-label="Runtime data evidence"
      data-runtime-evidence="host-authoritative"
    >
      <span className={cn('font-medium', fresh ? 'text-success' : 'text-warning')}>{fresh ? 'HOST FRESH' : 'HOST UNAVAILABLE'}</span>
      <span>{source}</span>
      <span>{environment}</span>
      <span>source {sourceTime}</span>
      <span>snapshot {snapshotTime}</span>
      <span className={fresh ? 'text-success' : 'text-warning'}>{freshness}</span>
    </div>
  )
}

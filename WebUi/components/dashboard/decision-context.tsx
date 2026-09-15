'use client'

import { ClipboardCheck } from 'lucide-react'
import { useWpeRuntime } from '@/components/runtime-bridge'
import { RuntimeUnavailable } from '@/components/runtime-state'
import { Panel, PanelBody, PanelHeader } from '@/components/ui/panel'
import { useI18n } from '@/lib/i18n/context'

export function DecisionContext() {
  const runtime = useWpeRuntime()
  const { t } = useI18n()

  if (!runtime.runtimeFresh) {
    return <RuntimeUnavailable stale={Boolean(runtime.lastUpdated)} subject={t('dashboard.lastDecision')} />
  }

  const decision = runtime.lastDecision?.trim()
  const detail = runtime.decisionDiagnostics?.trim() || runtime.lastReason?.trim()

  return (
    <Panel className="h-full">
      <PanelHeader icon={<ClipboardCheck className="size-4" />} title={t('dashboard.lastDecision')} />
      <PanelBody className="space-y-4">
        <div>
          <div className="font-mono text-[10px] uppercase tracking-wide text-muted-foreground">{t('dashboard.lastDecision')}</div>
          <div className="mt-1 break-words text-sm font-medium">{decision || t('common.notProvided')}</div>
        </div>
        <div className="border-t border-border pt-4">
          <div className="font-mono text-[10px] uppercase tracking-wide text-muted-foreground">{t('common.reason')}</div>
          <p className="mt-1 break-words text-sm leading-6 text-muted-foreground">{detail || t('common.notProvided')}</p>
        </div>
      </PanelBody>
    </Panel>
  )
}

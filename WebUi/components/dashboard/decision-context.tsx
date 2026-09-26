'use client'

import { ClipboardCheck } from 'lucide-react'
import { useWpeRuntime } from '@/components/runtime-bridge'
import { RuntimeUnavailable } from '@/components/runtime-state'
import { Panel, PanelBody, PanelHeader } from '@/components/ui/panel'
import { useI18n } from '@/lib/i18n/context'
import { decisionSummary, runtimeLabel } from '@/lib/runtime-labels'

export function DecisionContext() {
  const runtime = useWpeRuntime()
  const { t, locale } = useI18n()

  if (!runtime.runtimeFresh) {
    return <RuntimeUnavailable stale={Boolean(runtime.lastUpdated)} subject={t('dashboard.lastDecision')} />
  }

  const summary = decisionSummary(runtime.lastDecision, locale)
  const zh = locale === 'zh_CN'
  const zht = locale === 'zh_TW'
  const actionLabel = zh ? '当前动作' : zht ? '目前動作' : t('dashboard.lastDecision')
  const reasonLabel = zh ? '系统解释' : zht ? '系統解釋' : t('common.reason')
  const stageLabel = zh ? '当前阶段' : zht ? '目前階段' : 'Stage'
  const stage = runtimeLabel(runtime.workflowNode, locale)

  return (
    <Panel className="h-full">
      <PanelHeader icon={<ClipboardCheck className="size-4" />} title={t('dashboard.lastDecision')} />
      <PanelBody className="space-y-4">
        <div>
          <div className="text-[10px] uppercase tracking-wide text-muted-foreground">{actionLabel}</div>
          <div className="mt-1 break-words text-lg font-semibold">{summary.title}</div>
        </div>
        <div className="border-t border-border pt-4">
          <div className="text-[10px] uppercase tracking-wide text-muted-foreground">{reasonLabel}</div>
          <p className="mt-1 break-words text-sm leading-6 text-muted-foreground">
            {summary.detail || (runtime.lastReason?.trim() ? runtime.lastReason : t('common.notProvided'))}
          </p>
        </div>
        {stage ? <div className="border-t border-border pt-4 text-xs">
          <span className="text-muted-foreground">{stageLabel}：</span>
          <span className="font-medium text-foreground">{stage}</span>
        </div> : null}
      </PanelBody>
    </Panel>
  )
}

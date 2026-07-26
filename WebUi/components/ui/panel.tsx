import type { ReactNode } from 'react'
import { cn } from '@/lib/utils'

export function Panel({
  className,
  children,
  glass = false,
}: {
  className?: string
  children: ReactNode
  glass?: boolean
}) {
  return (
    <section
      className={cn(
        'relative rounded-xl border border-border bg-card/60',
        glass && 'glass',
        className,
      )}
    >
      {children}
    </section>
  )
}

export function PanelHeader({
  title,
  icon,
  action,
  className,
}: {
  title: ReactNode
  icon?: ReactNode
  action?: ReactNode
  className?: string
}) {
  return (
    <div
      className={cn(
        'flex items-center justify-between gap-2 border-b border-border px-4 py-3',
        className,
      )}
    >
      <div className="flex items-center gap-2 text-sm font-medium text-foreground">
        {icon ? <span className="text-muted-foreground">{icon}</span> : null}
        {title}
      </div>
      {action ? <div className="flex items-center gap-1">{action}</div> : null}
    </div>
  )
}

export function PanelBody({
  className,
  children,
}: {
  className?: string
  children: ReactNode
}) {
  return <div className={cn('p-4', className)}>{children}</div>
}

export function Kpi({
  label,
  value,
  sub,
  tone,
}: {
  label: string
  value: ReactNode
  sub?: ReactNode
  tone?: 'up' | 'down' | 'neutral'
}) {
  return (
    <div className="flex flex-col gap-1">
      <span className="text-xs text-muted-foreground">{label}</span>
      <span className="tabular text-2xl font-semibold tracking-tight text-foreground">
        {value}
      </span>
      {sub ? (
        <span
          className={cn(
            'tabular text-xs',
            tone === 'up' && 'text-success',
            tone === 'down' && 'text-danger',
            (!tone || tone === 'neutral') && 'text-muted-foreground',
          )}
        >
          {sub}
        </span>
      ) : null}
    </div>
  )
}

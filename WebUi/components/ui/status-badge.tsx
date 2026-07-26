import { cn } from '@/lib/utils'

const toneMap: Record<string, { text: string; bg: string; dot: string }> = {
  success: { text: 'text-success', bg: 'bg-success/10', dot: 'bg-success' },
  warning: { text: 'text-warning', bg: 'bg-warning/10', dot: 'bg-warning' },
  danger: { text: 'text-danger', bg: 'bg-danger/10', dot: 'bg-danger' },
  info: { text: 'text-info', bg: 'bg-info/10', dot: 'bg-info' },
  ai: { text: 'text-ai', bg: 'bg-ai/10', dot: 'bg-ai' },
  muted: { text: 'text-muted-foreground', bg: 'bg-muted', dot: 'bg-muted-foreground' },
}

export function StatusDot({
  token,
  pulse = false,
  className,
}: {
  token: string
  pulse?: boolean
  className?: string
}) {
  const tone = toneMap[token] ?? toneMap.muted
  return (
    <span className={cn('relative inline-flex size-2 shrink-0', className)}>
      {pulse ? (
        <span
          className={cn(
            'absolute inline-flex size-full rounded-full opacity-60 animate-pulse-dot',
            tone.dot,
          )}
        />
      ) : null}
      <span className={cn('relative inline-flex size-2 rounded-full', tone.dot)} />
    </span>
  )
}

export function StatusBadge({
  token,
  label,
  pulse = false,
  className,
}: {
  token: string
  label: string
  pulse?: boolean
  className?: string
}) {
  const tone = toneMap[token] ?? toneMap.muted
  return (
    <span
      className={cn(
        'inline-flex items-center gap-1.5 rounded-md px-2 py-0.5 text-xs font-medium',
        tone.bg,
        tone.text,
        className,
      )}
    >
      <StatusDot token={token} pulse={pulse} />
      {label}
    </span>
  )
}

export function Tag({
  children,
  className,
}: {
  children: React.ReactNode
  className?: string
}) {
  return (
    <span
      className={cn(
        'inline-flex items-center rounded-md border border-border bg-muted/40 px-1.5 py-0.5 font-mono text-[10px] tracking-wide text-muted-foreground uppercase',
        className,
      )}
    >
      {children}
    </span>
  )
}

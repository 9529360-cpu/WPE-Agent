import { cn } from '@/lib/utils'
import { useId } from 'react'

function toPoints(data: number[], w: number, h: number, pad = 2) {
  const min = Math.min(...data)
  const max = Math.max(...data)
  const range = max - min || 1
  const step = (w - pad * 2) / (data.length - 1 || 1)
  return data.map((d, i) => {
    const x = pad + i * step
    const y = h - pad - ((d - min) / range) * (h - pad * 2)
    return [x, y] as const
  })
}

export function Sparkline({
  data,
  color = 'var(--info)',
  className,
  width = 96,
  height = 28,
  fill = false,
}: {
  data: number[]
  color?: string
  className?: string
  width?: number
  height?: number
  fill?: boolean
}) {
  const pts = toPoints(data, width, height)
  const line = pts.map((p) => p.join(',')).join(' ')
  const area = `${pts[0][0]},${height} ${line} ${pts[pts.length - 1][0]},${height}`
  const gid = `sg-${useId().replaceAll(':', '')}`
  return (
    <svg
      viewBox={`0 0 ${width} ${height}`}
      className={cn('overflow-visible', className)}
      preserveAspectRatio="none"
      aria-hidden
    >
      {fill ? (
        <>
          <defs>
            <linearGradient id={gid} x1="0" y1="0" x2="0" y2="1">
              <stop offset="0%" stopColor={color} stopOpacity="0.28" />
              <stop offset="100%" stopColor={color} stopOpacity="0" />
            </linearGradient>
          </defs>
          <polygon points={area} fill={`url(#${gid})`} />
        </>
      ) : null}
      <polyline
        points={line}
        fill="none"
        stroke={color}
        strokeWidth="1.5"
        strokeLinecap="round"
        strokeLinejoin="round"
        vectorEffect="non-scaling-stroke"
      />
    </svg>
  )
}

export function AreaChart({
  data,
  benchmark,
  color = 'var(--info)',
  className,
  height = 220,
}: {
  data: number[]
  benchmark?: number[]
  color?: string
  className?: string
  height?: number
}) {
  const w = 600
  const all = benchmark ? [...data, ...benchmark] : data
  const min = Math.min(...all)
  const max = Math.max(...all)
  const range = max - min || 1
  const map = (arr: number[]) => {
    const step = w / (arr.length - 1 || 1)
    return arr.map((d, i) => {
      const x = i * step
      const y = height - 8 - ((d - min) / range) * (height - 24)
      return [x, y] as const
    })
  }
  const pts = map(data)
  const line = pts.map((p) => p.join(',')).join(' ')
  const area = `0,${height} ${line} ${w},${height}`
  return (
    <svg
      viewBox={`0 0 ${w} ${height}`}
      className={cn('w-full', className)}
      preserveAspectRatio="none"
    >
      <defs>
        <linearGradient id="area-fill" x1="0" y1="0" x2="0" y2="1">
          <stop offset="0%" stopColor={color} stopOpacity="0.3" />
          <stop offset="100%" stopColor={color} stopOpacity="0" />
        </linearGradient>
      </defs>
      {[0.25, 0.5, 0.75].map((g) => (
        <line
          key={g}
          x1="0"
          x2={w}
          y1={height * g}
          y2={height * g}
          stroke="var(--border)"
          strokeWidth="1"
          vectorEffect="non-scaling-stroke"
        />
      ))}
      {benchmark ? (
        <polyline
          points={map(benchmark).map((p) => p.join(',')).join(' ')}
          fill="none"
          stroke="var(--muted-foreground)"
          strokeWidth="1.25"
          strokeDasharray="4 4"
          vectorEffect="non-scaling-stroke"
        />
      ) : null}
      <polygon points={area} fill="url(#area-fill)" />
      <polyline
        points={line}
        fill="none"
        stroke={color}
        strokeWidth="2"
        strokeLinejoin="round"
        vectorEffect="non-scaling-stroke"
      />
    </svg>
  )
}

export function BarSeries({
  data,
  height = 180,
  className,
}: {
  data: { m: string; v: number }[]
  height?: number
  className?: string
}) {
  const max = Math.max(...data.map((d) => Math.abs(d.v))) || 1
  return (
    <div className={cn('flex items-end gap-1.5', className)} style={{ height }}>
      {data.map((d) => {
        const h = (Math.abs(d.v) / max) * (height - 24)
        const up = d.v >= 0
        return (
          <div key={d.m} className="flex flex-1 flex-col items-center gap-1">
            <div className="flex w-full flex-1 items-end justify-center">
              <div
                className={cn('w-full rounded-sm', up ? 'bg-success/70' : 'bg-danger/70')}
                style={{ height: Math.max(2, h) }}
                title={`${d.m}: ${d.v}%`}
              />
            </div>
            <span className="text-[9px] text-muted-foreground">{d.m}</span>
          </div>
        )
      })}
    </div>
  )
}

export function ProgressBar({
  value,
  color = 'var(--info)',
  className,
}: {
  value: number
  color?: string
  className?: string
}) {
  return (
    <div className={cn('h-1.5 w-full overflow-hidden rounded-full bg-muted', className)}>
      <div
        className="h-full rounded-full transition-all"
        style={{ width: `${Math.min(100, Math.max(0, value))}%`, background: color }}
      />
    </div>
  )
}

export function Donut({
  segments,
  size = 132,
  thickness = 14,
}: {
  segments: { value: number; color: string; label: string }[]
  size?: number
  thickness?: number
}) {
  const total = segments.reduce((s, x) => s + x.value, 0) || 1
  const r = (size - thickness) / 2
  const c = 2 * Math.PI * r
  const lengths = segments.map((segment) => (segment.value / total) * c)
  return (
    <svg viewBox={`0 0 ${size} ${size}`} width={size} height={size} aria-hidden>
      <g transform={`rotate(-90 ${size / 2} ${size / 2})`}>
        {segments.map((s, i) => {
          const len = lengths[i]
          const offset = lengths.slice(0, i).reduce((sum, value) => sum + value, 0)
          const dash = `${len} ${c - len}`
          const el = (
            <circle
              key={i}
              cx={size / 2}
              cy={size / 2}
              r={r}
              fill="none"
              stroke={s.color}
              strokeWidth={thickness}
              strokeDasharray={dash}
              strokeDashoffset={-offset}
            />
          )
          return el
        })}
      </g>
    </svg>
  )
}

export function Gauge({
  value,
  max = 100,
  color = 'var(--warning)',
  label,
}: {
  value: number
  max?: number
  color?: string
  label?: string
}) {
  const pct = Math.min(1, value / max)
  const size = 120
  const r = 46
  const c = Math.PI * r // half circle
  return (
    <div className="relative flex flex-col items-center">
      <svg viewBox="0 0 120 70" width={size} height={70} aria-hidden>
        <path
          d="M 14 60 A 46 46 0 0 1 106 60"
          fill="none"
          stroke="var(--muted)"
          strokeWidth="10"
          strokeLinecap="round"
        />
        <path
          d="M 14 60 A 46 46 0 0 1 106 60"
          fill="none"
          stroke={color}
          strokeWidth="10"
          strokeLinecap="round"
          strokeDasharray={c}
          strokeDashoffset={c * (1 - pct)}
        />
      </svg>
      <div className="-mt-6 flex flex-col items-center">
        <span className="tabular text-xl font-semibold text-foreground">{value}</span>
        {label ? <span className="text-[11px] text-muted-foreground">{label}</span> : null}
      </div>
    </div>
  )
}

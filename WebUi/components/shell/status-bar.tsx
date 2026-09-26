'use client'
import {StatusDot} from '@/components/ui/status-badge'
import {useWpeRuntime} from '@/components/runtime-bridge'
import {useI18n} from '@/lib/i18n/context'
import {runtimeLabel} from '@/lib/runtime-labels'

export function StatusBar(){
 const runtime=useWpeRuntime(),{t,formatDate,locale}=useI18n(),fresh=runtime.runtimeFresh===true,running=runtime.agentIsRunning===true
 const zh=locale==='zh_CN',zht=locale==='zh_TW'
 const unavailable=zh?'不可用':zht?'不可用':'unavailable'
 const dataAge=fresh?(zh?`数据 ${Math.round(runtime.runtimeAgeSeconds??0)} 秒前`:zht?`資料 ${Math.round(runtime.runtimeAgeSeconds??0)} 秒前`:`fresh ${Math.round(runtime.runtimeAgeSeconds??0)}s`):(runtime.runtimeDiagnosticReason??(zh?'运行时不可用':zht?'執行狀態不可用':'runtime unavailable'))
 const sourceLabel=zh?'数据源':zht?'資料源':'source'
 const snapshotLabel=zh?'界面快照':zht?'介面快照':'snapshot'
 return <footer className="flex min-h-9 min-w-0 flex-wrap items-center gap-x-3 gap-y-1 overflow-hidden border-t border-border bg-sidebar px-4 py-1 text-[11px] text-muted-foreground">
  <span className="flex shrink-0 items-center gap-1.5 font-medium"><StatusDot token={fresh?'success':'warning'} pulse={fresh}/>{t(fresh?'common.runtimeOnline':'common.runtimeOffline')}</span>
  <span className="truncate">{runtime.runtimeProviderId??unavailable} · {runtimeLabel(runtime.environment,locale)??unavailable}</span>
  <span className="hidden truncate lg:inline">{sourceLabel} {runtime.sourceTimestampUtc?formatDate(runtime.sourceTimestampUtc):unavailable} · {snapshotLabel} {runtime.snapshotTimestampUtc?formatDate(runtime.snapshotTimestampUtc):unavailable}</span>
  <span className={fresh?'text-success':'text-warning'}>{dataAge}</span>
  <span className="ml-auto flex shrink-0 items-center gap-1.5 font-medium"><StatusDot token={running?'success':'muted'} pulse={fresh&&running}/>{t(running?'agents.running':'agents.stopped')}</span>
 </footer>
}
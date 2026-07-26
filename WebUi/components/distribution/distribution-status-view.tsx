'use client'
import {AlertTriangle,FileLock2} from 'lucide-react'
import type {RuntimeCollectionState,RuntimeDistributionCollection} from '@/components/runtime-bridge'
import {Panel,PanelBody,PanelHeader} from '@/components/ui/panel'
import {StatusBadge} from '@/components/ui/status-badge'
export type DistributionProjection=RuntimeDistributionCollection
export const unsupportedDistributionProjection:DistributionProjection={state:'unsupported',message:'The current host runtime does not expose professional distribution status.'}
const tone:Record<RuntimeCollectionState,string>={available:'success',unsupported:'muted',stale:'warning',error:'danger'}
const copy:Record<RuntimeCollectionState,string>={available:'Distribution evidence is unavailable.',unsupported:'Professional distribution status is not supported by the host.',stale:'Distribution evidence is stale and hidden.',error:'Distribution evidence could not be validated.'}
function time(value:string|null|undefined){if(!value)return'Not provided';const parsed=Date.parse(value);return Number.isNaN(parsed)?value:new Date(parsed).toLocaleString()}
function hash(value:string|null){return value?`${value.slice(0,12)}...`:'Not provided'}
export function DistributionStatusView({projection}:{projection:DistributionProjection}){
 const value=projection.state==='available'&&projection.value?.status!=='Error'?projection.value:undefined
 const displayState=projection.state==='available'&&!value?'error':projection.state
 return <div className="min-w-0 space-y-5">
  <div className="grid gap-3 sm:grid-cols-3" role="status" aria-live="polite" aria-atomic="true">
   <Metric label="Projection" value={projection.state} badge={tone[projection.state]}/>
   <Metric label="Decision" value={value?.status??'Denied'} badge={value?.status==='Authorized'?'success':value?.status==='Error'?'danger':'warning'}/>
   <Metric label="As of" value={time(value?.asOfUtc)}/>
  </div>
  {displayState!=='available'||!value?<Panel><PanelBody className="flex min-h-52 flex-col items-center justify-center p-6 text-center"><div role={displayState==='error'?'alert':'status'} aria-live={displayState==='error'?'assertive':'polite'} aria-atomic="true" className="flex flex-col items-center"><AlertTriangle aria-hidden="true" className="mb-3 size-5 text-warning"/><h2 className="text-sm font-medium">Distribution projection {displayState}</h2><p className="mt-2 max-w-xl [overflow-wrap:anywhere] text-xs leading-5 text-muted-foreground">{projection.message||copy[displayState]}</p></div></PanelBody></Panel>
  :<Panel className="min-w-0 overflow-hidden"><PanelHeader icon={<FileLock2 className="size-4"/>} title={<h2 className="text-sm font-medium">Approval evidence</h2>} action={<span className="text-xs text-muted-foreground">Read only</span>}/><PanelBody><dl className="grid gap-3 text-xs sm:grid-cols-2 lg:grid-cols-4"><Cell label="Allowed" value={value.allowed?'Yes':'No'}/><Cell label="Withdrawn" value={value.withdrawn?'Yes':'No'}/><Cell label="Approval roles" value={value.approvalRoles.length?value.approvalRoles.join(' / '):'None'}/><Cell label="Earliest validity" value={time(value.validUntilUtc)}/><Cell label="Receipt hash" value={hash(value.receiptHash)} mono/><Cell label="Policy hash" value={hash(value.policyHash)} mono/><Cell label="Content facts hash" value={hash(value.contentFactsHash)} mono/><Cell label="Audit hash" value={hash(value.auditCorrelationHash)} mono/><Cell label="Consent scope hash" value={hash(value.consentScopeHash)} mono/><Cell label="Consent version" value={value.consentVersion??'Not provided'}/><Cell label="Suitability version" value={value.suitabilityVersion??'Not provided'}/><div className="sm:col-span-2 lg:col-span-4"><dt className="text-muted-foreground">Reason codes</dt><dd className="mt-1 flex flex-wrap gap-1">{value.reasonCodes.length?value.reasonCodes.map(code=><span key={code} className="max-w-full rounded-md bg-muted px-1.5 py-0.5 font-mono text-[10px] [overflow-wrap:anywhere]">{code}</span>):'None'}</dd></div></dl></PanelBody></Panel>}
 </div>
}
function Metric({label,value,badge}:{label:string;value:string;badge?:string}){return <div className="rounded-lg border border-border bg-panel/40 p-3"><div className="text-xs text-muted-foreground">{label}</div><div className="mt-2">{badge?<StatusBadge token={badge} label={value}/>:<span className="text-sm font-medium [overflow-wrap:anywhere]">{value}</span>}</div></div>}
function Cell({label,value,mono=false}:{label:string;value:string;mono?:boolean}){return <div className="min-w-0"><dt className="text-muted-foreground">{label}</dt><dd className={`${mono?'font-mono text-[11px] ':''}[overflow-wrap:anywhere]`}>{value}</dd></div>}

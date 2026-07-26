'use client'
import{AlertTriangle,DatabaseZap,ShieldCheck}from'lucide-react'
import type{RuntimeSecurityStorageCollection}from'@/components/runtime-bridge'
import{Panel,PanelBody,PanelHeader}from'@/components/ui/panel'
import{StatusBadge}from'@/components/ui/status-badge'
export type SecurityProjection=RuntimeSecurityStorageCollection
export const unsupportedSecurityProjection:SecurityProjection={state:'unsupported',message:'The current host runtime does not expose security storage status.'}
const collectionTone={available:'success',unsupported:'muted',stale:'warning',error:'danger'}as const
const runtimeTone={Unknown:'muted',Ready:'success',Committed:'success',Failed:'danger'}as const
export function SecurityStatusView({projection}:{projection:SecurityProjection}){
 const value=projection.state==='available'?projection.value:undefined
 if(!value)return <Panel><PanelBody className="flex min-h-52 flex-col items-center justify-center p-6 text-center"><AlertTriangle aria-hidden="true" className="mb-3 size-5 text-warning"/><h2 className="text-sm font-medium">Security storage {projection.state}</h2><p className="mt-2 max-w-xl text-xs leading-5 text-muted-foreground">{projection.message??'Sanitized security storage status is unavailable.'}</p></PanelBody></Panel>
 return <Panel><PanelHeader icon={<ShieldCheck className="size-4"/>} title={<h2 className="text-sm font-medium">Security storage</h2>} action={<span className="text-xs text-muted-foreground">Read only</span>}/><PanelBody><div className="mb-4 flex items-center gap-2"><StatusBadge token={collectionTone[projection.state]} label={projection.state}/><StatusBadge token={runtimeTone[value.state]} label={value.state}/></div><dl className="grid gap-4 text-xs sm:grid-cols-2 lg:grid-cols-4"><div><dt className="text-muted-foreground">Reason</dt><dd className="mt-1 break-words font-mono text-[11px]">{value.reasonCode}</dd></div><div><dt className="text-muted-foreground">Envelope version</dt><dd className="mt-1">{value.envelopeVersion}</dd></div><div><dt className="text-muted-foreground">Record count</dt><dd className="mt-1">{value.recordCount}</dd></div><div><dt className="text-muted-foreground">Evidence hash</dt><dd className="mt-1 break-all font-mono text-[11px]">{value.evidenceSha256??'Not available'}</dd></div></dl>{value.recordCount===0&&<div className="mt-5 flex items-center gap-2 border-t border-border pt-4 text-xs text-muted-foreground"><DatabaseZap className="size-4" aria-hidden="true"/>No committed record evidence.</div>}</PanelBody></Panel>
}

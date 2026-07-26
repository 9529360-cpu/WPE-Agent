'use client'
import {Panel,PanelBody} from '@/components/ui/panel'
import {useI18n} from '@/lib/i18n/context'
export function RuntimeUnavailable({stale=false,subject}:{stale?:boolean;subject?:string}){const{t}=useI18n(),name=subject??t('common.empty');return <Panel><PanelBody className="p-8 text-center"><div className="text-sm font-medium">{stale?t('runtime.staleTitle'):t('runtime.missingTitle',{subject:name})}</div><p className="mt-2 text-xs text-muted-foreground">{stale?t('runtime.staleBody'):t('runtime.missingBody',{subject:name})}</p></PanelBody></Panel>}
export function RuntimeMetric({label,value}:{label:string;value?:string|number|null}){const{t}=useI18n();return <div className="min-w-0 rounded-lg border border-border bg-panel/40 p-3"><div className="break-words text-xs text-muted-foreground">{label}</div><div className="mt-1 break-words text-sm font-medium">{value===undefined||value===null||value===''?t('runtime.valueMissing'):value}</div></div>}

'use client'
import {Cpu} from 'lucide-react';import {Panel,PanelBody,PanelHeader} from '@/components/ui/panel';import {useI18n} from '@/lib/i18n/context'
export function ResourceMonitor(){const{t}=useI18n();return <Panel><PanelHeader icon={<Cpu className="size-4"/>} title={t('dashboard.resources')}/><PanelBody className="p-6 text-center text-xs text-muted-foreground">{t('dashboard.resourcesMissing')}</PanelBody></Panel>}

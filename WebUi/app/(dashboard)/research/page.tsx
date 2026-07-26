'use client'
import {PageHeader} from '@/components/shell/page-header'
import {useWpeRuntime} from '@/components/runtime-bridge'
import {ResearchStatusView,unsupportedResearchProjection} from '@/components/research/research-status-view'
export default function ResearchPage(){const runtime=useWpeRuntime();return <div className="flex min-w-0 flex-col gap-5"><PageHeader title="Cross-asset research" description="Host-authoritative read-only validation evidence"/><ResearchStatusView projection={runtime.research??unsupportedResearchProjection}/></div>}

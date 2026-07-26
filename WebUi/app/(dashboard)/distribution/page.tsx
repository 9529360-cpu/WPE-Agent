'use client'
import {PageHeader} from '@/components/shell/page-header';import {useWpeRuntime} from '@/components/runtime-bridge';import {DistributionStatusView,unsupportedDistributionProjection} from '@/components/distribution/distribution-status-view'
export default function DistributionPage(){const runtime=useWpeRuntime();return <div className="flex min-w-0 flex-col gap-5"><PageHeader title="Professional distribution" description="Host-authoritative approval and version-binding evidence"/><DistributionStatusView projection={runtime.distribution??unsupportedDistributionProjection}/></div>}

'use client'
import{PageHeader}from'@/components/shell/page-header';import{useWpeRuntime}from'@/components/runtime-bridge';import{SecurityStatusView,unsupportedSecurityProjection}from'@/components/security/security-status-view'
export default function SecurityPage(){const runtime=useWpeRuntime();return <div className="flex min-w-0 flex-col gap-5"><PageHeader title="Security storage" description="Sanitized, host-authoritative storage status"/><SecurityStatusView projection={runtime.securityStorage??unsupportedSecurityProjection}/></div>}

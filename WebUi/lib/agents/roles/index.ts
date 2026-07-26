import roleRegistry from './role-registry.json'

export type RoleMode = 'deterministic' | 'optional-llm'
export type RoleRuntime = 'local'

export type TypedAuthorization = {
  machineEvidenceGate: string[]
  machineRiskAuthorization: string[]
  machineExecutionEligibility: string[]
  automaticSafetyAction: string[]
  humanLifecycleGovernance: string[]
  humanExternalPublicationReview: string[]
}

export type RegistryRoleDefinition = {
  id: string
  name: string
  mode: RoleMode
  runtime: RoleRuntime
  optionalLlm: boolean
  deterministicFallback: string
  allowedActions: string[]
  forbiddenActions: string[]
  typedAuthorization: TypedAuthorization
  inputs: string[]
  outputs: string[]
  mustRouteThrough?: string[]
}

export type RoleDefinition = RegistryRoleDefinition

export type RoleRegistry = {
  version: string
  localOnly: true
  privateTestnetPerOrderHumanApproval: false
  roles: RegistryRoleDefinition[]
}

export const ROLE_IDS = [
  'orchestrator',
  'data-quality',
  'news-ingest',
  'strategy-research',
  'backtest',
  'risk',
  'execution',
  'order-recovery',
] as const

export const roleRegistryData = roleRegistry as RoleRegistry

export function validateRoleRegistry(registry: RoleRegistry = roleRegistryData): string[] {
  const errors: string[] = []
  const seen = new Set<string>()

  if (!registry.localOnly) errors.push('registry must be localOnly')

  for (const role of registry.roles) {
    if (!ROLE_IDS.includes(role.id as (typeof ROLE_IDS)[number])) errors.push(`unknown role: ${role.id}`)
    if (seen.has(role.id)) errors.push(`duplicate role: ${role.id}`)
    seen.add(role.id)

    if (role.runtime !== 'local') errors.push(`${role.id} runtime must be local`)
    if (!role.deterministicFallback) errors.push(`${role.id} missing deterministicFallback`)
    if (role.allowedActions.some((action) => action === 'trade' || action === 'submitOrder')) {
      errors.push(`${role.id} cannot be granted direct trading actions`)
    }
    if (role.id === 'execution') {
      const requiredPath = role.mustRouteThrough ?? []
      if (!requiredPath.includes('Risk Gate') || !requiredPath.includes('ReliableOrderExecutor')) {
        errors.push('execution must route through Risk Gate and ReliableOrderExecutor')
      }
      if (role.allowedActions.includes('direct-exchange-submit')) {
        errors.push('execution cannot bypass ReliableOrderExecutor')
      }
    }
    if (role.id === 'risk' && role.allowedActions.includes('submit-via-executor')) {
      errors.push('risk cannot submit orders')
    }
  }

  for (const id of ROLE_IDS) {
    if (!seen.has(id)) errors.push(`missing role: ${id}`)
  }

  return errors
}

export function getRoleDirectory(): RoleDefinition[] {
  return roleRegistryData.roles
}

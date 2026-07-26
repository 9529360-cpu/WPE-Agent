import assert from 'node:assert/strict'
import test from 'node:test'
import { readFile } from 'node:fs/promises'

const registryUrl = new URL('../../lib/agents/roles/role-registry.json', import.meta.url)
const contractUrl = new URL('../../lib/agents/roles/permissions.contract.json', import.meta.url)
const adapterUrl = new URL('../../lib/agents/roles/index.ts', import.meta.url)
const agentsPageUrl = new URL('../../app/(dashboard)/agents/page.tsx', import.meta.url)
const registry = JSON.parse(await readFile(registryUrl, 'utf8'))
const contract = JSON.parse(await readFile(contractUrl, 'utf8'))
const adapterSource = await readFile(adapterUrl, 'utf8')
const agentsPageSource = await readFile(agentsPageUrl, 'utf8')

test('role registry is local only and complete', () => {
  assert.equal(registry.localOnly, true)
  assert.equal(registry.roles.length, 8)
  const ids = registry.roles.map((role) => role.id).sort()
  assert.deepEqual(ids, [
    'backtest',
    'data-quality',
    'execution',
    'news-ingest',
    'orchestrator',
    'order-recovery',
    'risk',
    'strategy-research',
  ])
})

test('role authorization is typed and private Testnet has no per-order human approval', () => {
  assert.equal(registry.privateTestnetPerOrderHumanApproval, false)
  assert.equal(JSON.stringify(registry).includes('approvalRequired'), false)
  assert.equal(JSON.stringify(contract).includes('approvalRequired'), false)

  const requiredTypes = contract.properties.roles.items.properties.typedAuthorization.required
  for (const role of registry.roles) {
    assert.deepEqual(Object.keys(role.typedAuthorization).sort(), [...requiredTypes].sort())
    for (const actions of Object.values(role.typedAuthorization)) {
      assert.ok(Array.isArray(actions))
      for (const action of actions) assert.ok(role.allowedActions.includes(action))
    }
  }
})

test('production model and Agent directory preserve distinct authorization categories', () => {
  assert.equal(adapterSource.includes('approvalRequired'), false)
  assert.equal(agentsPageSource.includes('approvalRequired'), false)
  assert.equal(agentsPageSource.includes("t('common.approval')"), false)

  for (const category of contract.properties.roles.items.properties.typedAuthorization.required) {
    assert.ok(agentsPageSource.includes(`authorization.${category}`), `missing Agent directory category: ${category}`)
  }
})

test('risk and execution authorization is machine-only', () => {
  const risk = registry.roles.find((role) => role.id === 'risk')
  const execution = registry.roles.find((role) => role.id === 'execution')

  assert.deepEqual(risk.typedAuthorization.machineRiskAuthorization, ['allow'])
  assert.deepEqual(execution.typedAuthorization.machineExecutionEligibility, ['submit-via-executor'])
  assert.ok(!risk.typedAuthorization.humanLifecycleGovernance.includes('allow'))
  assert.ok(!execution.typedAuthorization.humanLifecycleGovernance.includes('submit-via-executor'))
  assert.ok(!execution.typedAuthorization.humanExternalPublicationReview.includes('submit-via-executor'))
})

test('safety actions are immediate and lifecycle governance cannot authorize orders', () => {
  const immediateSafetyActions = new Set()
  const orderActions = new Set(['allow', 'submit-via-executor', 'trade', 'submitOrder'])

  for (const role of registry.roles) {
    for (const action of role.typedAuthorization.automaticSafetyAction) immediateSafetyActions.add(action)
    for (const action of role.typedAuthorization.humanLifecycleGovernance) {
      assert.ok(!orderActions.has(action), `lifecycle governance cannot authorize ${action}`)
    }
    for (const action of role.typedAuthorization.humanExternalPublicationReview) {
      assert.ok(!orderActions.has(action), `publication review cannot authorize ${action}`)
    }
  }

  assert.ok(immediateSafetyActions.has('block'))
  assert.ok(immediateSafetyActions.has('halt'))
  assert.ok(immediateSafetyActions.has('degrade'))
})

test('no role can bypass risk gate or reliable order executor', () => {
  const execution = registry.roles.find((role) => role.id === 'execution')
  const risk = registry.roles.find((role) => role.id === 'risk')

  assert.ok(execution)
  assert.ok(risk)
  assert.ok(execution.mustRouteThrough.includes('ReliableOrderExecutor'))
  assert.ok(execution.mustRouteThrough.includes('Risk Gate'))
  assert.ok(!execution.allowedActions.includes('direct-exchange-submit'))
  assert.ok(!risk.allowedActions.includes('submit-via-executor'))
})

test('every role has a deterministic fallback and no direct trading permission', () => {
  for (const role of registry.roles) {
    assert.equal(typeof role.deterministicFallback, 'string')
    assert.notEqual(role.deterministicFallback.length, 0)
    assert.ok(!role.allowedActions.includes('trade'))
    assert.ok(!role.allowedActions.includes('submitOrder'))
  }
})

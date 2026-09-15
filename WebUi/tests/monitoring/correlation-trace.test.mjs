import assert from 'node:assert/strict'
import test from 'node:test'
import { readFile } from 'node:fs/promises'

const monitoringUrl = new URL('../../app/(dashboard)/monitoring/page.tsx', import.meta.url)
const monitoringSource = await readFile(monitoringUrl, 'utf8')

test('monitoring correlation trace is derived only from sanitized available audit events', () => {
  assert.match(monitoringSource, /RuntimeAuditEventV1/)
  assert.match(monitoringSource, /runtime\.auditEvents\?\.state === 'available'/)
  assert.match(monitoringSource, /correlationId/)
  assert.match(monitoringSource, /buildTraceGroups/)
  assert.match(monitoringSource, /selectedCorrelation/)
  assert.match(monitoringSource, /setSelectedCorrelation/)
  assert.doesNotMatch(monitoringSource, /payloadJson|payload_json/)
  assert.doesNotMatch(monitoringSource, /causationId|causation_id/)
})

test('trace view keeps the full audit timeline as a separate read-only surface', () => {
  assert.match(monitoringSource, /title=\{t\('monitoring\.correlation'\)\}/)
  assert.match(monitoringSource, /title=\{t\('monitoring\.audit'\)\}/)
  assert.match(monitoringSource, /activeTrace\.events/)
  assert.match(monitoringSource, /event\.summary/)
  assert.match(monitoringSource, /audit\.map/)
})

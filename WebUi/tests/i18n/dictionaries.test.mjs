import test from 'node:test'
import assert from 'node:assert/strict'
import fs from 'node:fs'
import path from 'node:path'
import vm from 'node:vm'
import ts from 'typescript'

const root=path.resolve(import.meta.dirname,'../..')
const source=fs.readFileSync(path.join(root,'lib/i18n/dictionaries.ts'),'utf8')
const output=ts.transpileModule(source,{compilerOptions:{module:ts.ModuleKind.CommonJS,target:ts.ScriptTarget.ES2022}}).outputText
const compiled={exports:{}}
vm.runInNewContext(`(function(exports,module){${output}\n})(compiled.exports,compiled)`,{compiled})
const {locales,localeMeta,translate,explicitlyLocalizedKeys}=compiled.exports

test('six locales expose native names and valid html language tags',()=>{
  assert.deepEqual([...locales],['zh_CN','zh_TW','en_US','ja_JP','ko_KR','it_IT'])
  for(const locale of locales){assert.ok(localeMeta[locale].name);assert.match(localeMeta[locale].htmlLang,/^[a-z]{2}(?:-[A-Z]{2})?$/)}
})

test('critical page copy is explicitly localized instead of silently falling back to English',()=>{
  assert.ok(explicitlyLocalizedKeys.length>=70)
  const invariantTerms=new Set(['monitoring.tokens'])
  for(const locale of locales.filter(value=>value!=='en_US'))for(const key of explicitlyLocalizedKeys){const value=translate(locale,key);assert.notEqual(value,key,`${locale}:${key} returned raw key`);if(!invariantTerms.has(key))assert.notEqual(value,translate('en_US',key),`${locale}:${key} fell back to English`)}
})

test('reachable pages use the typed i18n context and contain no mojibake',()=>{
  const files=['app/(dashboard)/page.tsx','app/(dashboard)/agents/page.tsx','app/(dashboard)/teacher/page.tsx','app/(dashboard)/settings/page.tsx','app/(dashboard)/orders/page.tsx','app/(dashboard)/backtest/page.tsx','app/(dashboard)/risk/page.tsx','app/(dashboard)/plugins/page.tsx','app/(dashboard)/monitoring/page.tsx']
  for(const file of files){const text=fs.readFileSync(path.join(root,file),'utf8');assert.match(text,/useI18n/);assert.doesNotMatch(text,/[鍑锵鏈鏇鐨閲]/,`${file} contains mojibake`);assert.doesNotMatch(text,/<PageHeader\s+title=["']/,`${file} hardcodes a page title`)}
})

test('dashboard root and shell do not fork the component tree by locale',()=>{
  const page=fs.readFileSync(path.join(root,'app/(dashboard)/page.tsx'),'utf8')
  const layout=fs.readFileSync(path.join(root,'app/(dashboard)/layout.tsx'),'utf8')
  assert.doesNotMatch(page,/locale\s*===\s*['"]zh_CN['"]/)
  assert.doesNotMatch(page,/WpeConsole/)
  assert.doesNotMatch(layout,/locale\s*===\s*['"]zh_CN['"]/)
  assert.doesNotMatch(layout,/isConsoleRoot/)
  assert.match(page,/useI18n/)
  assert.match(layout,/<Sidebar\s*\/>/)
  assert.match(layout,/<Topbar\s*\/>/)
  assert.match(layout,/<StatusBar\s*\/>/)
})

test('converged route tree preserves accepted Teacher, notification, settings and Agent controls',()=>{
  const teacher=fs.readFileSync(path.join(root,'app/(dashboard)/teacher/page.tsx'),'utf8')
  const settings=fs.readFileSync(path.join(root,'app/(dashboard)/settings/page.tsx'),'utf8')
  const agents=fs.readFileSync(path.join(root,'app/(dashboard)/agents/page.tsx'),'utf8')
  const hostCommand=fs.readFileSync(path.join(root,'lib/host-command.ts'),'utf8')
  const nav=fs.readFileSync(path.join(root,'lib/nav.ts'),'utf8')

  for(const field of ['teacherLessons','teacherRecommendations','teacherCorrections','teacherOutcomes'])assert.match(teacher,new RegExp(field))
  assert.match(teacher,/executionAuthority=false/)
  assert.doesNotMatch(teacher,/postHostCommand|postMessage|submitOrder|direct-exchange-submit/)

  assert.match(settings,/postHostCommand/)
  assert.match(settings,/open-settings/)
  assert.match(settings,/open-notification-settings/)
  assert.match(settings,/notificationOutbox/)
  assert.match(settings,/telegramSubscribers/)
  assert.doesNotMatch(settings,/\.postMessage\s*\(/)
  assert.doesNotMatch(settings,/postMessage\(\{type:['"](?:approve|confirm)/)

  assert.match(agents,/postHostCommand/)
  assert.match(agents,/agentStartAllowed/)
  assert.match(agents,/agentStopAllowed/)
  assert.doesNotMatch(agents,/\.postMessage\s*\(/)

  for(const command of ['open-settings','open-notification-settings','agent-start','agent-stop'])assert.ok(hostCommand.includes(`'${command}'`),`host bridge is missing allowed command: ${command}`)
  assert.doesNotMatch(hostCommand,/place-order|approve|confirm|submitOrder|direct-exchange-submit/)

  for(const route of ['/teacher','/backtest','/plugins','/security'])assert.ok(nav.includes(`href:'${route}'`),`missing converged route ${route}`)
  for(const unaccepted of ['/equities','/distribution','/research'])assert.ok(!nav.includes(`href:'${unaccepted}'`),`unaccepted route exposed in primary navigation: ${unaccepted}`)
})

test('authorization copy and read-only approval projection stay complete',()=>{
  const keys=['settings.authorizationHelp','settings.authorizationStale','settings.authorizationError','settings.authorizationUnsupported','settings.modeResearch','settings.modeSignal','settings.modeReview','settings.modeAutoTestnet','settings.approvalId','settings.created','settings.reasonCode','settings.statusPending','settings.statusRevoked','settings.statusExpired','settings.statusArtifactUnavailable']
  for(const locale of locales.filter(value=>value!=='en_US'))for(const key of keys){const value=translate(locale,key);assert.notEqual(value,key,`${locale}:${key} returned raw key`);assert.notEqual(value,translate('en_US',key),`${locale}:${key} fell back to English`)}
  const page=fs.readFileSync(path.join(root,'app/(dashboard)/settings/page.tsx'),'utf8')
  const preview=fs.readFileSync(path.join(root,'lib/runtime-preview.development.ts'),'utf8')
  assert.doesNotMatch(page,/runtime\.loggedInUser/)
  assert.doesNotMatch(page,/postMessage\(\{type:['"](?:approve|confirm)/)
  assert.match(page,/max-w-full overflow-x-auto/)
  assert.match(page,/approval\.approvalId/)
  assert.match(preview,/approval#[A-Fa-f0-9]{12}/)
})

test('autonomous Settings queue and legacy history fields are localized in all six locales',()=>{
  const keys=['settings.modeLegacyReadOnly','settings.autoEnabledAt','settings.riskGate','settings.automaticQueue','settings.automaticQueueEmpty','settings.automaticQueueUnavailable','settings.executionId','settings.isolationReason','settings.legacyApprovalHistory','settings.legacyHistoryEmpty','settings.legacyHistoryUnavailable','settings.statusHistorical']
  for(const locale of locales)for(const key of keys){
    const value=translate(locale,key)
    assert.notEqual(value,key,`${locale}:${key} returned raw key`)
    assert.ok(value.trim(),`${locale}:${key} returned empty copy`)
    if(locale!=='en_US')assert.notEqual(value,translate('en_US',key),`${locale}:${key} fell back to English`)
  }
})

test('authorization help preserves private Testnet automatic fail-closed and read-only history semantics',()=>{
  const semanticTerms={
    en_US:[/Private Testnet/,/Risk Gate/,/automatic queue/,/fail-closed/,/read-only history/],
    zh_CN:[/私人 Testnet/,/Risk Gate/,/自动队列/,/fail-closed/,/只读历史/],
    zh_TW:[/私人 Testnet/,/Risk Gate/,/自動佇列/,/fail-closed/,/唯讀歷史/],
    ja_JP:[/プライベート Testnet/,/Risk Gate/,/自動キュー/,/fail-closed/,/読み取り専用の履歴/],
    ko_KR:[/개인 Testnet/,/Risk Gate/,/자동 대기열/,/fail-closed/,/읽기 전용 이력/],
    it_IT:[/Testnet private/,/Risk Gate/,/coda automatica/,/fail-closed/,/cronologia in sola lettura/],
  }
  for(const locale of locales){const value=translate(locale,'settings.authorizationHelp');for(const term of semanticTerms[locale])assert.match(value,term,`${locale}:authorizationHelp missing ${term}`)}
})

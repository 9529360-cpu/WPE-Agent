import assert from'node:assert/strict'
import test from'node:test'
import{renderToStaticMarkup}from'react-dom/server'
import{SecurityStatusView,type SecurityProjection}from'./security-status-view'
const render=(projection:SecurityProjection)=>renderToStaticMarkup(<SecurityStatusView projection={projection}/>)
test('fails closed for unsupported stale and error',()=>{for(const state of['unsupported','stale','error']as const)assert.match(render({state,message:'sanitized'}),new RegExp(state))})
test('renders only sanitized storage status',()=>{const projection:SecurityProjection={state:'available',value:{state:'Committed',reasonCode:'security-storage.restore-committed',envelopeVersion:1,recordCount:3,evidenceSha256:'A'.repeat(64)}};const html=render(projection);assert.match(html,/restore-committed/);assert.match(html,/Envelope version/);assert.match(html,/Record count/);assert.match(html,/A{64}/);assert.doesNotMatch(html,/<button|keyId|databasePath|innerException|ciphertext/i)})
test('renders unknown without inventing evidence',()=>{const html=render({state:'available',value:{state:'Unknown',reasonCode:'security-storage.not-run',envelopeVersion:1,recordCount:0,evidenceSha256:null}});assert.match(html,/not-run/);assert.match(html,/Not available/);assert.doesNotMatch(html,/<button/)})

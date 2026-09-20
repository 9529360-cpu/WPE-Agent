export type HostCommand='open-settings'|'open-notification-settings'|'agent-start'|'agent-stop'
export type HistoricalCollectionKey='orders'|'equity'|'backtests'|'skillCalls'|'auditEvents'|'postTradeReviews'|'reconciliations'
type HostMessage={type:HostCommand}|{type:'history-page';requestId:string;collection:HistoricalCollectionKey;cursor:string}

function postMessage(message:HostMessage):boolean{
  const bridge=(window as Window&{chrome?:{webview?:{postMessage:(message:HostMessage)=>void}}}).chrome?.webview
  if(!bridge)return false
  bridge.postMessage(message)
  return true
}

export function postHostCommand(type:HostCommand):boolean{return postMessage({type})}

export function postHistoryPageRequest(requestId:string,collection:HistoricalCollectionKey,cursor:string):boolean{
  if(!/^[A-Za-z0-9_-]{1,64}$/.test(requestId)||cursor.length<1||cursor.length>2048)return false
  return postMessage({type:'history-page',requestId,collection,cursor})
}

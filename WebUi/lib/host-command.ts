export type HostCommand='open-settings'|'open-notification-settings'|'agent-start'|'agent-stop'

export function postHostCommand(type:HostCommand):boolean{
  const bridge=(window as Window&{chrome?:{webview?:{postMessage:(message:{type:HostCommand})=>void}}}).chrome?.webview
  if(!bridge)return false
  bridge.postMessage({type})
  return true
}

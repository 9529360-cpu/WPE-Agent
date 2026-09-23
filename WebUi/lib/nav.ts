import type {LucideIcon} from 'lucide-react'
import {LayoutDashboard,Bot,Receipt,History,ShieldAlert,Activity,Settings,WalletCards,FlaskConical,Plug,ShieldCheck} from 'lucide-react'
import type {Locale,TranslationKey} from '@/lib/i18n/dictionaries'

export type NavItem={labelKey?:TranslationKey;labels?:Record<Locale,string>;href:string;icon:LucideIcon;shortcut?:string;badge?:string}
export type NavGroup={titleKey:TranslationKey;items:NavItem[]}

type Translator=(key:TranslationKey)=>string

export function navItemLabel(item:NavItem,locale:Locale,t:Translator){
 return item.labels?.[locale]??(item.labelKey?t(item.labelKey):item.href)
}

const securityLabels:Record<Locale,string>={
 zh_CN:'安全存储',zh_TW:'安全儲存',en_US:'Security storage',ja_JP:'安全ストレージ',ko_KR:'보안 저장소',it_IT:'Archiviazione sicura',
}

export const navGroups:NavGroup[]=[
 {titleKey:'nav.command',items:[
  {labelKey:'nav.dashboard',href:'/',icon:LayoutDashboard,shortcut:'D'},
  {labelKey:'nav.agents',href:'/agents',icon:Bot,shortcut:'A'},
  {labelKey:'nav.orders',href:'/orders',icon:Receipt,shortcut:'O'},
  {labelKey:'dashboard.portfolio',href:'/positions',icon:WalletCards},
 ]},
 {titleKey:'nav.analysisRiskGroup',items:[
  {labelKey:'nav.backtest',href:'/backtest',icon:FlaskConical},
  {labelKey:'nav.risk',href:'/risk',icon:ShieldAlert,shortcut:'R'},
 ]},
 {titleKey:'nav.system',items:[
  {labelKey:'dashboard.equityHistory',href:'/history',icon:History},
  {labelKey:'nav.plugins',href:'/plugins',icon:Plug},
  {labels:securityLabels,href:'/security',icon:ShieldCheck},
  {labelKey:'nav.monitoring',href:'/monitoring',icon:Activity,shortcut:'M'},
  {labelKey:'nav.settings',href:'/settings',icon:Settings},
 ]},
]
export const flatNav=navGroups.flatMap(group=>group.items)

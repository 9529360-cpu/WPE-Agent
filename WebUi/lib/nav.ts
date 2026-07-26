import type {LucideIcon} from 'lucide-react'
import {LayoutDashboard,Bot,Receipt,Workflow,History,ShieldAlert,Activity,Settings,WalletCards} from 'lucide-react'
import type {TranslationKey} from '@/lib/i18n/dictionaries'
export type NavItem={labelKey:TranslationKey;href:string;icon:LucideIcon;shortcut?:string;badge?:string}
export type NavGroup={titleKey:TranslationKey;items:NavItem[]}
export const navGroups:NavGroup[]=[
 {titleKey:'nav.command',items:[{labelKey:'nav.dashboard',href:'/',icon:LayoutDashboard,shortcut:'D'},{labelKey:'nav.agents',href:'/agents',icon:Bot,shortcut:'A'},{labelKey:'nav.orders',href:'/orders',icon:Receipt,shortcut:'O'},{labelKey:'dashboard.portfolio',href:'/positions',icon:WalletCards}]},
 {titleKey:'nav.strategyGroup',items:[{labelKey:'nav.strategies',href:'/strategies',icon:Workflow,shortcut:'S'},{labelKey:'nav.risk',href:'/risk',icon:ShieldAlert,shortcut:'R'}]},
 {titleKey:'nav.system',items:[{labelKey:'dashboard.equityHistory',href:'/history',icon:History},{labelKey:'nav.monitoring',href:'/monitoring',icon:Activity,shortcut:'M'},{labelKey:'nav.settings',href:'/settings',icon:Settings}]},
]
export const flatNav=navGroups.flatMap(group=>group.items)

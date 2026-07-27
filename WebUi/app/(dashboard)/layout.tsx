'use client'

import type { ReactNode } from 'react'
import { usePathname } from 'next/navigation'
import { Sidebar } from '@/components/shell/sidebar'
import { StatusBar } from '@/components/shell/status-bar'
import { Topbar } from '@/components/shell/topbar'
import { useI18n } from '@/lib/i18n/context'
import { RuntimeEvidenceStrip } from './_components/runtime-evidence-strip'

export default function DashboardLayout({ children }: { children: ReactNode }) {
  const pathname = usePathname()
  const { locale } = useI18n()
  if (pathname === '/' && locale === 'zh_CN') return children

  return (
    <div className="flex h-dvh overflow-hidden bg-background">
      <Sidebar />
      <div className="flex min-w-0 flex-1 flex-col">
        <Topbar />
        <main className="scrollbar-thin flex-1 overflow-y-auto grid-noise">
          <div className="mx-auto max-w-[1600px] p-4 lg:p-6">
            <RuntimeEvidenceStrip />
            {children}
          </div>
        </main>
        <StatusBar />
      </div>
    </div>
  )
}

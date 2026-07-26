import type { Metadata, Viewport } from 'next'
import { Geist, Geist_Mono } from 'next/font/google'
import './globals.css'
import { RuntimeBridge } from '@/components/runtime-bridge'
import { I18nProvider } from '@/lib/i18n/context'

const geistSans = Geist({
  subsets: ['latin'],
  variable: '--font-geist-sans',
})
const geistMono = Geist_Mono({
  subsets: ['latin'],
  variable: '--font-geist-mono',
})

export const metadata: Metadata = {
  title: 'WPE Agent | Trading Command Center',
  description: 'Local-first trading Agent command center for markets, decisions, risk, and execution state.',
  icons: {
    icon: [
      { url: '/icon-light-32x32.png', media: '(prefers-color-scheme: light)' },
      { url: '/icon-dark-32x32.png', media: '(prefers-color-scheme: dark)' },
      { url: '/icon.svg', type: 'image/svg+xml' },
    ],
    apple: '/apple-icon.png',
  },
}

export const viewport: Viewport = {
  colorScheme: 'dark',
  themeColor: '#0b0d10',
}

export default function RootLayout({ children }: Readonly<{ children: React.ReactNode }>) {
  return (
    <html lang="zh-CN" className={`dark ${geistSans.variable} ${geistMono.variable}`}>
      <body className="bg-background font-sans antialiased">
        <I18nProvider><RuntimeBridge>{children}</RuntimeBridge></I18nProvider>
      </body>
    </html>
  )
}

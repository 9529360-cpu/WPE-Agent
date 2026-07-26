'use client'

import { createContext, useContext, useEffect, useMemo, useSyncExternalStore } from 'react'
import { localeMeta, locales, translate, type Locale, type TranslationKey } from './dictionaries'

const STORAGE_KEY = 'wpe.locale'
const CHANGE_EVENT = 'wpe-locale-change'

type I18nValue = {
  locale: Locale
  setLocale: (locale: Locale) => void
  t: (key: TranslationKey, values?: Record<string, string | number>) => string
  formatDate: (value: Date | string, options?: Intl.DateTimeFormatOptions) => string
  formatNumber: (value: number, options?: Intl.NumberFormatOptions) => string
  formatCurrency: (value: number, currency?: string) => string
}

const Context = createContext<I18nValue | null>(null)

function readLocale(): Locale {
  const saved = localStorage.getItem(STORAGE_KEY)
  return locales.includes(saved as Locale) ? (saved as Locale) : 'zh_CN'
}

function subscribe(onChange: () => void) {
  window.addEventListener('storage', onChange)
  window.addEventListener(CHANGE_EVENT, onChange)
  return () => {
    window.removeEventListener('storage', onChange)
    window.removeEventListener(CHANGE_EVENT, onChange)
  }
}

export function I18nProvider({ children }: { children: React.ReactNode }) {
  const locale = useSyncExternalStore<Locale>(subscribe, readLocale, () => 'zh_CN')

  useEffect(() => {
    document.documentElement.lang = localeMeta[locale].htmlLang
  }, [locale])

  const setLocale = (next: Locale) => {
    localStorage.setItem(STORAGE_KEY, next)
    window.dispatchEvent(new Event(CHANGE_EVENT))
  }

  const value = useMemo<I18nValue>(() => {
    const intl = localeMeta[locale].intl
    return {
      locale,
      setLocale,
      t: (key, values) => translate(locale, key, values),
      formatDate: (input, options) =>
        new Intl.DateTimeFormat(intl, options ?? { dateStyle: 'medium', timeStyle: 'short' }).format(new Date(input)),
      formatNumber: (input, options) => new Intl.NumberFormat(intl, options).format(input),
      formatCurrency: (input, currency = 'USD') =>
        new Intl.NumberFormat(intl, { style: 'currency', currency, maximumFractionDigits: 2 }).format(input),
    }
  }, [locale])

  return <Context.Provider value={value}>{children}</Context.Provider>
}

export function useI18n() {
  const value = useContext(Context)
  if (!value) throw new Error('useI18n must be used inside I18nProvider')
  return value
}

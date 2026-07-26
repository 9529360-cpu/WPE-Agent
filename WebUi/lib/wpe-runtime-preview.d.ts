declare module 'wpe-runtime-preview' {
  import type { WpeRuntimeState } from '@/components/runtime-bridge'

  export const browserPreviewEnabled: boolean
  export const previewState: WpeRuntimeState
}

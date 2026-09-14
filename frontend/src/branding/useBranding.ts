import { use } from 'react'
import { BrandingContext } from './BrandingContext'
import type { BrandingContextValue } from './branding'

export function useBranding(): BrandingContextValue {
  const context = use(BrandingContext)
  if (!context) throw new Error('useBranding must be used within a <BrandingProvider>')
  return context
}

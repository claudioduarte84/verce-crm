import { createContext } from 'react'
import type { BrandingContextValue } from './branding'

export const BrandingContext = createContext<BrandingContextValue | undefined>(undefined)

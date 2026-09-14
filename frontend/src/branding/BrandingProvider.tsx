import { useEffect, useRef, useState, type ReactNode } from 'react'
import { apiClient } from '../api/client'
import type { BrandingResponse } from '../api/types'
import { useSession } from '../auth/useSession'
import { BrandingContext } from './BrandingContext'
import { FALLBACK_BRANDING, type BrandingStatus, type ResolvedBranding } from './branding'

const BRANDING_ENDPOINT = '/api/settings/branding'
const DOCUMENT_TITLE_TEMPLATE = (name: string, subtitle: string) => `${name} | ${subtitle}`

function resolve(response: BrandingResponse): ResolvedBranding {
  const byRole = new Map(response.logos.map((logo) => [logo.role, logo.url]))
  return {
    productName: response.productName,
    productSubtitle: response.productSubtitle,
    logoUrl: byRole.get('SYSTEM_LOGO'),
    compactLogoUrl: byRole.get('SYSTEM_LOGO_COMPACT'),
    faviconUrl: byRole.get('FAVICON'),
  }
}

function applyDocumentEffects(branding: ResolvedBranding) {
  document.title = DOCUMENT_TITLE_TEMPLATE(branding.productName, branding.productSubtitle)
  if (!branding.faviconUrl) return
  const link = document.querySelector<HTMLLinkElement>("link[rel~='icon']")
  if (!link) return
  link.type = 'image/png'
  link.href = branding.faviconUrl
}

export function BrandingProvider({ children }: { children: ReactNode }) {
  const { session } = useSession()
  const [branding, setBranding] = useState<BrandingStatus>({ status: 'loading', branding: FALLBACK_BRANDING })
  // Same StrictMode-double-mount concern as SessionProvider: `inFlight` dedupes the actual
  // network call itself (not just which result wins) when React double-invokes this effect in
  // development, and `requestId` still guards against a stale in-flight request winning a race
  // against a newer session transition (e.g. a logout that starts right after the fetch began).
  const inFlight = useRef<Promise<void> | null>(null)
  const requestId = useRef(0)

  useEffect(() => {
    if (session.status !== 'authenticated') {
      // oxlint-disable-next-line react/set-state-in-effect
      setBranding({ status: 'fallback', branding: FALLBACK_BRANDING })
      return
    }
    if (inFlight.current) return

    const thisRequestId = ++requestId.current
    // oxlint-disable-next-line react/set-state-in-effect
    setBranding((current) => ({ status: 'loading', branding: current.branding }))

    const operation = (async () => {
      const result = await apiClient.get<BrandingResponse>(BRANDING_ENDPOINT)
      if (requestId.current !== thisRequestId) return
      if (result.ok) setBranding({ status: 'ready', branding: resolve(result.data) })
      else setBranding({ status: 'unavailable', branding: FALLBACK_BRANDING })
    })()

    inFlight.current = operation
    void operation.finally(() => { if (inFlight.current === operation) inFlight.current = null })
  }, [session.status])

  useEffect(() => {
    applyDocumentEffects(branding.branding)
  }, [branding])

  return <BrandingContext value={{ branding }}>{children}</BrandingContext>
}

/**
 * Application branding (H-S2-001 / ADR-0015 §5): the frontend renders product name, subtitle,
 * logo and favicon from the server's `/api/settings/branding` — never a hard-coded
 * `import logo from './logo.png'`.
 *
 * `/api/settings/branding` requires authentication (SECURITY §3.2's anonymous allow-list is
 * closed and does not include it), so a route reachable before login — only the Login page —
 * can never fetch live branding. FALLBACK is the mission's own sanctioned answer for that case
 * (§12): safe, baked-in defaults, never treated as authoritative, always superseded once the
 * server responds. That is a fixed architectural fact of this route, not a loading state, so it
 * is its own status rather than being folded into 'loading'.
 */
export const FALLBACK_PRODUCT_NAME = 'VERCE 3D'
export const FALLBACK_PRODUCT_SUBTITLE = 'Laboratório de Custos'

export interface ResolvedBranding {
  productName: string
  productSubtitle: string
  logoUrl?: string
  compactLogoUrl?: string
  faviconUrl?: string
}

export const FALLBACK_BRANDING: ResolvedBranding = {
  productName: FALLBACK_PRODUCT_NAME,
  productSubtitle: FALLBACK_PRODUCT_SUBTITLE,
}

export type BrandingStatus =
  | { status: 'loading'; branding: ResolvedBranding }
  | { status: 'ready'; branding: ResolvedBranding }
  /** No authenticated session (e.g. the Login page) — branding intentionally never fetched. */
  | { status: 'fallback'; branding: ResolvedBranding }
  /** An authenticated fetch failed (network/5xx) — same safe defaults, distinguishable for callers
   * that want to know a real fetch was attempted and did not succeed. */
  | { status: 'unavailable'; branding: ResolvedBranding }

export interface BrandingContextValue {
  branding: BrandingStatus
}

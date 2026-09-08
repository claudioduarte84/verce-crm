import { use } from 'react'
import { SessionContext } from './SessionContext'
import type { SessionContextValue } from './session'

export function useSession(): SessionContextValue {
  const context = use(SessionContext)
  if (!context) throw new Error('useSession must be used within a <SessionProvider>')
  return context
}

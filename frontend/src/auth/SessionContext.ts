import { createContext } from 'react'
import type { SessionContextValue } from './session'

export const SessionContext = createContext<SessionContextValue | undefined>(undefined)

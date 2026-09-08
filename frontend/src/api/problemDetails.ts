/**
 * RFC 7807 Problem Details, as ASP.NET Core's default error responses shape them
 * (SECURITY §: Production must never leak stack traces to the client).
 */
export interface ProblemDetails {
  type?: string
  title?: string
  status?: number
  detail?: string
  instance?: string
  errors?: Record<string, string[]>
  [key: string]: unknown
}

export function isProblemDetails(value: unknown): value is ProblemDetails {
  return (
    typeof value === 'object' &&
    value !== null &&
    ('title' in value || 'status' in value || 'detail' in value)
  )
}

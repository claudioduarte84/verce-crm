import { Component, type ErrorInfo, type ReactNode } from 'react'

interface Props {
  children: ReactNode
}

interface State {
  error: Error | null
}

/**
 * Application-level error boundary (mission §22). Production must never render a raw stack
 * trace — only development shows `error.message` for debugging. Hooks cannot implement
 * componentDidCatch/getDerivedStateFromError as of React 19, so this stays a class component.
 */
export class ErrorBoundary extends Component<Props, State> {
  state: State = { error: null }

  static getDerivedStateFromError(error: Error): State {
    return { error }
  }

  componentDidCatch(error: Error, info: ErrorInfo) {
    console.error('[ErrorBoundary] Unhandled error in the component tree', error, info.componentStack)
  }

  render() {
    if (this.state.error) {
      return (
        <div role="alert" className="page-centered">
          <div>
            <p>Ocorreu um erro inesperado. Tente recarregar a página.</p>
            {import.meta.env.DEV && (
              <pre style={{ textAlign: 'left', whiteSpace: 'pre-wrap' }}>{this.state.error.message}</pre>
            )}
          </div>
        </div>
      )
    }

    return this.props.children
  }
}

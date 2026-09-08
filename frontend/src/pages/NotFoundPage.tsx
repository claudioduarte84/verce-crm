import { Link } from 'react-router-dom'

export function NotFoundPage() {
  return (
    <div className="page-centered">
      <div>
        <p>Página não encontrada.</p>
        <Link to="/">Voltar ao início</Link>
      </div>
    </div>
  )
}

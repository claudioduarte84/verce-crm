import { useEffect, useState } from 'react'
import { Link, useParams } from 'react-router-dom'
import { apiClient } from '../api/client'
import { useSession } from '../auth/useSession'

type Sale = { id: string; saleNumber: string; source: string; status: string; quoteRevisionId: string | null; customerNameSnapshot: string | null; soldAt: string; netAmount: number | string; channelFeeAmount: number | string; shippingAmount: number | string; totalCostAmount: number | string; grossProfitAmount: number | string; effectiveMarginPercent: number | string; version: number; items: { id: string; productName: string; quantity: number | string; lineTotalAmount: number | string; lineCostAmount: number | string }[]; history: { id: string; toStatus: string; reason: string | null; changedAt: string }[] }
const money = (value: number | string) => Number(value).toLocaleString('pt-BR', { style: 'currency', currency: 'BRL' })
export function SaleDetailPage() {
  const { saleId } = useParams(); const { session } = useSession(); const canManage = session.status === 'authenticated' && (session.user.roles.includes('Owner') || session.user.roles.includes('Operator'))
  const [sale, setSale] = useState<Sale | null>(null); const [message, setMessage] = useState('')
  async function load() { if (!saleId) return; const r = await apiClient.get<Sale>(`/api/sales/${saleId}`); if (r.ok) setSale(r.data); else setMessage(r.error.safeMessage) }
  // oxlint-disable-next-line react(set-state-in-effect), react-hooks(exhaustive-deps) -- saleId is the route identity and deliberately reloads the resource.
  useEffect(() => { void load() }, [saleId])
  async function cancel() { if (!sale) return; let reason: string | null; do { reason = window.prompt('Motivo do cancelamento:'); if (reason === null) return; reason = reason.trim() } while (!reason); const r = await apiClient.post<void>(`/api/sales/${sale.id}/cancel`, { reason, version: sale.version }); if (r.ok) await load(); else setMessage(r.error.safeMessage) }
  if (!sale) return <section className="product-page"><Link to="/sales">← Vendas</Link><p role="status" className="form-error">{message}</p></section>
  return <section className="product-page"><Link to="/sales">← Vendas</Link><div className="page-heading"><div><h1>Venda {sale.saleNumber}</h1><p>{sale.customerNameSnapshot ?? 'Cliente não informado'} · {sale.source}</p></div>{canManage && sale.status === 'CONFIRMED' && <button className="button-danger" onClick={() => void cancel()}>Cancelar venda</button>}</div>
    <div className="split-grid"><div className="card"><h2>Itens</h2><table><thead><tr><th>Produto</th><th>Qtd.</th><th>Receita</th><th>Custo</th></tr></thead><tbody>{sale.items.map(i => <tr key={i.id}><td>{i.productName}</td><td>{i.quantity}</td><td>{money(i.lineTotalAmount)}</td><td>{money(i.lineCostAmount)}</td></tr>)}</tbody></table></div><div className="card"><h2>Totais</h2><dl className="totals-grid"><div><dt>Receita líquida</dt><dd>{money(sale.netAmount)}</dd></div><div><dt>Taxas</dt><dd>{money(sale.channelFeeAmount)}</dd></div><div><dt>Frete</dt><dd>{money(sale.shippingAmount)}</dd></div><div><dt>Lucro</dt><dd>{money(sale.grossProfitAmount)} ({(Number(sale.effectiveMarginPercent) * 100).toFixed(1)}%)</dd></div></dl><h3>Histórico</h3><ul>{sale.history.map(h => <li key={h.id}>{h.toStatus} — {new Date(h.changedAt).toLocaleString('pt-BR')}{h.reason ? `: ${h.reason}` : ''}</li>)}</ul></div></div>{message && <p role="status" className="form-error">{message}</p>}</section>
}

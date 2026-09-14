import { useEffect, useState, type FormEvent } from 'react'
import { apiClient } from '../api/client'
import type { AddressRequest, AddressResponse, CustomerListItemResponse, CustomerListResponse, CustomerRequest, CustomerResponse, PersonType } from '../api/types'

const PAGE_SIZE = 10

type CustomerForm = Omit<CustomerRequest, 'version'>
type AddressForm = Omit<AddressRequest, 'customerVersion'>

const blankForm: CustomerForm = { personType: 'Individual', name: '', tradeName: '', document: '', email: '', phone: '', notes: '' }
const blankAddress: AddressForm = { label: '', zipCode: '', street: '', number: '', complement: '', district: '', city: '', state: 'SP', country: 'BR', isPrimary: true, isDefaultShipping: true, notes: '' }

function toAddressForm(address: AddressResponse): AddressForm {
  return { label: address.label, zipCode: address.zipCode, street: address.street, number: address.number, complement: address.complement ?? '', district: address.district, city: address.city, state: address.state, country: address.country, isPrimary: address.isPrimary, isDefaultShipping: address.isDefaultShipping, notes: address.notes ?? '' }
}

export function CustomersPage() {
  const [items, setItems] = useState<CustomerListItemResponse[]>([])
  const [total, setTotal] = useState(0)
  const [page, setPage] = useState(1)
  const [search, setSearch] = useState('')
  const [form, setForm] = useState<CustomerForm>(blankForm)
  const [address, setAddress] = useState<AddressForm>(blankAddress)
  const [editingAddressId, setEditingAddressId] = useState<string | null>(null)
  const [selected, setSelected] = useState<CustomerResponse | null>(null)
  const [message, setMessage] = useState('')

  async function load(targetPage: number) {
    // No "show inactive" toggle: the only path that ever sets IsActive=false is soft-delete
    // (DELETE /api/customers/{id}), which also stamps deleted_at — and every list query filters
    // deleted_at unconditionally. There is no reachable "inactive but still listed" state to
    // toggle back into, so the `active` query parameter is left unused here.
    const params = new URLSearchParams({ search, page: String(targetPage), pageSize: String(PAGE_SIZE) })
    const result = await apiClient.get<CustomerListResponse>(`/api/customers?${params}`)
    if (result.ok) { setItems(result.data.items); setTotal(Number(result.data.total)); setPage(Number(result.data.page)) }
    else setMessage(result.error.safeMessage)
  }

  async function open(id: string) {
    const result = await apiClient.get<CustomerResponse>(`/api/customers/${id}`)
    if (result.ok) {
      setSelected(result.data)
      setForm({ personType: result.data.personType, name: result.data.name, tradeName: result.data.tradeName ?? '', document: result.data.document ?? '', email: result.data.email ?? '', phone: result.data.phone ?? '', notes: result.data.notes ?? '' })
      setEditingAddressId(null)
      setAddress(blankAddress)
    } else setMessage(result.error.safeMessage)
  }

  // The initial page is fetched from the server after mount; load intentionally captures the
  // first empty search/page rather than refetching on every keystroke.
  // oxlint-disable-next-line react/set-state-in-effect, react-hooks/exhaustive-deps
  useEffect(() => { void load(1) }, [])

  async function submit(event: FormEvent) {
    event.preventDefault()
    const body: CustomerRequest = { ...form, version: selected?.version ?? 0 }
    const result = selected ? await apiClient.put<CustomerResponse>(`/api/customers/${selected.id}`, body) : await apiClient.post<CustomerResponse>('/api/customers', body)
    if (result.ok) {
      setMessage(selected ? 'Cliente atualizado com sucesso.' : 'Cliente criado com sucesso.')
      if (selected) await open(selected.id)
      else { setForm(blankForm); await load(1) }
    } else setMessage(result.error.safeMessage)
  }

  async function deactivate() {
    if (!selected) return
    if (!window.confirm(`Desativar ${selected.name}? O cliente deixará de aparecer na lista de clientes ativos.`)) return
    const result = await apiClient.delete<void>(`/api/customers/${selected.id}?version=${selected.version}`)
    if (result.ok) { setMessage('Cliente desativado com sucesso.'); setSelected(null); setForm(blankForm); await load(page) }
    else setMessage(result.error.safeMessage)
  }

  async function saveAddress(event: FormEvent) {
    event.preventDefault()
    if (!selected) return
    const body: AddressRequest = { ...address, customerVersion: selected.version }
    const result = editingAddressId
      ? await apiClient.put<AddressResponse>(`/api/customers/${selected.id}/addresses/${editingAddressId}`, body)
      : await apiClient.post<AddressResponse>(`/api/customers/${selected.id}/addresses`, body)
    if (result.ok) { setAddress(blankAddress); setEditingAddressId(null); setMessage(editingAddressId ? 'Endereço atualizado com sucesso.' : 'Endereço salvo com sucesso.'); await open(selected.id) }
    else setMessage(result.error.safeMessage)
  }

  function editAddress(item: AddressResponse) {
    setEditingAddressId(item.id)
    setAddress(toAddressForm(item))
  }

  function cancelEditAddress() {
    setEditingAddressId(null)
    setAddress(blankAddress)
  }

  async function deleteAddress(addressId: string) {
    if (!selected) return
    if (!window.confirm('Remover este endereço?')) return
    const result = await apiClient.delete<void>(`/api/customers/${selected.id}/addresses/${addressId}?customerVersion=${selected.version}`)
    if (result.ok) { setMessage('Endereço removido com sucesso.'); if (editingAddressId === addressId) cancelEditAddress(); await open(selected.id) }
    else setMessage(result.error.safeMessage)
  }

  function newCustomer() {
    setSelected(null)
    setForm(blankForm)
    setEditingAddressId(null)
    setAddress(blankAddress)
    setMessage('')
  }

  const lastPage = Math.max(1, Math.ceil(total / PAGE_SIZE))

  return (
    <section className="product-page">
      <div className="page-heading">
        <div>
          <h1>Clientes</h1>
          <p>Cadastre e encontre clientes, seus contatos e endereços.</p>
        </div>
        <button type="button" onClick={newCustomer}>Novo cliente</button>
      </div>

      <div className="split-grid">
        <div className="card">
          <label htmlFor="customer-search">Buscar clientes</label>
          <div className="inline-form">
            <input id="customer-search" value={search} onChange={(e) => setSearch(e.target.value)} placeholder="Nome, documento, e-mail ou telefone" />
            <button type="button" onClick={() => void load(1)}>Buscar</button>
          </div>
          <ul className="data-list">
            {items.map((customer) => (
              <li key={customer.id}>
                <button type="button" onClick={() => void open(customer.id)}><strong>{customer.name}</strong></button>
                <span>{customer.email ?? customer.phone ?? 'Sem contato'}</span>
                <span className={customer.isActive ? 'badge' : 'badge badge--muted'}>{customer.isActive ? 'Ativo' : 'Inativo'}</span>
              </li>
            ))}
          </ul>
          <nav className="pagination" aria-label="Paginação de clientes">
            <button type="button" disabled={page <= 1} onClick={() => void load(page - 1)}>Anterior</button>
            <span>Página {page} de {lastPage}</span>
            <button type="button" disabled={page >= lastPage} onClick={() => void load(page + 1)}>Próxima</button>
          </nav>
        </div>

        <form className="card form-stack" onSubmit={submit}>
          <h2>{selected ? 'Editar cliente' : 'Novo cliente'}</h2>
          <label htmlFor="customer-type">Tipo</label>
          <select id="customer-type" value={form.personType} onChange={(e) => setForm({ ...form, personType: e.target.value as PersonType })}>
            <option value="Individual">Pessoa física</option>
            <option value="Company">Empresa</option>
          </select>
          <label htmlFor="customer-name">Nome</label>
          <input id="customer-name" required minLength={2} value={form.name} onChange={(e) => setForm({ ...form, name: e.target.value })} />
          <label htmlFor="customer-document">CPF ou CNPJ</label>
          <input id="customer-document" value={form.document ?? ''} onChange={(e) => setForm({ ...form, document: e.target.value })} />
          <label htmlFor="customer-email">E-mail</label>
          <input id="customer-email" type="email" value={form.email ?? ''} onChange={(e) => setForm({ ...form, email: e.target.value })} />
          <label htmlFor="customer-phone">Telefone</label>
          <input id="customer-phone" value={form.phone ?? ''} onChange={(e) => setForm({ ...form, phone: e.target.value })} />
          <button className="button-primary" type="submit">{selected ? 'Salvar alterações' : 'Salvar cliente'}</button>
          {selected && selected.isActive && (
            <button type="button" className="button-danger" onClick={() => void deactivate()}>Desativar cliente</button>
          )}
        </form>
      </div>

      {selected && (
        <div className="split-grid">
          <form className="card form-stack" onSubmit={saveAddress}>
            <h2>{editingAddressId ? 'Editar endereço' : 'Adicionar endereço'}</h2>
            <label htmlFor="address-label">Identificação</label>
            <input id="address-label" required value={address.label} onChange={(e) => setAddress({ ...address, label: e.target.value })} />
            <label htmlFor="address-zip">CEP</label>
            <input id="address-zip" required value={address.zipCode} onChange={(e) => setAddress({ ...address, zipCode: e.target.value })} />
            <label htmlFor="address-street">Rua</label>
            <input id="address-street" required value={address.street} onChange={(e) => setAddress({ ...address, street: e.target.value })} />
            <label htmlFor="address-number">Número</label>
            <input id="address-number" required value={address.number} onChange={(e) => setAddress({ ...address, number: e.target.value })} />
            <label htmlFor="address-district">Bairro</label>
            <input id="address-district" required value={address.district} onChange={(e) => setAddress({ ...address, district: e.target.value })} />
            <label htmlFor="address-city">Cidade</label>
            <input id="address-city" required value={address.city} onChange={(e) => setAddress({ ...address, city: e.target.value })} />
            <label htmlFor="address-state">UF</label>
            <input id="address-state" required minLength={2} maxLength={2} value={address.state} onChange={(e) => setAddress({ ...address, state: e.target.value })} />
            <label>
              <input type="checkbox" checked={address.isPrimary} onChange={(e) => setAddress({ ...address, isPrimary: e.target.checked })} /> Endereço principal
            </label>
            <label>
              <input type="checkbox" checked={address.isDefaultShipping} onChange={(e) => setAddress({ ...address, isDefaultShipping: e.target.checked })} /> Endereço padrão de entrega
            </label>
            <button className="button-primary" type="submit">{editingAddressId ? 'Salvar edição' : 'Salvar endereço'}</button>
            {editingAddressId && <button type="button" onClick={cancelEditAddress}>Cancelar edição</button>}
          </form>

          <div className="card">
            <h2>Endereços de {selected.name}</h2>
            <ul className="data-list">
              {selected.addresses.map((item) => (
                <li key={item.id}>
                  <strong>{item.label}</strong>
                  <span>{item.street}, {item.number} — {item.city}/{item.state}</span>
                  {item.isPrimary && <span className="badge">Principal</span>}
                  {item.isDefaultShipping && <span className="badge">Entrega padrão</span>}
                  <div className="inline-form">
                    <button type="button" onClick={() => editAddress(item)}>Editar</button>
                    <button type="button" onClick={() => void deleteAddress(item.id)}>Remover</button>
                  </div>
                </li>
              ))}
            </ul>
          </div>
        </div>
      )}

      {message && <p role="status" className="form-error">{message}</p>}
    </section>
  )
}

import { useCallback, useEffect, useState, type FormEvent } from 'react'
import { Link } from 'react-router-dom'
import { apiClient } from '../api/client'
import { useSession } from '../auth/useSession'

type Capability={capabilityCode:string;state:string}
type Account={id:string;providerCode:string;externalAccountId:string;salesChannelId:string;displayName:string;active:boolean;authorizationState:string;runtimeAvailability:string;syncState:string;version:number;capabilities:Capability[]}
type Provider={code:string;name:string;capabilities:{capabilityCode:string;state:string}[]}
type Channel={id:string;code:string;name:string;kind:string;active:boolean}
type AuthorizationForm={providerCode:string;salesChannelId:string;displayName:string}
type EditForm={displayName:string}
type BegunAuthorization={sessionId:string;expiresAt:string;authorizationUri:string}
// Mirrors ConnectionStatusResponse (CommerceEndpoints.cs) exactly — never CredentialReference,
// secret version, state/browser hashes or file paths: the backend DTO structurally excludes
// them, so there is nothing for this page to accidentally render even by extending this type.
type ConnectionStatus={accountId:string;authorizationState:string;runtimeAvailability:string;identityVerifiedAt:string|null;lastSuccessAt:string|null;lastFailureAt:string|null;lastFailureClassification:string|null;requiredAction:string;authorizationInProgress:boolean;credentialOperationInProgress:boolean;version:number}
const blankAuthorization:AuthorizationForm={providerCode:'',salesChannelId:'',displayName:''}
// pt-BR local catalog text for the RequiredAction codes the backend can return (CommerceEndpoints.cs RequiredAction) —
// never invented client-side, only labeled.
const requiredActionLabels:Record<string,string>={
  AUTHORIZE:'Conecte esta conta autorizando com o provedor.',
  REAUTHORIZE:'Autorização não é mais válida. Reautorize com o provedor.',
  RECONNECT_EXISTING_ACCOUNT:'Uma nova autorização identificou esta MESMA conta no provedor. Reconecte explicitamente esta conta em vez de criar uma nova.',
  REAUTHORIZE_AFTER_STORE_LOSS:'A credencial protegida local não pôde ser confirmada (perda de armazenamento/chave). Reautorize para gerar uma nova credencial; a conta e o histórico são preservados.',
  CHECK_PROVIDER_ACCOUNT:'O provedor negou uma capacidade ou uma operação falhou. Verifique a conta diretamente no provedor.',
  CONTACT_ADMIN:'Contate o administrador: configuração ou armazenamento de credenciais indisponível.',
  NONE:'',
}

export function MarketplaceAccountsPage(){
  const {session}=useSession();const isOwner=session.status==='authenticated'&&session.user.roles.includes('Owner')
  const [items,setItems]=useState<Account[]>([]);const [providers,setProviders]=useState<Provider[]>([]);const [channels,setChannels]=useState<Channel[]>([]);const [selected,setSelected]=useState<Account|null>(null);const [status,setStatus]=useState<ConnectionStatus|null>(null);const [authorization,setAuthorization]=useState<AuthorizationForm>(blankAuthorization);const [edit,setEdit]=useState<EditForm>({displayName:''});const [message,setMessage]=useState('')
  const load=useCallback(async()=>{const [a,p,c]=await Promise.all([apiClient.get<Account[]>('/api/commerce/marketplace-accounts'),apiClient.get<Provider[]>('/api/commerce/providers'),apiClient.get<Channel[]>('/api/pricing/channels?includeInactive=false')]);if(a.ok)setItems(a.data);else setMessage(a.error.safeMessage);if(p.ok)setProviders(p.data);if(c.ok)setChannels(c.data.filter(x=>x.kind==='Marketplace'&&x.code!=='DIRECT'))},[])
  useEffect(()=>{const timer=window.setTimeout(()=>void load(),0);return()=>window.clearTimeout(timer)},[load])
  const loadStatus=useCallback(async(accountId:string)=>{const r=await apiClient.get<ConnectionStatus>(`/api/commerce/marketplace-accounts/${accountId}/connection`);if(r.ok)setStatus(r.data)},[])
  function choose(item:Account){setSelected(item);setEdit({displayName:item.displayName});setStatus(null);void loadStatus(item.id)}
  function fresh(){setSelected(null);setStatus(null);setAuthorization(blankAuthorization);setEdit({displayName:''})}
  async function begin(e:FormEvent){e.preventDefault();const r=await apiClient.post<BegunAuthorization>('/api/commerce/marketplace-authorizations',authorization);if(!r.ok){setMessage(r.error.safeMessage);return}setMessage('Redirecionando para autorização segura do provedor.');window.location.assign(r.data.authorizationUri)}
  async function save(e:FormEvent){e.preventDefault();if(!selected)return;const r=await apiClient.put<void>(`/api/commerce/marketplace-accounts/${selected.id}`,{displayName:edit.displayName,version:selected.version});setMessage(r.ok?'Conta atualizada.':r.error.safeMessage);if(r.ok){fresh();await load()}}
  async function reconnect(){if(!selected)return;const r=await apiClient.post<BegunAuthorization>(`/api/commerce/marketplace-accounts/${selected.id}/reauthorize`,{version:selected.version});if(!r.ok){setMessage(r.error.safeMessage);return}setMessage('Redirecionando para reautorização segura do provedor.');window.location.assign(r.data.authorizationUri)}
  async function disconnect(){if(!selected)return;const r=await apiClient.post<void>(`/api/commerce/marketplace-accounts/${selected.id}/disconnect`,{version:selected.version});setMessage(r.ok?'Conta desconectada. Acesso já negado; exclusão local pendente ou confirmada.':r.error.safeMessage);if(r.ok){fresh();await load()}}
  async function toggle(){if(!selected)return;const action=selected.active?'deactivate':'activate';const r=await apiClient.post<void>(`/api/commerce/marketplace-accounts/${selected.id}/${action}?version=${selected.version}`);setMessage(r.ok?`Conta ${selected.active?'desativada':'ativada'}.`:r.error.safeMessage);if(r.ok){fresh();await load()}}
  // A probe (success OR failure) always mutates the account's Connection child, which bumps the
  // ROOT's own Version (mission concurrency rule: any change to root or children bumps the root).
  // load() refreshes the list but never `selected` itself, so without this the NEXT action taken
  // on the still-selected account (most importantly reconnecting right after a probe reveals
  // REAUTHORIZE_AFTER_STORE_LOSS) would send the now-stale Version and fail with a spurious
  // concurrency conflict instead of the real reauthorize flow the Owner just asked for.
  async function probe(){if(!selected)return;const r=await apiClient.post<{succeeded:boolean;code:string}>(`/api/commerce/marketplace-accounts/${selected.id}/probe`,{version:selected.version});setMessage(r.ok?'Sondagem concluída: conta disponível.':`Sondagem falhou: ${r.error.safeMessage}`);const refreshed=await apiClient.get<Account[]>('/api/commerce/marketplace-accounts');if(refreshed.ok){setItems(refreshed.data);const updated=refreshed.data.find(x=>x.id===selected.id);if(updated)setSelected(updated)}await loadStatus(selected.id)}
  const requiredAction=status?.requiredAction??'NONE'
  return <section className="product-page"><div className="page-heading"><div><Link to="/">← Início</Link><h1>Contas de Marketplace</h1><p>Contas são criadas e reautorizadas somente pelo fluxo seguro do provedor. Credenciais nunca são exibidas ou editadas aqui.</p></div>{isOwner&&<button type="button" onClick={fresh}>Conectar conta</button>}</div>{message&&<p role="status">{message}</p>}
    <div className="split-grid"><div className="card"><ul className="data-list">{items.map(x=><li key={x.id}><button type="button" onClick={()=>choose(x)}><strong>{x.displayName}</strong></button><span>{x.providerCode} · {x.externalAccountId}</span><span>Canal: {channels.find(c=>c.id===x.salesChannelId)?.name??x.salesChannelId}</span><span className={x.active?'badge':'badge badge--muted'}>{x.active?'Ativa':'Inativa'}</span><span>Autorização: {x.authorizationState}; disponibilidade: {x.runtimeAvailability}; sincronização: {x.syncState}</span><span>Capacidades verificadas: {x.capabilities.map(c=>`${c.capabilityCode}: ${c.state}`).join(', ')||'ainda não verificadas'}</span></li>)}{items.length===0&&<li>Nenhuma conta conectada.</li>}</ul></div>
      {isOwner&&!selected&&<form className="card form-stack" onSubmit={begin}><h2>Conectar conta</h2><label htmlFor="account-provider">Provedor</label><select id="account-provider" required value={authorization.providerCode} onChange={e=>setAuthorization({...authorization,providerCode:e.target.value})}><option value="">Selecione</option>{providers.map(x=><option key={x.code} value={x.code}>{x.name}</option>)}</select><label htmlFor="account-channel">Canal</label><select id="account-channel" required value={authorization.salesChannelId} onChange={e=>setAuthorization({...authorization,salesChannelId:e.target.value})}><option value="">Selecione</option>{channels.map(x=><option key={x.id} value={x.id}>{x.code} · {x.name}</option>)}</select><label htmlFor="account-name">Nome de exibição</label><input id="account-name" required value={authorization.displayName} onChange={e=>setAuthorization({...authorization,displayName:e.target.value})}/><button type="submit">Autorizar com provedor</button></form>}
      {selected&&<div className="card form-stack"><h2>Conta conectada</h2><p>Identidade externa, canal e credencial são definidos pelo provedor e não podem ser editados manualmente.</p>
        {status&&<div role="status"><p>Estado de autorização: <strong>{status.authorizationState}</strong> · Disponibilidade: <strong>{status.runtimeAvailability}</strong></p>
          {status.identityVerifiedAt&&<p>Identidade verificada em: {new Date(status.identityVerifiedAt).toLocaleString('pt-BR')}</p>}
          <p>Último sucesso: {status.lastSuccessAt?new Date(status.lastSuccessAt).toLocaleString('pt-BR'):'nenhum registrado'}</p>
          <p>Última falha: {status.lastFailureAt?`${new Date(status.lastFailureAt).toLocaleString('pt-BR')}${status.lastFailureClassification?` (${status.lastFailureClassification})`:''}`:'nenhuma registrada'}</p>
          {status.authorizationInProgress&&<p className="badge">Autorização em andamento</p>}
          {status.credentialOperationInProgress&&<p className="badge">Operação de credencial em andamento</p>}
          {requiredAction!=='NONE'&&<p role="alert" className={requiredAction==='RECONNECT_EXISTING_ACCOUNT'||requiredAction==='REAUTHORIZE_AFTER_STORE_LOSS'?'badge badge--warning':'badge badge--muted'}>{requiredActionLabels[requiredAction]??requiredAction}</p>}
        </div>}
        {isOwner&&<form className="form-stack" onSubmit={save}><label htmlFor="account-name">Nome de exibição</label><input id="account-name" required value={edit.displayName} onChange={e=>setEdit({displayName:e.target.value})}/><button type="submit">Salvar nome</button>
          <button type="button" onClick={()=>void reconnect()}>{requiredAction==='RECONNECT_EXISTING_ACCOUNT'?'Reconectar conta existente':requiredAction==='REAUTHORIZE_AFTER_STORE_LOSS'?'Recuperar credencial (reautorizar)':'Reautorizar'}</button>
          <button type="button" onClick={()=>void probe()}>Sondar disponibilidade</button>
          <button type="button" onClick={()=>void disconnect()}>Desconectar conta</button>
          <button type="button" onClick={()=>void toggle()}>{selected.active?'Desativar':'Ativar'} conta</button>
        </form>}
      </div>}</div>
  </section>
}

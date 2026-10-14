import { act,render,screen,waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter } from 'react-router-dom'
import { beforeEach,describe,expect,it,vi } from 'vitest'
import { MarketplaceAccountsPage } from './MarketplaceAccountsPage'
const api=vi.hoisted(()=>({get:vi.fn(),post:vi.fn(),put:vi.fn(),delete:vi.fn()}));vi.mock('../api/client',()=>({apiClient:api}))
const state=vi.hoisted(()=>({roles:['Owner'] as string[]}));vi.mock('../auth/useSession',()=>({useSession:()=>({session:{status:'authenticated',user:{roles:state.roles}}})}))
const connectionStatus=vi.hoisted(()=>({current:{accountId:'a1',authorizationState:'CONNECTED',runtimeAvailability:'AVAILABLE',identityVerifiedAt:'2026-01-01T00:00:00Z',lastSuccessAt:'2026-01-02T00:00:00Z',lastFailureAt:null as string|null,lastFailureClassification:null as string|null,requiredAction:'NONE',authorizationInProgress:false,credentialOperationInProgress:false,version:1}}))
const syncStatus=(summary:Record<string,unknown>={},avail:Record<string,unknown>={})=>({availability:{canStart:true,blockedReason:null,requiredAction:'NONE',retryAfterUntil:null,activeRunId:null,...avail},summary:{activeRun:null,latestRun:null,latestFullRun:null,lastSucceededFullRunAt:null,openBlockingItemCount:0,health:'NEVER_RUN',...summary}})
function setup(){api.get.mockImplementation((p:string)=>{if(p.endsWith('/listing-sync'))return Promise.resolve({ok:true,data:syncStatus()});if(p.includes('/sku-mappings'))return Promise.resolve({ok:true,data:{items:[],page:1,pageSize:25,total:0}});if(p==='/api/commerce/marketplace-accounts')return Promise.resolve({ok:true,data:[{id:'a1',providerCode:'SHOPEE',externalAccountId:'shop',salesChannelId:'c1',displayName:'Loja',active:true,authorizationState:'CONNECTED',runtimeAvailability:'AVAILABLE',syncState:'NEVER_SYNCED',version:1,capabilities:[{capabilityCode:'LISTINGS_READ',state:'GRANTED'}]}]});if(p==='/api/commerce/providers')return Promise.resolve({ok:true,data:[{code:'SHOPEE',name:'Shopee',capabilities:[]}]});if(p==='/api/commerce/marketplace-accounts/a1/connection')return Promise.resolve({ok:true,data:connectionStatus.current});return Promise.resolve({ok:true,data:[{id:'c1',code:'SHOP',name:'Shopee',kind:'Marketplace',active:true}]})});api.post.mockResolvedValue({ok:true,data:{sessionId:'s1',expiresAt:'2026-01-01T00:00:00Z',authorizationUri:'https://provider.example/authorize'}});api.put.mockResolvedValue({ok:true,data:{}})}
describe('MarketplaceAccountsPage',()=>{beforeEach(()=>{vi.clearAllMocks();state.roles=['Owner'];connectionStatus.current={accountId:'a1',authorizationState:'CONNECTED',runtimeAvailability:'AVAILABLE',identityVerifiedAt:'2026-01-01T00:00:00Z',lastSuccessAt:'2026-01-02T00:00:00Z',lastFailureAt:null,lastFailureClassification:null,requiredAction:'NONE',authorizationInProgress:false,credentialOperationInProgress:false,version:1};setup()})
  it('shows authorization state and lifecycle controls to Owner',async()=>{render(<MemoryRouter><MarketplaceAccountsPage/></MemoryRouter>);expect(await screen.findByRole('button',{name:'Conectar conta'})).toBeInTheDocument();expect(await screen.findByText(/LISTINGS_READ: GRANTED/)).toBeInTheDocument()})
  it('keeps Operator account administration read only',async()=>{state.roles=['Operator'];render(<MemoryRouter><MarketplaceAccountsPage/></MemoryRouter>);expect(await screen.findByText(/LISTINGS_READ: GRANTED/)).toBeInTheDocument();expect(screen.queryByRole('button',{name:'Conectar conta'})).not.toBeInTheDocument()})
  it('starts authorization without collecting an external identity or credential',async()=>{const user=userEvent.setup();render(<MemoryRouter><MarketplaceAccountsPage/></MemoryRouter>);await screen.findByRole('option',{name:'Shopee'});api.post.mockResolvedValueOnce({ok:false,error:{safeMessage:'provider unavailable'}});await user.selectOptions(screen.getByLabelText('Provedor'),'SHOPEE');await user.selectOptions(screen.getByLabelText('Canal'),'c1');await user.type(screen.getByLabelText('Nome de exibição'),'Nova Loja');await user.click(screen.getByRole('button',{name:'Autorizar com provedor'}));await waitFor(()=>expect(api.post).toHaveBeenCalledWith('/api/commerce/marketplace-authorizations',{providerCode:'SHOPEE',salesChannelId:'c1',displayName:'Nova Loja'}));expect(screen.queryByLabelText(/Identidade externa/)).not.toBeInTheDocument();expect(screen.queryByLabelText(/credencial/i)).not.toBeInTheDocument()})
  it('uses dedicated reauthorization and disconnect endpoints',async()=>{const user=userEvent.setup();render(<MemoryRouter><MarketplaceAccountsPage/></MemoryRouter>);await user.click(await screen.findByRole('button',{name:'Loja'}));api.post.mockResolvedValueOnce({ok:false,error:{safeMessage:'provider unavailable'}});await user.click(screen.getByRole('button',{name:'Reautorizar'}));expect(api.post).toHaveBeenCalledWith('/api/commerce/marketplace-accounts/a1/reauthorize',{version:1});await user.click(screen.getByRole('button',{name:'Desconectar conta'}));expect(api.post).toHaveBeenCalledWith('/api/commerce/marketplace-accounts/a1/disconnect',{version:1})})

  it('shows safe status detail (identity verified, last success, no secret fields) after selecting an account',async()=>{const user=userEvent.setup();render(<MemoryRouter><MarketplaceAccountsPage/></MemoryRouter>);await user.click(await screen.findByRole('button',{name:'Loja'}));expect(await screen.findByText(/Último sucesso/)).toBeInTheDocument();expect(screen.getByText(/Identidade verificada em/)).toBeInTheDocument();const html=document.body.innerHTML;expect(html).not.toMatch(/credentialReference/i);expect(html).not.toMatch(/stateHash/i);expect(html).not.toMatch(/browserBindingHash/i);expect(html).not.toMatch(/access.?token/i);expect(html).not.toMatch(/refresh.?token/i)})

  it('offers an explicit Probe action that calls the dedicated endpoint',async()=>{const user=userEvent.setup();render(<MemoryRouter><MarketplaceAccountsPage/></MemoryRouter>);await user.click(await screen.findByRole('button',{name:'Loja'}));await user.click(await screen.findByRole('button',{name:'Sondar disponibilidade'}));expect(api.post).toHaveBeenCalledWith('/api/commerce/marketplace-accounts/a1/probe',{version:1})})

  it('shows a distinct RECONNECT_EXISTING_ACCOUNT prompt and reuses the reauthorize route for it',async()=>{connectionStatus.current={...connectionStatus.current,requiredAction:'RECONNECT_EXISTING_ACCOUNT'};const user=userEvent.setup();render(<MemoryRouter><MarketplaceAccountsPage/></MemoryRouter>);await user.click(await screen.findByRole('button',{name:'Loja'}));expect(await screen.findByText(/MESMA conta no provedor/)).toBeInTheDocument();const reconnectButton=screen.getByRole('button',{name:'Reconectar conta existente'});await user.click(reconnectButton);expect(api.post).toHaveBeenCalledWith('/api/commerce/marketplace-accounts/a1/reauthorize',{version:1})})

  it('shows a distinct REAUTHORIZE_AFTER_STORE_LOSS prompt without deleting the account, and reuses the reauthorize route',async()=>{connectionStatus.current={...connectionStatus.current,authorizationState:'REAUTHORIZATION_REQUIRED',requiredAction:'REAUTHORIZE_AFTER_STORE_LOSS'};const user=userEvent.setup();render(<MemoryRouter><MarketplaceAccountsPage/></MemoryRouter>);await user.click(await screen.findByRole('button',{name:'Loja'}));expect(await screen.findByText(/perda de armazenamento\/chave/)).toBeInTheDocument();const recoverButton=screen.getByRole('button',{name:'Recuperar credencial (reautorizar)'});await user.click(recoverButton);expect(api.post).toHaveBeenCalledWith('/api/commerce/marketplace-accounts/a1/reauthorize',{version:1});expect(screen.getByRole('button',{name:'Loja'})).toBeInTheDocument()})

  it('lets Operator see safe status detail read-only without any configuration/credential action',async()=>{state.roles=['Operator'];const user=userEvent.setup();render(<MemoryRouter><MarketplaceAccountsPage/></MemoryRouter>);await user.click(await screen.findByRole('button',{name:'Loja'}));expect(await screen.findByText(/Último sucesso/)).toBeInTheDocument();expect(screen.queryByRole('button',{name:'Reautorizar'})).not.toBeInTheDocument();expect(screen.queryByRole('button',{name:'Sondar disponibilidade'})).not.toBeInTheDocument();expect(screen.queryByRole('button',{name:'Desconectar conta'})).not.toBeInTheDocument();expect(screen.queryByRole('button',{name:'Ativar conta'})).not.toBeInTheDocument()})

  it('lets Viewer see safe status detail read-only without any configuration/credential action',async()=>{state.roles=['Viewer'];const user=userEvent.setup();render(<MemoryRouter><MarketplaceAccountsPage/></MemoryRouter>);await user.click(await screen.findByRole('button',{name:'Loja'}));expect(await screen.findByText(/Último sucesso/)).toBeInTheDocument();expect(screen.queryByRole('button',{name:'Reautorizar'})).not.toBeInTheDocument();expect(screen.queryByRole('button',{name:'Sondar disponibilidade'})).not.toBeInTheDocument()})
})

// ---- S8C.2 C4: the listing-sync block and "SKUs confirmados" on the selected account ----
const twoAccounts=[
  {id:'a1',providerCode:'MERCADO_LIVRE',externalAccountId:'seller-1',salesChannelId:'c1',displayName:'Loja Um',active:true,authorizationState:'CONNECTED',runtimeAvailability:'AVAILABLE',syncState:'NEVER_SYNCED',version:1,capabilities:[{capabilityCode:'LISTINGS_READ',state:'GRANTED'}]},
  {id:'a2',providerCode:'MERCADO_LIVRE',externalAccountId:'seller-2',salesChannelId:'c1',displayName:'Loja Dois',active:true,authorizationState:'CONNECTED',runtimeAvailability:'AVAILABLE',syncState:'SYNCED',version:1,capabilities:[]},
]
const syncRun=(runId:string,accountId:string,total:number)=>({runId,accountId,kind:'FULL',status:'SUCCEEDED',phase:null,rootRunId:runId,retryOfRunId:null,requestedAt:'2026-10-09T12:00:00Z',startedAt:'2026-10-09T12:00:05Z',finishedAt:'2026-10-09T12:05:00Z',attemptCount:1,enumerationCompleted:true,counters:{total,found:total,blockingRetryable:0,blockingNonRetryable:0,pending:0,byOutcome:{},byOrigin:{},listingsCreated:0,observationsAppended:0},safeResultCode:'LISTING_SYNC_COMPLETED',requiredAction:'NONE'})
function serveAccounts(extra?:(p:string)=>unknown){api.get.mockImplementation((p:string)=>{const custom=extra?.(p);if(custom!==undefined)return custom as Promise<unknown>
  if(p==='/api/commerce/marketplace-accounts')return Promise.resolve({ok:true,data:twoAccounts})
  if(p==='/api/commerce/providers')return Promise.resolve({ok:true,data:[{code:'MERCADO_LIVRE',name:'Mercado Livre',capabilities:[]}]})
  if(p.endsWith('/connection'))return Promise.resolve({ok:true,data:{...connectionStatus.current,accountId:p.split('/')[4]}})
  if(p.endsWith('/listing-sync'))return Promise.resolve({ok:true,data:syncStatus()})
  if(p.includes('/sku-mappings'))return Promise.resolve({ok:true,data:{items:[{id:'m1',marketplaceAccountId:'a1',externalSku:'SKU-1',productId:'p1',productCode:'P-001',productName:'Caneca',active:true,confirmedAt:'2026-10-01T10:00:00Z',invalidatedAt:null,invalidatedReason:null,version:3}],page:1,pageSize:25,total:1}})
  return Promise.resolve({ok:true,data:[{id:'c1',code:'ML',name:'Mercado Livre',kind:'Marketplace',active:true}]})})}
const OAUTH_BUTTONS=['Reautorizar','Sondar disponibilidade','Desconectar conta','Salvar nome']

describe('MarketplaceAccountsPage — listing sync and SKU mappings',()=>{
  beforeEach(()=>{vi.clearAllMocks();state.roles=['Owner'];serveAccounts();api.post.mockResolvedValue({ok:true,status:202,data:{}})})

  it('shows the sync block and the SKU list only for a selected account, separate from the authorization block, with no automatic sync',async()=>{
    const user=userEvent.setup();render(<MemoryRouter><MarketplaceAccountsPage/></MemoryRouter>)
    await screen.findByRole('button',{name:'Loja Um'})
    expect(screen.queryByRole('heading',{name:'Sincronização de anúncios'})).not.toBeInTheDocument();expect(screen.queryByRole('heading',{name:'SKUs confirmados'})).not.toBeInTheDocument()
    await user.click(screen.getByRole('button',{name:'Loja Um'}))
    expect(await screen.findByRole('heading',{name:'Sincronização de anúncios'})).toBeInTheDocument();expect(await screen.findByRole('heading',{name:'SKUs confirmados'})).toBeInTheDocument()
    expect(screen.getByRole('heading',{name:'Conta conectada'})).toBeInTheDocument() // the S8C.1 authorization block is still its own card
    expect(api.post).not.toHaveBeenCalled()
    expect(api.get).toHaveBeenCalledWith('/api/commerce/marketplace-accounts/a1/listing-sync',expect.anything())
    expect(api.get).toHaveBeenCalledWith(expect.stringContaining('/api/commerce/marketplace-accounts/a1/sku-mappings?'),expect.anything())
  })

  it('no longer renders the generic account syncState as listing health in the account list',async()=>{
    render(<MemoryRouter><MarketplaceAccountsPage/></MemoryRouter>)
    await screen.findByRole('button',{name:'Loja Um'})
    expect(document.body.textContent).not.toMatch(/sincronização:|NEVER_SYNCED|; sincroniza/)
    expect(screen.getAllByText(/Autorização: CONNECTED; disponibilidade: AVAILABLE$/)).toHaveLength(2)
  })

  it('Owner gets Start, SKU deactivation and every S8C.1 authorization control',async()=>{
    const user=userEvent.setup();render(<MemoryRouter><MarketplaceAccountsPage/></MemoryRouter>)
    await user.click(await screen.findByRole('button',{name:'Loja Um'}))
    expect(await screen.findByRole('button',{name:'Sincronizar anúncios'})).toBeEnabled();expect(await screen.findByRole('button',{name:'Desativar SKU SKU-1'})).toBeInTheDocument()
    for(const name of OAUTH_BUTTONS)expect(screen.getByRole('button',{name})).toBeInTheDocument()
  })

  it('Operator gets Start and SKU deactivation but still no OAuth credential controls',async()=>{
    state.roles=['Operator'];const user=userEvent.setup();render(<MemoryRouter><MarketplaceAccountsPage/></MemoryRouter>)
    await user.click(await screen.findByRole('button',{name:'Loja Um'}))
    expect(await screen.findByRole('button',{name:'Sincronizar anúncios'})).toBeEnabled();expect(await screen.findByRole('button',{name:'Desativar SKU SKU-1'})).toBeInTheDocument()
    for(const name of OAUTH_BUTTONS)expect(screen.queryByRole('button',{name})).not.toBeInTheDocument()
    expect(screen.queryByRole('button',{name:'Conectar conta'})).not.toBeInTheDocument()
  })

  it('Viewer only reads: status and SKUs visible, no Start, Retry, Desativar or credential control',async()=>{
    state.roles=['Viewer'];const user=userEvent.setup();render(<MemoryRouter><MarketplaceAccountsPage/></MemoryRouter>)
    await user.click(await screen.findByRole('button',{name:'Loja Um'}))
    expect(await screen.findByText('Nunca sincronizada')).toBeInTheDocument();expect(await screen.findByText('SKU-1')).toBeInTheDocument()
    for(const name of ['Sincronizar anúncios','Tentar novamente itens com erro','Desativar SKU SKU-1',...OAUTH_BUTTONS])expect(screen.queryByRole('button',{name})).not.toBeInTheDocument()
  })

  it('switching accounts shows only the new account data and never the previous one, even when the old response arrives late',async()=>{
    let lateA1:((value:unknown)=>void)|undefined
    serveAccounts(p=>{
      if(p.includes('/a1/listing-sync'))return new Promise(resolve=>{lateA1=resolve})
      if(p.includes('/a2/listing-sync'))return Promise.resolve({ok:true,data:syncStatus({health:'HEALTHY',latestRun:syncRun('r-a2','a2',42),latestFullRun:syncRun('r-a2','a2',42)})})
      if(p.includes('/a2/sku-mappings'))return Promise.resolve({ok:true,data:{items:[{id:'m2',marketplaceAccountId:'a2',externalSku:'SKU-OF-A2',productId:'p9',productCode:'P-009',productName:'Copo',active:true,confirmedAt:'2026-10-01T10:00:00Z',invalidatedAt:null,invalidatedReason:null,version:1}],page:1,pageSize:25,total:1}})
      return undefined})
    const user=userEvent.setup();render(<MemoryRouter><MarketplaceAccountsPage/></MemoryRouter>)
    await user.click(await screen.findByRole('button',{name:'Loja Um'}));await waitFor(()=>expect(lateA1).toBeDefined())
    await user.click(screen.getByRole('button',{name:'Loja Dois'}))
    expect(await screen.findByText('SKU-OF-A2')).toBeInTheDocument()
    expect(await screen.findByRole('group',{name:'Última execução'})).toHaveTextContent('Total: 42')
    await act(async()=>{lateA1?.({ok:true,data:syncStatus({health:'ATTENTION_REQUIRED',openBlockingItemCount:9,latestRun:syncRun('r-a1','a1',1)})})})
    expect(screen.getByRole('group',{name:'Última execução'})).toHaveTextContent('Total: 42');expect(document.body.textContent).not.toMatch(/Itens bloqueantes em aberto: 9/)
    expect(screen.queryByText('SKU-1')).not.toBeInTheDocument()
    // the S8C.1 connection status keeps the same isolation
    expect(api.get).toHaveBeenCalledWith('/api/commerce/marketplace-accounts/a2/listing-sync',expect.anything())
  })

  it('while the new account is still loading, nothing of the previous account remains on screen',async()=>{
    serveAccounts(p=>{
      if(p.includes('/a1/listing-sync'))return Promise.resolve({ok:true,data:syncStatus({health:'HEALTHY',latestRun:syncRun('r-a1','a1',11),latestFullRun:syncRun('r-a1','a1',11)})})
      if(p.includes('/a2/listing-sync')||p.includes('/a2/sku-mappings'))return new Promise(()=>{}) // never answers
      return undefined})
    const user=userEvent.setup();render(<MemoryRouter><MarketplaceAccountsPage/></MemoryRouter>)
    await user.click(await screen.findByRole('button',{name:'Loja Um'}))
    expect(await screen.findByRole('group',{name:'Última execução'})).toHaveTextContent('Total: 11');expect(await screen.findByText('SKU-1')).toBeInTheDocument()
    await user.click(screen.getByRole('button',{name:'Loja Dois'}))
    expect(await screen.findByText('Carregando sincronização…')).toBeInTheDocument()
    expect(screen.queryByRole('group',{name:'Última execução'})).not.toBeInTheDocument();expect(screen.queryByText('SKU-1')).not.toBeInTheDocument()
    expect(screen.queryByRole('button',{name:'Sincronizar anúncios'})).not.toBeInTheDocument() // no Start until the new account's own availability is known
  })

  it('starts a sync for the selected account only, with no body',async()=>{
    const user=userEvent.setup();render(<MemoryRouter><MarketplaceAccountsPage/></MemoryRouter>)
    await user.click(await screen.findByRole('button',{name:'Loja Dois'}))
    await user.click(await screen.findByRole('button',{name:'Sincronizar anúncios'}))
    expect(api.post).toHaveBeenCalledTimes(1);expect(api.post).toHaveBeenCalledWith('/api/commerce/marketplace-accounts/a2/listing-syncs')
  })

  it('renders the connection RequiredAction from the C3 catalog, separately from the sync block',async()=>{
    connectionStatus.current={...connectionStatus.current,authorizationState:'REAUTHORIZATION_REQUIRED',requiredAction:'REAUTHORIZE'}
    const user=userEvent.setup();render(<MemoryRouter><MarketplaceAccountsPage/></MemoryRouter>)
    await user.click(await screen.findByRole('button',{name:'Loja Um'}))
    expect(await screen.findByText('Autorização não é mais válida. Reautorize com o provedor.')).toBeInTheDocument()
    const sync=screen.getByRole('heading',{name:'Sincronização de anúncios'}).closest('section')!
    expect(sync).not.toHaveTextContent('Autorização não é mais válida')
  })

  it('does not break the S8C.1 controls: reauthorize and probe still use their dedicated endpoints',async()=>{
    const user=userEvent.setup();render(<MemoryRouter><MarketplaceAccountsPage/></MemoryRouter>)
    await user.click(await screen.findByRole('button',{name:'Loja Um'}))
    api.post.mockResolvedValueOnce({ok:false,error:{safeMessage:'provider unavailable'}})
    await user.click(await screen.findByRole('button',{name:'Reautorizar'}));expect(api.post).toHaveBeenCalledWith('/api/commerce/marketplace-accounts/a1/reauthorize',{version:1})
    await user.click(screen.getByRole('button',{name:'Sondar disponibilidade'}));expect(api.post).toHaveBeenCalledWith('/api/commerce/marketplace-accounts/a1/probe',{version:1})
  })
})

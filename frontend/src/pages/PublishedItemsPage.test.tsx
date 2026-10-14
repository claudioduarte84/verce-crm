import { act,render,screen,waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter } from 'react-router-dom'
import { beforeEach,describe,expect,it,vi } from 'vitest'
import { PublishedItemsPage } from './PublishedItemsPage'
const api=vi.hoisted(()=>({get:vi.fn(),post:vi.fn(),put:vi.fn(),delete:vi.fn()}));vi.mock('../api/client',()=>({apiClient:api}))
const state=vi.hoisted(()=>({roles:['Operator'] as string[]}));vi.mock('../auth/useSession',()=>({useSession:()=>({session:{status:'authenticated',user:{roles:state.roles}}})}))
const listing={id:'l1',providerCode:'SHOPEE',marketplaceAccountId:'a1',accountName:'Loja',salesChannelId:'c1',channelCode:'SHOP',channelName:'Shopee',externalListingId:'ext-1',title:'Anúncio',observedPrice:35,observedStatus:'ACTIVE',linkageState:'UNLINKED',syncState:'NEVER_SYNCED',version:1}
function setup(){api.get.mockImplementation((p:string)=>{if(p.startsWith('/api/commerce/published-items'))return Promise.resolve({ok:true,data:{items:[listing],page:1,pageSize:20,total:1}});if(p==='/api/commerce/marketplace-accounts')return Promise.resolve({ok:true,data:[{id:'a1',providerCode:'SHOPEE',displayName:'Loja',salesChannelId:'c1'}]});if(p.startsWith('/api/products'))return Promise.resolve({ok:true,data:{items:[{id:'p1',code:'P1',name:'Produto'}]}});if(p.startsWith('/api/commerce/offers'))return Promise.resolve({ok:true,data:{items:[]}});return Promise.resolve({ok:true,data:[]})});api.post.mockResolvedValue({ok:true,data:{}})}
describe('PublishedItemsPage',()=>{beforeEach(()=>{vi.clearAllMocks();state.roles=['Operator'];setup()})
  it('renders unlinked listing facts',async()=>{render(<MemoryRouter><PublishedItemsPage/></MemoryRouter>);expect(await screen.findByText('Anúncio')).toBeInTheDocument();expect(screen.getByText('Produto não vinculado')).toBeInTheDocument();expect(screen.queryByText('Preço não observado')).not.toBeInTheDocument()})
  it('offers product-only reconciliation to Operator',async()=>{const user=userEvent.setup();render(<MemoryRouter><PublishedItemsPage/></MemoryRouter>);await user.click(await screen.findByRole('button',{name:'Vincular'}));await user.selectOptions(screen.getByLabelText('Produto'),'p1');expect(screen.getByRole('option',{name:'Somente produto'})).toBeInTheDocument()})
  it('hides reconciliation from Viewer',async()=>{state.roles=['Viewer'];render(<MemoryRouter><PublishedItemsPage/></MemoryRouter>);await screen.findByText('Anúncio');expect(screen.queryByRole('button',{name:'Vincular'})).not.toBeInTheDocument()})
  it('renders NEEDS_REVIEW as a first-class state',async()=>{api.get.mockImplementation((p:string)=>p.startsWith('/api/commerce/published-items')?Promise.resolve({ok:true,data:{items:[{...listing,linkageState:'NEEDS_REVIEW'}],page:1,pageSize:20,total:1}}):p==='/api/commerce/marketplace-accounts'?Promise.resolve({ok:true,data:[]}):p.startsWith('/api/products')?Promise.resolve({ok:true,data:{items:[]}}):Promise.resolve({ok:true,data:{items:[]}}));render(<MemoryRouter><PublishedItemsPage/></MemoryRouter>);expect(await screen.findByText('NEEDS_REVIEW')).toBeInTheDocument()})
  it('links with a compatible offer',async()=>{const user=userEvent.setup();api.get.mockImplementation((p:string)=>{if(p.startsWith('/api/commerce/published-items'))return Promise.resolve({ok:true,data:{items:[listing],page:1,pageSize:20,total:1}});if(p==='/api/commerce/marketplace-accounts')return Promise.resolve({ok:true,data:[{id:'a1',providerCode:'SHOPEE',displayName:'Loja',salesChannelId:'c1'}]});if(p.startsWith('/api/products'))return Promise.resolve({ok:true,data:{items:[{id:'p1',code:'P1',name:'Produto'}]}});return Promise.resolve({ok:true,data:{items:[{id:'o1',productId:'p1',salesChannelId:'c1',status:'ACTIVE',intendedUnitPrice:30}]}})});render(<MemoryRouter><PublishedItemsPage/></MemoryRouter>);await user.click(await screen.findByRole('button',{name:'Vincular'}));await user.selectOptions(screen.getByLabelText('Produto'),'p1');await user.selectOptions(screen.getByLabelText('Oferta compatível (opcional)'),'o1');await user.click(screen.getByRole('button',{name:'Confirmar vínculo'}));expect(api.post).toHaveBeenCalledWith('/api/commerce/published-items/l1/link',{productId:'p1',channelOfferId:'o1',version:1})})
  it('unlinks an already linked item',async()=>{const user=userEvent.setup();api.get.mockImplementation((p:string)=>p.startsWith('/api/commerce/published-items')?Promise.resolve({ok:true,data:{items:[{...listing,linkageState:'LINKED',productId:'p1',productCode:'P1',productName:'Produto'}],page:1,pageSize:20,total:1}}):p==='/api/commerce/marketplace-accounts'?Promise.resolve({ok:true,data:[]}):p.startsWith('/api/products')?Promise.resolve({ok:true,data:{items:[]}}):Promise.resolve({ok:true,data:{items:[]}}));render(<MemoryRouter><PublishedItemsPage/></MemoryRouter>);await user.click(await screen.findByRole('button',{name:'Desvincular'}));expect(api.post).toHaveBeenCalledWith('/api/commerce/published-items/l1/unlink?version=1')})
  it('sends every selected filter to the server',async()=>{const user=userEvent.setup();render(<MemoryRouter><PublishedItemsPage/></MemoryRouter>);await screen.findByText('Anúncio');await user.type(screen.getByLabelText('Buscar'),'SKU');await user.selectOptions(screen.getByLabelText('Provedor'),'SHOPEE');await user.selectOptions(screen.getByLabelText('Conta'),'a1');await user.selectOptions(screen.getByLabelText('Canal'),'c1');await user.selectOptions(screen.getByLabelText('Status'),'ACTIVE');await user.selectOptions(screen.getByLabelText('Vínculo'),'UNLINKED');await user.selectOptions(screen.getByLabelText('Sincronização'),'NEVER_SYNCED');await user.click(screen.getByRole('button',{name:'Filtrar'}));await waitFor(()=>expect(api.get).toHaveBeenCalledWith(expect.stringContaining('provider=SHOPEE')));for(const fragment of ['marketplaceAccountId=a1','salesChannelId=c1','observedStatus=ACTIVE','linkage=UNLINKED','syncState=NEVER_SYNCED'])expect(api.get).toHaveBeenCalledWith(expect.stringContaining(fragment))})
})

// ---- S8C.2 C5: observed-listing metadata, stock non-authority, explicit SKU and "Confirmar SKU" ----
const CONFIRM='Confirmar SKU → Produto para esta conta'
const linkedAuto={...listing,id:'l2',externalListingId:'MLB-2',externalSku:'SKU-2',linkageState:'LINKED',productId:'p1',productCode:'P1',productName:'Produto',linkageSource:'DETERMINISTIC_SKU',providerNativeStatus:'paused:out_of_stock',variationCount:0,listingUrl:'https://produto.mercadolivre.com.br/MLB-2'}
const linkedManual={...listing,id:'l3',externalListingId:'MLB-3',externalSku:'SKU-3',linkageState:'LINKED',productId:'p1',productCode:'P1',productName:'Produto',linkageSource:'MANUAL',variationCount:4}
function serve(items:unknown[],overrides?:(p:string)=>unknown){api.get.mockImplementation((p:string)=>{const o=overrides?.(p);if(o!==undefined)return o as Promise<unknown>
  if(p.startsWith('/api/commerce/published-items'))return Promise.resolve({ok:true,data:{items,page:1,pageSize:20,total:items.length}})
  if(p==='/api/commerce/marketplace-accounts')return Promise.resolve({ok:true,data:[{id:'a1',providerCode:'MERCADO_LIVRE',displayName:'Loja',salesChannelId:'c1'}]})
  if(p.startsWith('/api/products'))return Promise.resolve({ok:true,data:{items:[{id:'p1',code:'P1',name:'Produto'}]}})
  return Promise.resolve({ok:true,data:{items:[]}})})}
const refusal=(status:number,safeMessage:string,problem:Record<string,unknown>={})=>({ok:false,error:{status,kind:'problem',safeMessage,problem:{status,...problem}}})
const GENERIC409='Esta ação não pôde ser concluída porque os dados mudaram. Atualize a página e tente novamente.'

describe('PublishedItemsPage — S8C.2 observed listing delta',()=>{
  beforeEach(()=>{vi.clearAllMocks();state.roles=['Operator'];api.post.mockResolvedValue({ok:true,status:201,data:{}})})

  it('shows the exact read-only header',async()=>{serve([listing]);render(<MemoryRouter><PublishedItemsPage/></MemoryRouter>);await screen.findByText('Anúncio');expect(screen.getByText('Anúncios observados nos provedores (somente leitura).')).toBeInTheDocument()})

  it('shows "Estoque: não sincronizado" on every row, linked or not, with no quantity or stock endpoint',async()=>{
    serve([listing,linkedAuto,linkedManual]);render(<MemoryRouter><PublishedItemsPage/></MemoryRouter>);await screen.findByText('Anúncio: MLB-2')
    expect(screen.getAllByText('Estoque: não sincronizado')).toHaveLength(3)
    expect(document.body.textContent).not.toMatch(/disponível|quantidade|em estoque|saldo|reserva/i)
    expect(api.get.mock.calls.some(([p])=>/stock|estoque|inventory/i.test(String(p)))).toBe(false)
    expect(api.post).not.toHaveBeenCalled()
  })

  it('renders provider native status, variation count (zero included) and never treats variations as stock',async()=>{
    serve([linkedAuto,linkedManual]);render(<MemoryRouter><PublishedItemsPage/></MemoryRouter>);await screen.findByText('Anúncio: MLB-2')
    expect(screen.getByText('Status no provedor: paused:out_of_stock')).toBeInTheDocument()
    expect(screen.getByText('Variações: 0')).toBeInTheDocument();expect(screen.getByText('Variações: 4')).toBeInTheDocument()
  })

  it('omits native status and variations when the server sent none',async()=>{serve([listing]);render(<MemoryRouter><PublishedItemsPage/></MemoryRouter>);await screen.findByText('Anúncio');expect(screen.queryByText(/Status no provedor/)).not.toBeInTheDocument();expect(screen.queryByText(/Variações/)).not.toBeInTheDocument()})

  it('links the listing URL only for http(s), in a new tab with noopener noreferrer',async()=>{
    serve([linkedAuto]);render(<MemoryRouter><PublishedItemsPage/></MemoryRouter>)
    const link=await screen.findByRole('link',{name:'Abrir anúncio no provedor'})
    expect(link).toHaveAttribute('href','https://produto.mercadolivre.com.br/MLB-2');expect(link).toHaveAttribute('target','_blank');expect(link).toHaveAttribute('rel','noopener noreferrer')
  })

  it.each([['absent',undefined],['null',null],['empty',''],['javascript scheme','javascript:alert(1)'],['data scheme','data:text/html,<b>x</b>'],['relative','/MLB-2'],['malformed','http//x'],['ftp','ftp://x.test/a']])('renders no link for %s listingUrl',async(_n,url)=>{
    serve([{...linkedAuto,listingUrl:url}]);render(<MemoryRouter><PublishedItemsPage/></MemoryRouter>);await screen.findByText('Anúncio: MLB-2')
    expect(screen.queryByRole('link',{name:'Abrir anúncio no provedor'})).not.toBeInTheDocument();expect(document.querySelector('a[href^="javascript"]')).toBeNull();expect(document.querySelectorAll('a[target="_blank"]')).toHaveLength(0)
  })

  it('shows the linkage-source badge only for LINKED rows, with the frozen texts',async()=>{
    serve([listing,linkedAuto,linkedManual,{...listing,id:'l4',externalListingId:'MLB-4',linkageState:'NEEDS_REVIEW',linkageSource:'MANUAL'}]);render(<MemoryRouter><PublishedItemsPage/></MemoryRouter>);await screen.findByText('Anúncio: MLB-2')
    expect(screen.getAllByText('Vinculado automaticamente por mapeamento de SKU confirmado')).toHaveLength(1);expect(screen.getAllByText('Vinculado manualmente')).toHaveLength(1) // not for the UNLINKED or NEEDS_REVIEW rows
  })

  it('shows externalSku as its own field and never falls back to the listing id',async()=>{
    serve([{...listing,externalSku:undefined},{...listing,id:'l5',externalListingId:'MLB-5',externalSku:'  '},linkedAuto]);render(<MemoryRouter><PublishedItemsPage/></MemoryRouter>);await screen.findByText('Anúncio: MLB-2')
    expect(screen.getAllByText('SKU não informado')).toHaveLength(2);expect(screen.getByText('SKU: SKU-2')).toBeInTheDocument()
    expect(screen.queryByText('SKU: ext-1')).not.toBeInTheDocument();expect(screen.getByText('Anúncio: ext-1')).toBeInTheDocument()
  })

  it('renders safeSyncError through the C3 catalog with a code fallback and no raw payload',async()=>{
    serve([{...listing,safeSyncError:'LISTING_READ_FAILED'},{...listing,id:'l6',externalListingId:'x6',safeSyncError:'BRAND_NEW_CODE'}]);render(<MemoryRouter><PublishedItemsPage/></MemoryRouter>);await screen.findAllByText('Anúncio')
    expect(screen.getByText('Falha ao ler este anúncio.')).toBeInTheDocument();expect(screen.getByText('BRAND_NEW_CODE')).toBeInTheDocument();expect(screen.queryByText('LISTING_READ_FAILED')).not.toBeInTheDocument()
  })
})

describe('PublishedItemsPage — Confirmar SKU',()=>{
  beforeEach(()=>{vi.clearAllMocks();state.roles=['Operator'];api.post.mockResolvedValue({ok:true,status:201,data:{}})})
  const open=async(items:unknown[])=>{serve(items);const user=userEvent.setup();render(<MemoryRouter><PublishedItemsPage/></MemoryRouter>);await screen.findAllByText(/Anúncio:/);return user}

  it.each([['Owner'],['Operator']])('%s sees it only on LINKED rows that have an externalSku and a product',async(role)=>{
    state.roles=[role]
    await open([listing,{...linkedAuto,id:'n1',externalListingId:'MLB-NOSKU',externalSku:null},{...linkedAuto,id:'n2',externalListingId:'MLB-NOPROD',productId:undefined},{...linkedAuto,id:'n3',externalListingId:'MLB-REVIEW',linkageState:'NEEDS_REVIEW',productId:undefined},linkedManual])
    expect(screen.getAllByRole('button',{name:CONFIRM})).toHaveLength(1) // only the eligible MANUAL-linked row
  })

  it('is offered for an automatically linked row too (the SKU is a different authority than the link)',async()=>{await open([linkedAuto]);expect(screen.getByRole('button',{name:CONFIRM})).toBeInTheDocument()})

  it('Viewer gets no mutable control at all',async()=>{state.roles=['Viewer'];await open([linkedAuto]);for(const name of [CONFIRM,'Desvincular','Corrigir vínculo','Vincular'])expect(screen.queryByRole('button',{name})).not.toBeInTheDocument();expect(screen.getByText('Estoque: não sincronizado')).toBeInTheDocument()})

  it('does nothing on load: no confirmation is sent until the user clicks',async()=>{await open([linkedAuto]);expect(api.post).not.toHaveBeenCalled()})

  it('POSTs exactly {externalSku, productId} of the listing to its own account, then reloads without touching linkageSource locally',async()=>{
    const user=await open([linkedManual]);const before=api.get.mock.calls.filter(([p])=>String(p).startsWith('/api/commerce/published-items')).length
    await user.click(screen.getByRole('button',{name:CONFIRM}))
    expect(api.post).toHaveBeenCalledTimes(1)
    expect(api.post).toHaveBeenCalledWith('/api/commerce/marketplace-accounts/a1/sku-mappings',{externalSku:'SKU-3',productId:'p1'}) // no listing id, no replace fields, no user, no stock
    expect(await screen.findByText(/SKU SKU-3 confirmado para esta conta\. A regra vale apenas para observações futuras/)).toBeInTheDocument()
    await waitFor(()=>expect(api.get.mock.calls.filter(([p])=>String(p).startsWith('/api/commerce/published-items')).length).toBe(before+1))
    expect(screen.getByText('Vinculado manualmente')).toBeInTheDocument();expect(screen.queryByText('Vinculado automaticamente por mapeamento de SKU confirmado')).not.toBeInTheDocument() // the server still says MANUAL: no optimistic rewrite
  })

  it('does not link, unlink or replace anything as part of confirming',async()=>{
    const user=await open([linkedManual]);await user.click(screen.getByRole('button',{name:CONFIRM}))
    await screen.findByText(/confirmado para esta conta/)
    expect(api.post.mock.calls.map(([p])=>String(p))).toEqual(['/api/commerce/marketplace-accounts/a1/sku-mappings'])
  })

  it.each([
    [409,'SKU_MAPPING_CONFLICT','Já existe um mapeamento ativo para este SKU.'],
    [422,'SKU_MAPPING_PRODUCT_INELIGIBLE','Produto inexistente ou inativo.'],
    [422,'SKU_MAPPING_SKU_INVALID','SKU inválido.'],
  ])('%i %s uses the frozen text and shows no success',async(status,code,text)=>{
    const user=await open([linkedManual]);api.post.mockResolvedValueOnce(refusal(status,status===409?GENERIC409:'Entidade não processável',{code,title:code}))
    await user.click(screen.getByRole('button',{name:CONFIRM}))
    expect(await screen.findByText(text)).toBeInTheDocument();expect(screen.queryByText(/confirmado para esta conta/)).not.toBeInTheDocument()
    expect(document.body.textContent).not.toContain(code)
  })

  it('an unknown error code falls back to the safe client message, never raw backend text',async()=>{
    const user=await open([linkedManual]);api.post.mockResolvedValueOnce({ok:false,error:{status:500,kind:'unexpected',safeMessage:'Não foi possível concluir a operação. Tente novamente em instantes.',debugDetail:'System.Exception at Verce.Api'}})
    await user.click(screen.getByRole('button',{name:CONFIRM}))
    expect(await screen.findByText('Não foi possível concluir a operação. Tente novamente em instantes.')).toBeInTheDocument();expect(document.body.textContent).not.toMatch(/System\.Exception/)
  })

  it('blocks a second confirmation while one is in flight',async()=>{
    const user=await open([linkedManual,{...linkedManual,id:'l9',externalListingId:'MLB-9',externalSku:'SKU-9'}]);let finish:((v:unknown)=>void)|undefined
    api.post.mockImplementationOnce(()=>new Promise(resolve=>{finish=resolve}))
    await user.click(screen.getAllByRole('button',{name:CONFIRM})[0])
    for(const b of screen.getAllByRole('button',{name:CONFIRM}))expect(b).toBeDisabled()
    await act(async()=>{finish?.({ok:true,status:201,data:{}})})
    expect(api.post).toHaveBeenCalledTimes(1)
  })
})

describe('PublishedItemsPage — S8C.2 non-regression',()=>{
  beforeEach(()=>{vi.clearAllMocks();state.roles=['Operator'];api.post.mockResolvedValue({ok:true,data:{}})})

  it('manual link and unlink still work with their original endpoints and bodies',async()=>{
    serve([linkedManual,listing]);const user=userEvent.setup();render(<MemoryRouter><PublishedItemsPage/></MemoryRouter>)
    await user.click(await screen.findByRole('button',{name:'Desvincular'}));expect(api.post).toHaveBeenCalledWith('/api/commerce/published-items/l3/unlink?version=1')
    await user.click(screen.getAllByRole('button',{name:'Vincular'})[0]);await user.selectOptions(screen.getByLabelText('Produto'),'p1');await user.click(screen.getByRole('button',{name:'Confirmar vínculo'}))
    expect(api.post).toHaveBeenCalledWith('/api/commerce/published-items/l1/link',{productId:'p1',channelOfferId:null,version:1})
  })

  it('keeps filters and pagination, and never polls or contacts a provider',async()=>{
    vi.useFakeTimers({shouldAdvanceTime:true});try{
      serve(Array.from({length:3},(_,i)=>({...listing,id:`x${i}`,externalListingId:`ext-${i}`})));api.get.mockImplementation((p:string)=>p.startsWith('/api/commerce/published-items')?Promise.resolve({ok:true,data:{items:[{...listing}],page:1,pageSize:20,total:45}}):Promise.resolve({ok:true,data:p==='/api/commerce/marketplace-accounts'?[]:{items:[]}}))
      const user=userEvent.setup({advanceTimers:vi.advanceTimersByTime});render(<MemoryRouter><PublishedItemsPage/></MemoryRouter>)
      await screen.findByText('Anúncio');expect(screen.getByRole('button',{name:'Anterior'})).toBeDisabled()
      await user.selectOptions(screen.getByLabelText('Status'),'ACTIVE');await user.click(screen.getByRole('button',{name:'Filtrar'}));await waitFor(()=>expect(api.get).toHaveBeenCalledWith(expect.stringContaining('observedStatus=ACTIVE')))
      await user.click(screen.getByRole('button',{name:'Próxima'}));await waitFor(()=>expect(api.get).toHaveBeenCalledWith(expect.stringContaining('page=2')))
      const calls=api.get.mock.calls.length;await act(async()=>{await vi.advanceTimersByTimeAsync(60000)});expect(api.get.mock.calls.length).toBe(calls) // no polling
      expect(api.get.mock.calls.some(([p])=>/mercadolivre|mercadolibre|provider-api|listing-sync/i.test(String(p)))).toBe(false)
    }finally{vi.useRealTimers()}
  })
})

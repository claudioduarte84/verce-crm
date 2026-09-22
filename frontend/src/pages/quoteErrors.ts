import type { ApiError } from '../api/client'

/**
 * Quoting/PDF business errors all arrive as ProblemDetails with a stable `code` extension
 * (QuotingEndpoints.cs `Problem` helper). `apiClient`'s generic `safeMessage` collapses every
 * 409 to one "dados mudaram" sentence and every other 4xx to the backend's static pt-BR title —
 * neither is precise enough for codes like QUOTE_DIRECT_APPROVAL_NOT_ALLOWED (a business rule,
 * not a concurrency conflict). This maps the known stable codes to a specific pt-BR message and
 * falls back to `safeMessage` for anything else (including CSRF/auth failures, which carry no
 * `code` at all).
 */
const MESSAGES_BY_CODE: Record<string, string> = {
  CUSTOMER_NOT_FOUND: 'Cliente não encontrado.',
  QUOTE_ITEM_QUANTITY_INVALID: 'A quantidade de um dos itens é inválida.',
  PRODUCT_NOT_FOUND: 'Um dos produtos selecionados não foi encontrado.',
  RECIPE_INVALID: 'A receita de um dos produtos está incompleta ou inválida.',
  COST_BASIS_UNAVAILABLE: 'Não foi possível calcular o custo de um dos itens.',
  SUPPLY_NOT_FOUND: 'Um dos insumos usados na receita não foi encontrado.',
  QUOTE_ITEM_MANUAL_COST_REQUIRED: 'Informe um custo manual válido para o item avulso.',
  QUOTE_SALES_CHANNEL_NOT_FOUND: 'Canal de vendas não encontrado.',
  QUOTE_SALES_CHANNEL_INACTIVE: 'O canal de vendas selecionado está inativo.',
  QUOTE_SALES_CHANNEL_REQUIRED: 'Selecione um canal de vendas.',
  FEE_RULE_NOT_FOUND: 'Não há uma regra de taxas vigente para este canal.',
  FIXED_FEE_PRECISION_INVALID: 'A taxa fixa do canal não pôde ser aplicada com a precisão exigida.',
  QUOTE_ITEM_SOURCE_DUPLICATE: 'O mesmo item da revisão anterior foi referenciado mais de uma vez.',
  QUOTE_ITEM_SOURCE_NOT_FOUND: 'Um dos itens referenciados não existe mais na revisão atual.',
  QUOTE_ITEM_SOURCE_PRODUCT_IMMUTABLE: 'Não é possível trocar o produto de um item existente — remova o item e adicione um novo.',
  CONCURRENCY_CONFLICT: 'Este orçamento foi alterado por outra pessoa enquanto você editava. Atualize a página e tente novamente.',
  QUOTE_REVISION_ALREADY_DECIDED: 'Esta revisão já foi decidida (aprovada, cancelada, expirada ou substituída) e não pode mais ser alterada.',
  QUOTE_DIRECT_APPROVAL_NOT_ALLOWED: 'Esta revisão precisa ser enviada e negociada antes de poder ser aprovada diretamente.',
  QUOTE_REVISION_EXPIRED: 'Esta revisão expirou e não pode mais ser aprovada.',
  QUOTE_HAS_NO_ITEMS: 'Adicione ao menos um item antes de aprovar o orçamento.',
  QUOTE_ITEM_INVALID_PRICE: 'Um dos itens está com preço inválido (zero ou negativo).',
  PRODUCTION_ORDER_IN_PROGRESS: 'Já existe uma ordem de produção em andamento para este orçamento.',
  QUOTE_INVALID_TRANSITION: 'Esta ação não é permitida no status atual do orçamento.',
  QUOTE_CANCEL_REASON_REQUIRED: 'Informe o motivo do cancelamento.',
  QUOTE_PERIOD_RANGE_INVALID: 'O período informado é inválido.',
  PDF_RENDER_EMPTY: 'Não foi possível gerar o PDF do orçamento. Tente novamente.',
  PDF_RENDER_TOO_LARGE: 'O PDF gerado excedeu o tamanho permitido. Contate o suporte.',
  PDF_RENDER_INVALID_SIGNATURE: 'Não foi possível gerar o PDF do orçamento. Tente novamente.',
}

export function quoteErrorMessage(error: ApiError): string {
  const code = error.problem?.code
  if (typeof code === 'string' && code in MESSAGES_BY_CODE) return MESSAGES_BY_CODE[code]
  return error.safeMessage
}

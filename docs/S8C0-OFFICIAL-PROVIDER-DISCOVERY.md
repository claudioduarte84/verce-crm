# S8C.0 — Official marketplace provider discovery

Research date: **2026-09-24**. Market: **Brazil**, with Mercado Livre site `MLB`, Brazilian Shopee shops, and Brazilian local TikTok Shop sellers. This is an architecture input, not an account entitlement test or a change to the S8B capability seed. `SUPPORTED` means an official API meets the stated minimum semantics in this market; actual use still requires app approval, seller grant, and runtime availability. `UNKNOWN` means the evidence is insufficient, never that the provider lacks the feature. No `UNSUPPORTED` decision was justified by the available official material.

Read this with [ADR-0023](architecture/ADR-0023-s8b-commerce-foundation.md), the [capability matrix](S8C0-CAPABILITY-MATRIX.md), [auth and security](S8C0-PROVIDER-AUTH-AND-SECURITY.md), [sync and limits](S8C0-SYNC-AND-RATE-LIMITS.md), and [architecture proposal](S8C0-INTEGRATION-ARCHITECTURE-PROPOSAL.md). Source IDs below link directly to official material. The same IDs are used throughout the package.

## Mercado Livre — official findings for MLB

- **Identity and registration.** Mercado Livre developer applications authorize each seller by OAuth authorization code. The seller's `user_id` identifies the account; an app token alone does not authorize seller resources. App registration, redirect URI, access scopes and seller consent are documented in [ML-AUTH]. Access token expiry and rotating, single-use refresh tokens require serialized renewal. App review and exact account-specific entitlements must be checked during onboarding; no credentials were used here.
- **Listings.** Seller item search and item detail expose `item_id`, status, title, category, price, available quantity, pictures, attributes, variations and seller SKU where provided [ML-ITEMS], [ML-PUBLISH]. `item_id` is external listing identity, while a user-product identity can also appear in the newer seller flow [ML-USERPRODUCT]. Listing create/update, status and variant operations exist [ML-PUBLISH], [ML-UPDATE], [ML-VARIANTS]. Category rules, moderation, catalog matching and progressive User Products rollout make publication state account dependent. A successful write must be reconciled by a later read; command acceptance is not proof of a live listing.
- **Orders.** Seller search and detail expose order IDs, line quantity, prices, `sale_fee`, discounts through a separate resource, payment and shipment links, and timestamps [ML-ORDERS], [ML-DISCOUNTS]. Use the **order ID**, scoped by `MarketplaceAccountId`, for `Sale.ExternalOrderId`; pack, shipment and payment IDs remain related identities [ML-PACKS]. Search documents a 12-month history ceiling and filtering of canceled orders, so an import from search alone cannot prove full interval coverage or all cancellations [ML-ORDERS]. Late changes need overlap and detail re-fetch.
- **Fees.** `GET /sites/{site}/listing_prices` quotes selling charges before a sale, with price, listing type, category and other inputs [ML-FEES]. The response includes total and component amounts. The fixed fee is already included in the sale fee total; adding it again would double count. Logistics, promotion, tax and seller-specific conditions may affect realized economics. This is a *quote*, not the actual fee on an order; order `sale_fee` is a separate realized fact [ML-ORDERS].
- **Analytics and ads.** Item/seller visits are available with limited date windows (150 days for the visits API) [ML-VISITS]. Product Ads read APIs expose campaign/ad performance and metrics such as impressions, clicks, cost and ROAS, conditional on advertising eligibility and authorization; ads use their own API version and may change independently of Commerce [ML-ADS]. Visit and ad metrics must stay distinct.
- **Shipping and stock.** Shipment detail and cost resources expose status, tracking/logistics and realized charges, depending on fulfillment flow [ML-SHIP]. Shipping quotes and actual shipment charges have different timing [ML-SHIP-COST]. Item and variant stock writes exist; multi-origin sellers may instead use User Products and warehouse-specific stock paths [ML-STOCK], [ML-USERPRODUCT]. Do not interpret all stock as one shop-wide integer or retry ambiguous writes blindly.
- **Notifications.** Topics include `orders_v2`, items and shipments. A notification carries a resource reference; consumers fetch current state rather than treating the notification as a complete snapshot. Delivery retries and a two-day missed-feed window mean notifications alone cannot establish completeness [ML-NOTIFY]. Webhook signing guarantees were not established from the accessed official pages.
- **Testing and errors.** Official test-user guidance covers development publication, but equivalence for orders, payments, shipping and advertising is unproven [ML-TEST]. HTTP 429 is documented, without a universal numeric quota; per-resource limits and backoff matter [ML-RATE]. See the sync document for retry classification.

## Shopee — current Brazilian capability remains unverified

The official [Shopee Open Platform developer guide][SH-PORTAL] and [documents portal][SH-DOCS] were located, but returned access restrictions to the public research surface on 2026-09-24. An older Shopee-hosted Open API guide [SH-OLD] proves only historical platform concepts in a different region and API generation; it cannot establish current Brazilian endpoints, signing, authorizations or feature support. Therefore all eight Brazil capability decisions are **UNKNOWN**, including fee quotation, ads and analytics. This is a documentation access limitation, not a finding of absence.

The next provider gate requires current, official Brazil-applicable Open Platform reference pages or provider-issued documentation for app registration, shop authorization and signing, each capability endpoint and scope, order history/cancellation behavior, quotas, webhook authentication, sandbox and retention. Until then, no Shopee adapter or seed-state change should be designed as if a named API were confirmed.

## TikTok Shop — official findings for Brazil

- **Market and testability.** The official Brazil workflow names the v202309 Get Order List/Detail, invoice upload, package document and warehouse APIs [TT-BR]. Brazil local full-function Development Shops support test products and forward/reverse order flows; cross-border test coverage is a separate question [TT-DEV]. BRL product-price precision and seller-price bounds are documented [TT-PRICE]. These establish a Brazilian Shop API presence, not blanket support for every global endpoint.
- **Orders and identity.** Brazil-specific order workflow confirms seller order read [TT-BR]. Use `order_id` plus the authorized shop/account identity; a package ID can group/split shipments and must not replace the order identity [TT-SPLIT]. The Brazil response may contain CPF and CPF holder name for invoicing. The exact history window, cancellation/refund completeness and late-update horizon remain unverified, so `ORDERS_READ` does not by itself imply `COMPLETE` Sales coverage.
- **Realized fees.** `GET /finance/202501/orders/{order_id}/statement_transactions` with `seller.finance.info` returns SKU-level sale, fee, commission, shipping, tax and refund transactions for all regions, with data only after 2023-07-01 [TT-FINANCE]. These are realized financial records, potentially later than order creation. They are **not** a pre-sale fee quote, so `FEES_QUOTE` remains UNKNOWN.
- **Shipping.** The Brazil workflow includes package shipping documents and warehouses [TT-BR]. Get Tracking supplies order logistics events with `seller.logistics` in the general reference [TT-TRACK], but its BR-local entitlement was not explicitly established. `SHIPPING_READ` remains UNKNOWN pending that proof, including planned versus actual shipping-charge semantics.
- **Products, inventory, analytics and ads.** Official product pricing guidance includes Brazil and global product API references include reading and audit state [TT-PRICE], [TT-PRODUCT]. Shop product-performance APIs exist with `data.shop_analytics.public.read` [TT-ANALYTICS]. However, the researched references did not explicitly confirm that each read/write/stock/analytics endpoint and its permission is enabled for **Brazilian local shops**. Those four capability cells remain UNKNOWN pending region-specific reference or official entitlement confirmation. TikTok Ads Business API is a distinct platform and does not establish a TikTok Shop seller-authorized `ADS_READ` port [TT-ADS]; that cell is UNKNOWN as well.
- **Versioning, notifications and errors.** v202309 and newer endpoints place the version in the path; endpoint versions and deprecation notices are independent [TT-VERSION]. Brazil invoice-status notification has a notification ID, shop ID, timestamp, order IDs and package ID [TT-INVOICE-WH]. It does not establish general webhook signing or order-event coverage. The common error model includes body code, message and request ID [TT-ERROR]. Generic numeric limits for this account/resource combination remain UNKNOWN.

## Evidence appendix

Every source below is **official**; accessed 2026-09-24. An endpoint listed here is a documented resource, not a call made by this mission. Current account grant and production behavior were not tested.

| ID | Official title and URL | Evidence used / relevant resource | Limit |
|---|---|---|---|
| ML-AUTH | [Autenticação e Autorização](https://developers.mercadolivre.com.br/autenticacao-e-autorizacao) | OAuth seller authorization, token renewal, scopes | Account-specific app approval unknown |
| ML-ITEMS | [Itens e buscas](https://developers.mercadolivre.com.br/itens-e-buscas) | `/users/{user_id}/items/search`, item discovery | User Products transition |
| ML-PUBLISH | [Publicar produtos](https://developers.mercadolivre.com.br/pt_br/publicacao-de-produtos) | `POST /items`, `GET /items/{id}`, item fields and test users | Category validation varies |
| ML-UPDATE | [Sincronização e modificação de publicações](https://developers.mercadolivre.com.br/pt_br/produto-sincronizacao-de-publicacoes) | Listing changes and lifecycle | Account/product flow differences |
| ML-VARIANTS | [Variações](https://developers.mercadolivre.com.br/pt_br/variacoes) | SKU/variant price and stock | Variant rules vary |
| ML-USERPRODUCT | [Preço por variação](https://developers.mercadolivre.com.br/pt_br/preco-variacao) | Progressive User Products rollout | Not all MLB sellers migrated |
| ML-STOCK | [Estoque multi-origem](https://developers.mercadolivre.com.br/pt_br/estoque-multi-origem) | `/user-products/{id}/stock/type/seller_warehouse` | Eligibility, CNPJ/warehouse restrictions |
| ML-ORDERS | [Gerenciar orders](https://developers.mercadolivre.com.br/pt_br/gerenciamento-de-vendas) | `/orders/search`, `/orders/{id}`, items/fees/status/history | Search ceiling/canceled filtering |
| ML-DISCOUNTS | [Gerenciar orders — descontos](https://developers.mercadolivre.com.br/pt_br/gerenciamento-de-vendas) | `GET /orders/{id}/discounts` | Verify exact discount breakdown per order |
| ML-PACKS | [Gestão de packs](https://developers.mercadolivre.com.br/pt_br/gestao-packs) | Pack versus order identity | Pack can contain multiple orders |
| ML-FEES | [Custos por vender](https://developers.mercadolivre.com.br/pt_br/comissao-por-vender) | `/sites/{site}/listing_prices`; pre-sale fee components | Not final transaction cost |
| ML-VISITS | [Visitas](https://developers.mercadolivre.com.br/recurso-visits) | Item/seller visits and date range | 150-day query-window limit |
| ML-ADS | [Product Ads para Catálogo e User Products: leitura](https://developers.mercadolivre.com.br/pt_br/product-ads-para-catalogo-e-user-products-leitura) | Advertising metrics and API v2 | Ads eligibility/approval separate |
| ML-SHIP | [Gerenciamento de envios](https://developers.mercadolivre.com.br/pt_br/gerenciamento-de-envios) | `/shipments/{id}`, costs, status | Cost detail differs by logistics mode |
| ML-SHIP-COST | [Custos de envio](https://developers.mercadolivre.com.br/pt_br/atributos/custos-de-envio) | Planned shipping-cost input | Quote versus actual distinction |
| ML-NOTIFY | [Notificações](https://developers.mercadolivre.com.br/produto-receba-notificacoes) | Topics, retries, missed feeds | Signature/ordering not confirmed |
| ML-RATE | [Rate limit — Erro 429](https://developers.mercadolivre.com.br/pt_br/usuarios-e-aplicativos/rate-limit-erro-429) | 429 and backoff | Universal numeric quota not published |
| ML-TEST | [Passos rápidos para publicar um imóvel de teste](https://developers.mercadolivre.com.br/pt_br/passos-rapidos-para-publicar-um-imovel-de-teste) | Official test-user example | Does not prove full order/ads sandbox |
| SH-PORTAL | [Shopee Open Platform developer guide](https://open.shopee.com/developer-guide/16) | Official portal location | Public access restricted during research |
| SH-DOCS | [Shopee Open Platform documents](https://open.shopee.com/documents) | Official reference portal location | Public access restricted during research |
| SH-OLD | [Shopee Open API integration guide (2020, Taiwan)](https://cdngarenanow-a.akamaihd.net/shopee/seller/seller_cms/b17e7e1846b98c422e4404f223b9f65f/%5BTW%5D%5BOpen%20API%5DAPI%E4%B8%B2%E6%8E%A5%E8%AA%AA%E6%98%8E%E4%BA%8B%E9%A0%85%20%282020_10_21%29_newnew.pdf) | Historical platform existence only | Not evidence for BR 2026 capability |
| TT-BR | [BR market: updated API workflow](https://partner.tiktokshop.com/docv2/page/br-market-updated-api-workflow-to-support-order-invoice-and-warehouse) | BR v202309 orders, invoices, shipping docs, warehouses | Does not describe all product APIs |
| TT-PRICE | [Product pricing](https://partner.tiktokshop.com/docv2/page/product-pricing) | Brazil BRL product/SKU pricing rules | No endpoint grant proof |
| TT-PRODUCT | [Get Product API update](https://partner.tiktokshop.com/docv2/page/q50o39n1) | `GET /product/202309/products/{id}`, audit | BR local availability not explicit |
| TT-ANALYTICS | [Get Shop Product Performance List](https://partner.tiktokshop.com/docv2/page/get-shop-product-performance-list-202405) | `/analytics/202405/shop_products/performance`, scope | BR local availability not explicit |
| TT-ADS | [TikTok API for Business](https://business-api.tiktok.com/gateway/docs/index) | Separate ads API platform | Does not establish Shop seller grant |
| TT-FINANCE | [Get Transactions by Order](https://partner.tiktokshop.com/docv2/page/get-transactions-by-order) | `/finance/202501/orders/{id}/statement_transactions` | Realized only; data after 2023-07-01 |
| TT-TRACK | [Get Tracking](https://partner.tiktokshop.com/docv2/page/get-tracking) | `/fulfillment/202309/orders/{id}/tracking` | Full cost data not established |
| TT-SPLIT | [Split Orders](https://partner.tiktokshop.com/docv2/page/split-orders) | Brazil all-units package split | Package is not order ID |
| TT-DEV | [Development Shop manual order progression](https://partner.tiktokshop.com/docv2/page/ar5ppjvv) | BR local full-function development shops | Production parity varies by market |
| TT-VERSION | [API versioning](https://partner.tiktokshop.com/docv2/page/api-versioning) | Version path and retirement notices | Endpoint-specific versions |
| TT-INVOICE-WH | [Invoice status change](https://partner.tiktokshop.com/docv2/page/36-invoice-status-change) | BR notification identity and payload | Signature/retry guarantees not established |
| TT-ERROR | [Common errors](https://partner.tiktokshop.com/docv2/page/678e3a45786253031531b942) | Body code/message/request ID, rate/timeout | Per-endpoint limits unknown |

[ML-AUTH]: https://developers.mercadolivre.com.br/autenticacao-e-autorizacao
[ML-ITEMS]: https://developers.mercadolivre.com.br/itens-e-buscas
[ML-PUBLISH]: https://developers.mercadolivre.com.br/pt_br/publicacao-de-produtos
[ML-UPDATE]: https://developers.mercadolivre.com.br/pt_br/produto-sincronizacao-de-publicacoes
[ML-VARIANTS]: https://developers.mercadolivre.com.br/pt_br/variacoes
[ML-USERPRODUCT]: https://developers.mercadolivre.com.br/pt_br/preco-variacao
[ML-STOCK]: https://developers.mercadolivre.com.br/pt_br/estoque-multi-origem
[ML-ORDERS]: https://developers.mercadolivre.com.br/pt_br/gerenciamento-de-vendas
[ML-DISCOUNTS]: https://developers.mercadolivre.com.br/pt_br/gerenciamento-de-vendas
[ML-PACKS]: https://developers.mercadolivre.com.br/pt_br/gestao-packs
[ML-FEES]: https://developers.mercadolivre.com.br/pt_br/comissao-por-vender
[ML-VISITS]: https://developers.mercadolivre.com.br/recurso-visits
[ML-ADS]: https://developers.mercadolivre.com.br/pt_br/product-ads-para-catalogo-e-user-products-leitura
[ML-SHIP]: https://developers.mercadolivre.com.br/pt_br/gerenciamento-de-envios
[ML-SHIP-COST]: https://developers.mercadolivre.com.br/pt_br/atributos/custos-de-envio
[ML-NOTIFY]: https://developers.mercadolivre.com.br/produto-receba-notificacoes
[ML-RATE]: https://developers.mercadolivre.com.br/pt_br/usuarios-e-aplicativos/rate-limit-erro-429
[ML-TEST]: https://developers.mercadolivre.com.br/pt_br/passos-rapidos-para-publicar-um-imovel-de-teste
[SH-PORTAL]: https://open.shopee.com/developer-guide/16
[SH-DOCS]: https://open.shopee.com/documents
[SH-OLD]: https://cdngarenanow-a.akamaihd.net/shopee/seller/seller_cms/b17e7e1846b98c422e4404f223b9f65f/%5BTW%5D%5BOpen%20API%5DAPI%E4%B8%B2%E6%8E%A5%E8%AA%AA%E6%98%8E%E4%BA%8B%E9%A0%85%20%282020_10_21%29_newnew.pdf
[TT-BR]: https://partner.tiktokshop.com/docv2/page/br-market-updated-api-workflow-to-support-order-invoice-and-warehouse
[TT-PRICE]: https://partner.tiktokshop.com/docv2/page/product-pricing
[TT-PRODUCT]: https://partner.tiktokshop.com/docv2/page/q50o39n1
[TT-ANALYTICS]: https://partner.tiktokshop.com/docv2/page/get-shop-product-performance-list-202405
[TT-ADS]: https://business-api.tiktok.com/gateway/docs/index
[TT-FINANCE]: https://partner.tiktokshop.com/docv2/page/get-transactions-by-order
[TT-TRACK]: https://partner.tiktokshop.com/docv2/page/get-tracking
[TT-SPLIT]: https://partner.tiktokshop.com/docv2/page/split-orders
[TT-DEV]: https://partner.tiktokshop.com/docv2/page/ar5ppjvv
[TT-VERSION]: https://partner.tiktokshop.com/docv2/page/api-versioning
[TT-INVOICE-WH]: https://partner.tiktokshop.com/docv2/page/36-invoice-status-change
[TT-ERROR]: https://partner.tiktokshop.com/docv2/page/678e3a45786253031531b942

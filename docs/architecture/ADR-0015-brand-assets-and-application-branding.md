# ADR-0015 — Brand Assets and Application Branding

- **Status:** Accepted (pending external review)
- **Date:** 2026-09-07
- **Sprint:** S0 (addendum)

## Context

The addendum introduces brand identity as a first-class concern:

- the operator uploads logos (PNG now, SVG later);
- the **application** shows a configurable logo, compact logo and favicon;
- **documents** carry a logo configurable *independently* from the application's;
- a template may inherit the default logo, choose a specific asset, or render none;
- replacing an asset must **not** alter documents already issued;
- uploads must be validated — unsafe file types rejected, no path traversal, no executable
  content;
- nothing visual may be hard-coded in React components.

The company data feeding documents (`CompanyProfile`) also gains web/social fields.

## Decision

### 1. `CompanyProfile` replaces `OrganizationProfile`

One single-row aggregate in **Settings**, renamed to match the addendum's vocabulary and
extended with `website`, `instagram`, `whatsapp`. It supplies the `company.*` bindings to every
document. Fiscal modeling stays deliberately shallow ([PRODUCT-VISION §5](../PRODUCT-VISION.md)):
`document` is a single free field, not a fiscal regime model.

`timezone` and `currency` stay here, since they are organization-level facts.

### 2. Brand assets: asset → versions, and documents reference the **version**

```
BrandAsset (AR)                    BrandAssetVersion (E, immutable)
├── brand_asset_type_code          ├── version_number
├── name                           ├── file: sha256, path, content_type, size
├── is_active                      ├── width_px, height_px
├── current_version_id             ├── original_file_name
└── deleted_at                     ├── uploaded_at / uploaded_by
                                   └── is_current
```

`BrandAssetType` is a **lookup table, not a C# enum**: `PRIMARY_LOGO`, `COMPACT_LOGO`,
`NEGATIVE_LOGO`, `SYMBOL`, `FAVICON`, `DOCUMENT_LOGO`, `OTHER`. Adding a type is data.

**Replacing a logo creates a new version. Versions are immutable and are never deleted.**
This is the mechanism that satisfies "asset replacement must not mutate historical documents":

- a **template** stores the *intent* (inherit / a specific `brand_asset_id` / none);
- a **generated document** stores the *resolved* `brand_asset_version_id`.

So re-opening a proposal from March renders March's logo, while a new proposal renders today's —
the same intent/resolution split used for prices in
[ADR-0003](ADR-0003-cost-and-price-snapshots.md), applied to artwork. Full render-time freezing
is specified in [ADR-0016](ADR-0016-document-render-snapshots.md).

### 3. Roles: assignment, not hard-coded lookup

`settings.branding_assignment` maps a **role** to an asset:

| Role | Used by |
|---|---|
| `SYSTEM_LOGO` | application shell |
| `SYSTEM_LOGO_COMPACT` | collapsed sidebar, small viewports |
| `FAVICON` | browser tab |
| `DOCUMENT_DEFAULT_LOGO` | documents that inherit |

`UNIQUE (role)`. Roles are stable; assets behind them change freely. The application asks for
*the system logo*, never for *"the asset named VERCE horizontal"*.

### 4. Logo resolution chain

A `Logo` block declares `logoSource`:

```
INHERIT_DEFAULT → branding_assignment[DOCUMENT_DEFAULT_LOGO] → asset → current version
SPECIFIC_ASSET  → brand_asset_id                             → asset → current version
NONE            → render nothing
```

Resolution happens **at render time**, in the Application layer, and the resulting
`brand_asset_version_id` is frozen into the render snapshot. If an inherited assignment is
missing or its asset is soft-deleted, the render **fails loudly**
(`DOCUMENT_BRAND_ASSET_UNRESOLVED`) rather than emitting a broken image into a customer-facing
proposal.

This is what lets the app show a horizontal logo, a shipping label show the symbol, and a
proposal show the full signature — three roles, one asset library, no code change.

### 5. Application branding is resolved, never hard-coded

The frontend fetches branding from `/api/settings/branding` (product name `VERCE 3D`, subtitle
`Laboratório de Custos`, and the three asset URLs) and renders from that response.
**No `import logo from './logo.png'` anywhere** — enforced by a lint rule, in the same spirit as
the no-hard-coded-colours rule of [ADR-0014](ADR-0014-frontend-architecture-and-theming.md).

Bootstrap defaults ship as seeded VERCE assets, so a fresh install is branded rather than blank,
and the operator replaces them without touching code.

### 6. Upload validation pipeline

Every upload passes, in order, and fails closed:

1. **Size** ≤ 5 MB (configurable).
2. **Extension** against an allow-list — v1: `.png`, `.jpg`/`.jpeg`, `.webp`.
3. **Magic bytes** sniffed from the stream. The browser-supplied `Content-Type` is **advisory
   only** and never trusted.
4. **Real decode** via ImageSharp: it must parse as an image, within dimension limits
   (≤ 8000 px/side, guarding decompression bombs). Dimensions are captured here.
5. **Re-encode** to a canonical form, stripping EXIF and any embedded payload. The bytes stored
   are bytes this system produced, not bytes a stranger uploaded.
6. **Content-addressed filename**: `{sha256[0:2]}/{sha256}.{ext}`. The user's filename is
   retained as *metadata only* and never touches a path, which closes path traversal by
   construction rather than by sanitizing.
7. **Stored outside the web root**, served by an authorized endpoint with an explicit
   `Content-Type` and `X-Content-Type-Options: nosniff`.

### 7. SVG is deliberately excluded from v1

SVG is XML that can carry `<script>`, `<foreignObject>` and external references — a stored-XSS
vector in the app and an SSRF vector in the renderer.

Support is *architected for* without being *forced into* v1: validation is an
`IUploadValidator` chosen by content type, so adding SVG means adding a validator that parses
the XML, strips scripting and external references against an allow-list, and rejects anything
unrecognized. The decision to enable it is then one registration, reviewed on its own merits.
Shipping SVG now with "we'll sanitize later" is how a logo upload becomes an account takeover.

## Alternatives considered

- **Files on disk with paths in `app_setting`** — no versioning, so replacing a logo silently
  rewrites every historical document. Fails the addendum's core requirement.
- **One mutable asset row, overwritten on replace** — same failure.
- **Assets referenced by asset id from documents** — subtly wrong: the id survives, but the
  *content* behind it changes. Documents must reference the **version**.
- **Storing images as `bytea` in PostgreSQL** — simplifies backup consistency, and rejected:
  it bloats the database and the row cache for data that is served as static bytes. Files plus
  a content hash, backed up alongside the database, is the better trade at this scale.
- **Trusting `Content-Type` from the browser** — trivially forged; rejected.
- **Keeping the user's original filename** — the classic path-traversal and content-sniffing
  vector; rejected in favour of content addressing.
- **A separate `Branding` module** — considered and rejected: brand assets, company profile and
  app settings are one cohesive concern (organization identity), and a module per noun is the
  sophistication trap [ADR-0001](ADR-0001-system-architecture.md) warns about. They live in
  **Settings**, which Documents already depends on.

## Consequences

**Positive:** replacing a logo is safe by construction; app and document branding are
independent; uploads are validated at four layers; content addressing gives free deduplication
and integrity checking; adding an asset type or a role is data.

**Negative:**
- Asset versions are never deleted, so storage grows with every replacement. At logo scale
  (kilobytes, a handful of replacements per year) this is irrelevant, and it is the price of the
  historical guarantee.
- Re-encoding can subtly alter a PNG (colour profile handling); acceptable and preferable to
  storing untrusted bytes. Operators must be told the stored asset is normalized.
- Branding via an API call means a brief pre-branding paint on first load; mitigated by
  server-rendering the branding into the initial HTML shell.
- `OrganizationProfile` → `CompanyProfile` is a rename across the docs; done in this delivery so
  no code ever carries the old name.

## Compliance checks

- Test: uploading a `.exe` renamed to `.png` is rejected at the magic-byte stage.
- Test: uploading a PNG with a script payload appended survives re-encoding with the payload gone.
- Test: a filename of `../../evil.png` produces a content-addressed path containing no traversal.
- Test: replacing an asset creates a new version; the previous version's file is untouched and
  still resolvable.
- Test: a document generated before a logo replacement still renders the old logo.
- Test: an unresolvable inherited logo fails the render rather than emitting a broken image.
- Lint: no image import in `frontend/src/features/**`; branding comes from the API.

# Document render fonts

Self-hosted, repository-controlled font resources for the QUOTE document renderer
(`BlockTreeRenderer` §"theme CSS"). Resolves ARCHITECTURE-DEBT.md's "Font licensing for document
embedding" input — S7 exit requirement.

| File | Family | Weight | Used for |
|---|---|---|---|
| `Inter-Regular.woff2` | Inter | 400 | body text (`fontBody` theme token) |
| `Inter-Bold.woff2` | Inter | 700 | bold body text (totals, literal `Text` blocks) |
| `Archivo-Bold.woff2` | Archivo | 700 | headings/section titles (`fontHeading` theme token) |

Both typefaces are released under the SIL Open Font License 1.1 (`OFL.txt`, same license text for
both — Google Fonts republishes every font it serves under the license its own designer chose;
both Inter and Archivo ship under OFL). The OFL explicitly permits embedding font files inside a
larger software distribution and inside documents the software generates (OFL §"PERMISSION &
CONDITIONS" clause 2; the "cannot be sold by itself" restriction in clause 1 does not apply to a
generated PDF, which is a *document produced by* the software, not the Font Software itself).

Downloaded from Google Fonts' own CDN (`fonts.gstatic.com`), the "latin" subset only — Portuguese
(pt-BR) fits entirely inside the Latin-1 Supplement range (ã, õ, ç, á, à, â, é, ê, í, ó, ô, ú are
all U+00E0–U+00FA), so no `latin-ext`/`vietnamese`/`cyrillic` subset is needed and none is shipped.

## Why self-hosted, not a CDN `<link>`

`PlaywrightHtmlToPdfRenderer` blocks every outbound request the rendered page makes
(`page.RouteAsync("**/*", route => route.AbortAsync())` — SECURITY §8 defense in depth) and the
render environment (a production container) is not guaranteed to have internet egress at all.
A `@font-face` pointing at `fonts.googleapis.com` would therefore silently fall back to whatever
generic sans-serif the container happens to ship — different on every machine, never proven,
never reproducible. `BlockTreeRenderer` instead reads these three files at render time and embeds
each as a `data:font/woff2;base64,...` URI directly inside the `<style>` block it builds, so the
self-contained HTML this module hands to Chromium carries its own typography with zero external
dependencies — consistent with the same template's embedded/local-only logo data URIs.

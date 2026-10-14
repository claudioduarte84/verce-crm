# ADR-0029 — S8C.2 Gate 061 Clarifications (price exactness, enumeration NON_RETRYABLE, runtime effect, safe-read budget)

- Status: Accepted
- Date: 2026-10-13
- Sprint: S8C.2
- Complements: ADR-0025 §B.3, §E.3, §E.6, §F.4. Supersedes nothing. ADR-0026/0027/0028 unchanged.
- Decision source: VERCE3D-S8C2-OPUS-ARCH-062 (answering S8C2_GATE061_ARCHITECTURE_DECISION_REQUIRED)

## 1. ObservedPrice exactness (ADR-0025 §B.3)
"Scale ≤ 2" and "> 0" are properties of the exact JSON number supplied by the provider, evaluated on
its lexeme before any conversion. No rounding is ever applied. A positive BRL value is valid iff its
exact value has at most two significant fractional digits and at most 16 integer digits (exactly the
values representable in numeric(18,2)). Otherwise: zero or negative ⇒ null + PRICE_NOT_POSITIVE;
positive but not representable (excess scale, magnitude, or beyond any finite decimal) ⇒ null +
PRICE_PRECISION_UNSUPPORTED; non-BRL ⇒ null + PRICE_CURRENCY_UNSUPPORTED (checked first). Exactly one
warning per price; never an exception; trailing zeros are not information loss. Warnings remain an
in-memory diagnostic (never persisted, logged or exposed).

## 2. Enumeration NON_RETRYABLE (ADR-0025 §E.3, complementing the detail-chunk rule)
During ENUMERATING of a FULL run, a whole-call NON_RETRYABLE provider failure (any safe code other than
LISTING_ENUMERATION_CURSOR_INVALID, which keeps its restart rule) stops provider work with
LISTING_ENUMERATION_INCOMPLETE: run FAILED, no known-not-enumerated reconciliation, no detail read,
PENDING items become TRANSIENT_ERROR with that code, runtime availability untouched, run
requiredAction NONE (existing ADR-0027 row). A response that cannot be read is a failure to obtain a
complete ID set, not provider unavailability.

## 3. Runtime effect at finish (ADR-0025 §E.6 runtime paragraph)
"A run with ≥ 1 successful provider call ⇒ MarkAvailable" applies only to a run finishing without a
stop reason. PROVIDER_UNAVAILABLE / PROVIDER_RATE_LIMITED ⇒ UNAVAILABLE (unchanged). Every other stop
code leaves runtime availability unchanged.

## 4. Safe-read budget (ADR-0025 §F.4)
The 30 s budget is per safe-read operation (one executor invocation), measured on a monotonic clock
from before the first attempt. Each attempt window is min(10 s, remaining) and covers send, headers
and the bounded body read. Backoff and Retry-After waits consume the budget. No attempt starts with
remaining ≤ 0; a wait ≥ remaining is not taken and the last attempt's classification is returned
(Retry-After beyond the budget ⇒ RATE_LIMITED with RetryAfterUntil, as before). Window expiry after
SendAsync (including during the body) is RETRYABLE / SENT_OR_UNKNOWN / PROVIDER_UNREACHABLE;
§F.6 and the final-attempt cancellation rule are unchanged. The token POST's single 20 s window also
covers its body read (deadline after headers ⇒ existing SENT_OR_UNKNOWN, never replayed path).

## 5. Migration 20260927114823 already applied elsewhere
A database that applied the earlier text holds blank-padded markers only where the latest legacy
fingerprint was shorter than 64; such markers can never equal a V2 fingerprint and are never compared
to non-64 fingerprints, so behaviour is identical to NULL. No forward-only migration is required unless
a future feature reads the marker as data or byte-identity across environments is required; then a
new idempotent migration nulls markers whose trimmed length ≠ 64, without touching history.

## Consequences
No new public code, DTO, endpoint or catalog row. Debt rows S8C2-08, S8C2-09, S8C2-10 close with this ADR.

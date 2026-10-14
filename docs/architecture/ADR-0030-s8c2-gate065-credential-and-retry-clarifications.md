# ADR-0030 — S8C.2 Gate 065 Clarifications (stored credential material, local vs provider failures, Retry-After wait)

- Status: Accepted
- Date: 2026-10-13
- Sprint: S8C.2
- Complements: ADR-0025 §A.6, §F.3, §F.4, §F.6, §G.3, §G.4 and ADR-0029 §2, §4. Supersedes nothing. ADR-0024/0026/0027/0028 unchanged.
- Decision source: VERCE3D-S8C2-OPUS-ARCH-066 (answering S8C2_GATE065_ARCHITECTURE_DECISION_REQUIRED)

## 1. Stored credential material is validated by its adapter, before any request
A protected-store SUCCESS proves only that the confirmed envelope exists, is authentic and is bound to
the account, provider, reference, version and confirmed operation. It does not prove the adapter can
use the bytes. The adapter that owns the material format (ADR-0025 §A.6) validates it before it
materializes any request. Mercado Livre v1 material is usable for a bearer read (scan, bulk,
/users/me, grant probe, Owner probe) iff it parses as JSON, its root is an object, every present
string value of access_token, refresh_token and user_id decodes to valid text, access_token has 1 to
4096 characters each in U+0021..U+007E, and user_id is non-empty. Usability for refresh is unchanged
(ADR-0025 §F.3); a decoding failure in any of those fields makes the material unparseable.

## 2. Unusable stored material is a confirmed credential loss
The envelope is authenticated and bound to the confirmed version, so unusable content is deterministic
and permanent for that version: no reread, wait, retry or new sync can repair it; only a new confirmed
version can. It is classified with the existing loss vocabulary, never as a provider or enumeration
failure. On bearer read paths the adapter raises a typed internal "stored credential unusable" signal
before any request, and the credential executor translates it exactly like store NOT_FOUND /
CORRUPTED_OR_UNDECRYPTABLE into CREDENTIAL_CONFIRMED_LOST: listing sync stops with
CREDENTIAL_CONFIRMED_LOST (run requiredAction by the ADR-0027 §4.1 credential rule, base
REAUTHORIZE_AFTER_STORE_LOSS), runtime availability and the connection unchanged; the Owner probe takes
its existing confirmed-loss branch (REAUTHORIZATION_REQUIRED with PROBE_CREDENTIAL_UNREADABLE). Refresh
keeps its §F.3 row (no invocation, REAUTHORIZATION_REQUIRED, REFRESH_TOKEN_UNAVAILABLE). STORE_UNAVAILABLE
remains a transient outage (CREDENTIAL_STORE_UNAVAILABLE, CONTACT_ADMIN); a token the provider rejects
remains PROVIDER_CREDENTIAL_REJECTED. The store and its sweep stay provider-neutral and do not validate
adapter formats.

## 3. Local failures are never provider evidence
A failure that occurs entirely before SendAsync is invoked never yields a provider-call failure, a
provider stop code or a runtime change on the listing-read path. ADR-0029 §2 applies only to provider
failures (certainty SENT_AND_RESPONSE_RECEIVED or SENT_OR_UNKNOWN); stored material never reaches it.
ADR-0025 §F.6, the final-attempt cancellation rule and the §F.3 token-refresh matrix are unchanged.

## 4. Retry wait (ADR-0029 §4, executable form)
After a non-final attempt fails with RETRYABLE or RATE_LIMITED: base = 250 ms after attempt 1 and 500 ms
after attempt 2; jitter is one sample in [0, 100] ms; floor = RetryAfterUntil − now (wall clock) only for
a 429 that carried a valid Retry-After; d = max(base + jitter, floor), or base + jitter without a floor;
remaining = 30 s − monotonic time elapsed since before the first attempt. If d ≥ remaining no wait is
taken and the last attempt's classification is returned (a 429 keeps RetryAfterUntil). Otherwise the
executor waits d (caller cancellation during the wait ⇒ OperationCanceledException, no further attempt)
and the next window is min(10 s, remaining); remaining ≤ 0 ⇒ no attempt and the last classification.
There is no wait after the third attempt. Jitter is never added on top of a dominating floor.

## Consequences
No new public code, DTO, endpoint, OpenAPI shape or catalog row; ADR-0027 rows are unchanged and
CREDENTIAL_CONFIRMED_LOST additionally covers unusable stored material. The last-resort catch of
listing-sync execution is unchanged and tracked as debt S8C2-11; write-time transmissibility of
provider tokens is tracked as debt S8C2-12.

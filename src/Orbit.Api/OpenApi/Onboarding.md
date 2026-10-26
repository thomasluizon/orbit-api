# Anonymous onboarding setup

Tracks thomasluizon/orbit-tickets#1782. Deploy this additive producer before the
paired consumer adopts its OpenAPI revision and shared contract snapshot.

`POST /api/onboarding/suggest-setup` returns the existing `HabitSetupSuggestion`
object on HTTP 200. HTTP 429 has `{ "code": "ONBOARDING_LIMIT" }`; HTTP 503 has
`{ "code": "ONBOARDING_UNAVAILABLE" }`. Both codes select the existing local
parser drawing. Responses have `Cache-Control: no-store`. The endpoint never
creates account, habit, conversation, prompt, response or AI usage rows.
Authenticated credentials are ignored before authentication for these routes.

## Enrollment and request binding

1. Post `{ "platform": "web" }` to `/api/onboarding/challenge`. Platforms are
   `web`, `desktop`, `android` and `ios`. A configured verifier is mandatory.
   The response is `{ "challenge": "...", "expiresAtUtc": "..." }`. Challenges
   expire after five minutes and bind to the trusted connection IP.
2. Post `{ "challenge": "...", "attestation": { "platform": "web", "token": "..." } }`
   to `/api/onboarding/install`. For iOS, also supply the base64 `keyId`. A verified
   proof consumes the challenge and issues `{ "installId": "...", "expiresAtUtc": "..." }`.
   Failed enrollment consumes the challenge too. Install credentials expire after
   one hour; rotating a credential preserves its original expiry and quota subject.
3. Post `{ "title": "Read every day", "language": "en", "attestation": { ... } }`
   to `/api/onboarding/suggest-setup`, with the signed credential in
   `X-Orbit-Install-Id`. Language is `en` or `pt-BR`, defaulting to `en`.
   Input is limited to 100 UTF-16 code units before trimming. URLs and control
   characters are refused. On success, replace the credential with the response's
   `X-Orbit-Install-Id` header. Each credential is single-use, including failed
   attempts. A fallback ends the Astra attempt rather than retrying a spent credential.

For Android enrollment, set the standard Play Integrity `requestHash` to the
unpadded base64url SHA-256 of the challenge's UTF-8 bytes. For iOS enrollment,
`clientDataHash` is SHA-256 of those bytes. Retain the hardware key for assertions.
Web and desktop present a fresh, single-use Turnstile token from an explicitly
allowed widget hostname for enrollment and each setup request. Enrollment mints
the signed credential only after token verification, independently of the shared
bot-protection switch or smoke-account behavior.

For each Android or iOS setup request, construct these ASCII bytes, separated by
exactly three LF characters, with no final LF:

```text
orbit-onboarding-setup-v1
<signed install credential>
<standard padded base64 of the trimmed title's UTF-8 bytes>
<language>
```

Android uses the unpadded base64url SHA-256 of these bytes as `requestHash`.
iOS uses SHA-256 of these bytes as `clientDataHash` for `generateAssertion`.
This binds the one-time server challenge, sentence and language without relying
on equivalent JSON encoders producing identical escapes.

## Limits and privacy

`AppConfigs` keys `OnboardingIpCallsPerHour`, `OnboardingInstallCallsPerHour`,
`OnboardingCallsPerDay` and `OnboardingTokensPerDay` all default to zero. Missing,
nonpositive or insufficient values refuse model access. The IP ceiling is also
clamped to ten per hour. UTC windows are global operational windows, not user dates.
Configuration uses the existing per-process AppConfig cache, with a 30-minute TTL.

Each admitted attempt reserves one daily call and 16,384 daily tokens before
moderation. Reservations are conservative and are not refunded after moderation
or transport failure. The entire serialized input, 1,024 framing tokens and the
1,024-token output ceiling must fit that reservation. The completion uses `gpt-6-luna` at low effort,
strict JSON schema, no tools, no account context, no provider output storage and
no SDK retries. Invalid or incomplete output is dropped.

Only `DistributedRateLimitBuckets` are written, including expiring replay leases.
Partition keys are HMAC hashes. No title, response, raw IP or raw install credential
is written or logged. Public keys and assertion counters travel in authenticated
install credentials; consumed credentials prevent counter rollback and replay.
App Attest receipts are verified and discarded; no device fraud-history store is
introduced. Apple production attestations are accepted; development attestations
are refused. iOS extension values, when supplied by the platform, must match the
configured bundle versions and validation categories.

The dedicated `OnboardingSecurity:AllowedOrigins` CORS policy does not inherit
the shared app/agent origin list. All credentials and platform configuration are
empty by default and fail closed. Live Turnstile, Play Integrity and App Attest
enrollment require environment configuration and device acceptance before rollout.

Apple verification follows [server validation](https://developer.apple.com/documentation/devicecheck/validating-apps-that-connect-to-your-server),
the [attestation format guide](https://developer.apple.com/documentation/devicecheck/attestation-object-validation-guide)
and [receipt verification](https://developer.apple.com/documentation/devicecheck/assessing-fraud-risk).
The attestation trust anchor is pinned from [Apple Private PKI](https://www.apple.com/certificateauthority/private/).

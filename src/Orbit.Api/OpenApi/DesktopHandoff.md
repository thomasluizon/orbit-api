# Desktop session handoff

Tracks thomasluizon/orbit-tickets#1679. These append-only routes support both HTTP
versions. Deploy the API before enabling the desktop consumer in #1560 step 2.

`POST /api/auth/desktop/handoff` requires the browser's authenticated Orbit session.
It accepts `state`, `codeChallenge`, `codeChallengeMethod` (exactly `S256`) and
`redirectUri`, and returns `{ "code": "<opaque code>", "expiresIn": 60 }`.
The code contains 256 random bits and is persisted only as a SHA-256 hash. No
session credentials are returned by this route or placed in a callback URL.

`POST /api/auth/desktop/exchange` is anonymous and is called by the native shell.
It accepts `code`, `state`, `codeVerifier` and `redirectUri`. The verifier follows
[RFC 7636](https://www.rfc-editor.org/rfc/rfc7636.html): 43 to 128 unreserved ASCII
characters, with an S256 challenge of 43 base64url characters. State is preserved
exactly and must contain 1 to 512 characters, excluding whitespace-only values.

Exchange returns the existing `LoginResponse`: `userId`, `token`, `name`, `email`,
`wasReactivated`, `refreshToken`. It creates a new session for the bound account
without replaying signup or Google token storage. `wasReactivated` is false
because the browser sign-in has already performed any account reactivation.
Deleted or deactivated accounts cannot redeem a handoff.

All bindings and the strict 60-second expiry must match before the database can
consume the code. A conditional delete chooses at most one concurrent exchange;
consumption and session persistence share a transaction. Unknown, expired,
replayed and mismatched codes return HTTP 401 with the same generic error:
`Invalid or expired desktop handoff.` Malformed input returns HTTP 400 through
the validation pipeline with that same generic message. Invalid attempts do not
consume an otherwise valid code. Both routes use the existing `auth` rate policy.

`DesktopAuth:RedirectUri` configures the single exact allowed URI. The code
default and production Terraform value are
`org.useorbit.desktop://auth/callback`. The staging API environment group sets
`DesktopAuth__RedirectUri` to
`org.useorbit.desktop.staging://auth/callback`. No normalization, extra query
parameters, alternate paths or cross-environment callbacks are accepted.

Apply Terraform's environment-group configuration and deploy the migration and
API to staging and production before the shell resumes. The consumer repository
rebaselines its contract snapshot through its own workflow after producer rollout;
this ticket changes only the API repository.

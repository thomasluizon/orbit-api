# Apple integrations

## Contents

- [Configuration](#configuration)
- [Sign-in transport](#sign-in-transport)

All capabilities default to disabled. No Apple credentials are included in the repository.

## Configuration

Terraform owns these keys in the `orbit-production-api` and `orbit-staging-api`
environment groups. The plain values are in `infra/configuration.tf`, in
`production_api_values` and `staging_api_values`. Staging merges the production map,
so a staging entry overrides the production value for the same key. Each `PrivateKey`
is an SSM SecureString at `/orbit/<environment>/api/<key>` in `us-east-2`, listed in
`api_secret_keys`. Terraform reads these parameters; it does not create them.

Each private key contains the complete PEM text from Apple's `.p8` file, including its
header and footer. To change a value, edit the Terraform value or the SSM parameter.
Then review and apply a plan targeted to `render_env_group.production_api` or
`render_env_group.staging_api`, and release the API through the normal workflow.
Do not set these keys on the Render API service's Environment screen. A service-level
value overrides the linked group, so the Terraform value stops having an effect.
Never include credentials in logs, issues or pull requests.

| Capability | Environment variables | Apple console |
| --- | --- | --- |
| Push | `Apns__KeyId`, `Apns__TeamId`, `Apns__PrivateKey`, `Apns__Topic`, `Apns__UseSandbox` | Apple Developer, Certificates, Identifiers & Profiles, Keys: create an APNs signing key for the selected environment and topic. `Topic` is the app's bundle identifier. |
| Purchases | `AppStore__KeyId`, `AppStore__IssuerId`, `AppStore__PrivateKey`, `AppStore__BundleId`, `AppStore__AppAppleId`, `AppStore__MonthlyProductId`, `AppStore__YearlyProductId` | App Store Connect, Users and Access, Integrations, In-App Purchase: create the signing key. App Information supplies the numeric Apple ID. Monetization, Subscriptions supplies the exact configured product identifiers. |
| Sign-in | `AppleSignIn__KeyId`, `AppleSignIn__TeamId`, `AppleSignIn__PrivateKey`, `AppleSignIn__NativeClientId`, `AppleSignIn__WebClientId`, `AppleSignIn__RedirectUri`, `AppleSignIn__WebOrigin` | Apple Developer, Certificates, Identifiers & Profiles: enable Sign in with Apple on the primary app ID, group its Services ID with that app, register the web domain and the exact HTTPS return URL, and create its Sign in with Apple key. |
| MCP sign-in | `AppleSignIn__McpRedirectUri`, plus all sign-in variables | Register the API's exact `/oauth/apple/callback` HTTPS return URL on the same Services ID. |

`NativeClientId` is the native app's bundle identifier; `WebClientId` is its grouped
Services ID. `RedirectUri` ends in `/api/auth/apple/callback`. `WebOrigin` is only the
trusted web origin, without a path or query. Apple grouping must preserve the same
subject across native, web and MCP sign-in. Sandbox purchases are accepted and their
environment is persisted; local StoreKit test signatures and Family Sharing are rejected.

In App Store Connect, App Information, App Store Server Notifications, configure both
the production and sandbox URLs as the API's `/api/subscriptions/app-store/notifications`
endpoint with version 2 notifications. Confirm setup with Apple's test notification,
a sandbox purchase and renewal, a refund, and duplicate notification delivery.
The test notification is acknowledged after signature and application verification.
An App Store subscription status and profile return `billingProvider: "app_store"`;
legacy `source` and `subscriptionSource` remain null. Their existing enums remain valid.

For push, register an `apns` device and confirm a title, body and URL arrive on the
selected APNs environment. A missing configuration logs a warning when the sender
is resolved. FCM and Web Push retain their own configuration and delivery paths.

For sign-in, confirm native and web sign-ins for a verified matching email return
the same Orbit user ID, then confirm a relay email creates its own account. Confirm
MCP reaches the existing consent page. Account deletion queues token revocation;
successful revocation clears the stored encrypted refresh token. Its job retries
provider failures and skips active or already completed accounts. The existing
`Encryption__Key` protects refresh tokens and OAuth state payloads at rest.

Each capability is off when its `KeyId` is blank. `AppStoreSettings.IsConfigured`,
`AppleSignInSettings.IsConfigured` and `ApnsSettings.IsConfigured` each require a
nonblank `KeyId`. To turn off one capability, follow these steps:

1. In `infra/configuration.tf`, set `AppStore__KeyId`, `AppleSignIn__KeyId` or
   `Apns__KeyId` to `""` in that environment's map.
2. For production only, add the current value to `staging_api_values` if staging
   has no entry for that key. Otherwise staging inherits the blank value.
3. Review and apply a plan targeted to that environment's API group.

A blank `AppleSignIn__KeyId` also turns off MCP sign-in. To turn off only MCP sign-in,
set `AppleSignIn__McpRedirectUri` to `""`. A blank `Apns__KeyId` also stops desktop
APNs delivery. Restore the value and apply the same targeted plan to turn it on again.
Do not delete an SSM parameter: the Terraform data source read fails, and the plan
stops. Do not remove a key from the Render group in the dashboard: the next apply
restores it. The other Apple capabilities, Stripe, Play, FCM and Web Push keep their
independent configuration. App Store subscriptions are managed by the customer;
account deletion does not submit App Store cancellation requests.
The sign-in service reads `AppleSignIn__TokenUrl`, `AppleSignIn__KeysUrl` and
`AppleSignIn__RevokeUrl` from configuration, with Apple's endpoints as defaults.
No environment override is required. A real response from
[Apple's discovery endpoint](https://appleid.apple.com/.well-known/openid-configuration)
confirms `token_endpoint`, `jwks_uri` and `revocation_endpoint` as
`https://appleid.apple.com/auth/token`, `https://appleid.apple.com/auth/keys` and
`https://appleid.apple.com/auth/revoke`, respectively. The service continues to
validate the fixed Apple issuer and audience. Endpoint configuration does not
change the trusted identity provider.

## Sign-in transport

`POST /api/auth/apple/native` accepts `identityToken`, `authorizationCode`,
`rawNonce`, optional `givenName` and `familyName`, `language` and optional
`referralCode`. The native authorization request must send the lowercase SHA-256
hex digest of `rawNonce` as its nonce. Both the submitted token and the code
exchange's token must verify with Apple's public keys, the configured native
audience and that nonce. The authorization code provides single-use protection.

Web clients call `POST /api/auth/apple/start` with a randomly generated `state`
(at least 16 characters), `language` and optional `referralCode`, and navigate to
the returned `authorizationUrl`. The API generates an independent state and nonce
for Apple, retaining the client state in encrypted server storage for five minutes.
Apple posts the code and state to the callback. The callback exchanges the code,
checks the nonce, signs in and returns HTTP 303 to
`{WebOrigin}/auth-callback#provider=apple&handoff=...&state=...`.
The client checks its original state before posting `handoff` and `state` to
`/api/auth/apple/handoff`. A handoff lasts 60 seconds and can be consumed once.
No callback cookie or session token in the redirect URL is required.

MCP uses its own configured callback and the same verified identity flow, then
creates an existing MCP consent ticket. It preserves the requesting client's
redirect, state, PKCE challenge, nonce and scopes in encrypted server storage.

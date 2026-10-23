# Apple integrations

Tracks thomasluizon/orbit-tickets#1559. Deploy this API before its iOS consumers.
All capabilities default to disabled. No Apple credentials are included in the repository.

## Configuration

Set environment variables in the Render API service's Environment screen. Apply them
through the normal API deployment. Keys contain the complete PEM text from Apple's
`.p8` file, including its header and footer. Keep signing keys in Render environment
secrets and the corresponding GitHub Actions environment secrets when deployment
needs them. Never include credentials in logs, issues or pull requests.

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

Remove the capability's `PrivateKey` variable and apply the environment change to
turn it off. The other Apple capabilities, Stripe, Play, FCM and Web Push keep their
independent configuration. App Store subscriptions are managed by the customer;
account deletion does not submit App Store cancellation requests.

## Provider coverage and refund reversals

Each provider retains its verified paid-through instant, interval and pending lapse
reason. The shared entitlement projects the longest currently valid paid term.
A shorter provider update cannot replace it, and a refund or cancellation falls
back to another provider's still-valid coverage. Provider bindings remain linked
while that coverage is active. The `PreserveProviderPaidCoverage` migration adds
nullable columns and backfills the currently selected provider from the existing
subscription projection. Other linked providers acquire coverage on their next
verified update; the migration does not infer terms from identifiers.

`REFUND_REVERSED` reconciles the verified current subscription state before its
UUID is recorded, using the same account binding checks as renewals. Apple's
[notification type documentation](https://developer.apple.com/documentation/appstoreservernotifications/notificationtype)
identifies the reversal and requires reinstatement after a refund. Its refund
event table has an empty subtype cell for `REFUND_REVERSED`; no subtype is required.
The complete table and possible values can be rederived from
[Apple's documentation JSON](https://developer.apple.com/tutorials/data/documentation/appstoreservernotifications/notificationtype.json).

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
`{WebOrigin}/auth-callback?provider=apple&handoff=...&state=...`.
The client checks its original state before posting `handoff` and `state` to
`/api/auth/apple/handoff`. A handoff lasts 60 seconds and can be consumed once.
No callback cookie or session token in the redirect URL is required.

MCP uses its own configured callback and the same verified identity flow, then
creates an existing MCP consent ticket. It preserves the requesting client's
redirect, state, PKCE challenge, nonce and scopes in encrypted server storage.

## Protocol evidence

The implementation uses the documented HTTP protocols and .NET cryptography.
The NuGet packages considered were
[dotAPNS 4.6.0](https://github.com/alexalok/dotAPNS/blob/ed3dab7f85cb41732281ba0d3c7b255ed83ce51c/dotAPNS/ApnsClient.cs) and
[Mimo.AppStoreServerLibrary 0.1.1](https://github.com/getmimo/app-store-server-library-dotnet/blob/05fedc5f1c64506e50785ab7a561142da5a2fe92/src/SignedDataVerifier.cs).
Neither package includes installed source files. Their published repository revisions
were inspected: dotAPNS's response abstraction discards the HTTP status needed for
unconditional 410 cleanup; Mimo's verifier embeds decoded payloads in exceptions and
does not enforce Apple's signing certificate extensions. The direct implementation
keeps status handling, certificate purpose checks and sanitized failures explicit.

- [APNs connection](https://developer.apple.com/documentation/usernotifications/establishing-a-connection-to-apns): HTTP/2 streams have a variable server limit. One shared connection and an eight-request semaphore bound sends.
- [APNs token authentication](https://developer.apple.com/documentation/usernotifications/establishing-a-token-based-connection-to-apns): ES256, `kid`, `iss`, `iat`, Base64URL and the 20-to-60-minute refresh window. Tokens are reused for 50 minutes.
- [APNs request](https://developer.apple.com/documentation/usernotifications/sending-notification-requests-to-apns) and [response](https://developer.apple.com/documentation/usernotifications/handling-notification-responses-from-apns): sandbox/production hosts, device path, topic, alert push type, `reason`, 410, BadDeviceToken, 429 and server failures.
- [App Store JWT authorization](https://developer.apple.com/documentation/appstoreserverapi/generating-json-web-tokens-for-api-requests): ES256 key, issuer, audience, bundle identifier and short-lived request tokens.
- [Subscription statuses](https://developer.apple.com/documentation/appstoreserverapi/get-all-subscription-statuses), [response](https://developer.apple.com/documentation/appstoreserverapi/statusresponse), [group](https://developer.apple.com/documentation/appstoreserverapi/subscriptiongroupidentifieritem), [last transaction](https://developer.apple.com/documentation/appstoreserverapi/lasttransactionsitem) and [status values](https://developer.apple.com/documentation/appstoreserverapi/status): current state, signed transactions and renewals, grace and revoked states.
- [Transaction](https://developer.apple.com/documentation/appstoreserverapi/jwstransactiondecodedpayload) and [renewal](https://developer.apple.com/documentation/appstoreserverapi/jwsrenewalinfodecodedpayload) claims: bundle, product, account token, original transaction, environment, expiration, revocation and grace timestamps.
- [JWS certificate header](https://developer.apple.com/documentation/appstoreserverapi/jwsdecodedheader), [Apple's verifier](https://github.com/apple/app-store-server-library-python/blob/main/appstoreserverlibrary/signed_data_verifier.py) and [Apple PKI](https://www.apple.com/certificateauthority/): three-certificate chain, Apple signing-purpose OIDs and root trust. Production enables certificate revocation checks; generated test chains disable them only through an internal test constructor.
- [Notification envelope](https://developer.apple.com/documentation/appstoreservernotifications/responsebodyv2), [decoded payload](https://developer.apple.com/documentation/appstoreservernotifications/responsebodyv2decodedpayload), [data](https://developer.apple.com/documentation/appstoreservernotifications/data) and [types](https://developer.apple.com/documentation/appstoreservernotifications/notificationtype): signed payload, UUID, version, application, environment and nested transaction verification.
- [Rate limits](https://developer.apple.com/documentation/appstoreserverapi/identifying-rate-limits): subscription lookup is limited to 50 requests per second in production, with sandbox at one tenth of that limit. Provider errors retain local state and return a retriable notification failure.
- [Firebase sender source](https://github.com/firebase/firebase-admin-dotnet/blob/62553e0f375e77f40f479b7a3a0bbc50808f9216/FirebaseAdmin/FirebaseAdmin/Messaging/FirebaseMessagingClient.cs): the installed SDK's repository revision confirms the `name` success field used by the FCM isolation test. Its `AppOptions` source and installed Google HTTP factory documentation confirm the injectable test transport.
- [Apple discovery](https://appleid.apple.com/.well-known/openid-configuration) and [public keys](https://appleid.apple.com/auth/keys): actual responses confirmed issuer, endpoints, RS256 and JWKS `keys`, `kid`, `kty`, `use`, `alg`, `n` and `e`.
- [Identity claims](https://developer.apple.com/documentation/signinwithapplejs/authorizationi/id_token) and [verification](https://developer.apple.com/documentation/signinwithapple/verifying-a-user): issuer, subject, audience, timestamps, nonce and verified email.
- [Authorization request](https://developer.apple.com/documentation/signinwithapplerestapi/request-an-authorization-to-the-sign-in-with-apple-server.), [token validation](https://developer.apple.com/documentation/signinwithapplerestapi/generate-and-validate-tokens), [token response](https://developer.apple.com/documentation/signinwithapplerestapi/tokenresponse), [client secret](https://developer.apple.com/documentation/accountorganizationaldatasharing/creating-a-client-secret) and [revocation](https://developer.apple.com/documentation/signinwithapplerestapi/revoke-tokens): form encoding, callback fields, one-use code exchange, encrypted refresh token storage and empty successful revocation bodies.

Apple's Markdown representation omits some REST property tables. To rederive those
fields, read the corresponding JSON documentation at
`https://developer.apple.com/tutorials/data/documentation/{framework}/{symbol}.json`,
including `primaryContentSections` with `kind: "properties"` or `"possibleValues"`.
Tests construct the documented payloads and generate their own signing keys and
certificate chains. They do not use production tokens or invented provider claims.

The embedded public Apple Root CA G3 certificate is downloaded from
[Apple's certificate endpoint](https://www.apple.com/certificateauthority/AppleRootCA-G3.cer).
Its SHA-256 digest is
`63343abfb89a6a03ebb57e9b3f5fa7be7c4f5c756f3017b3a8c488c3653e9179`.

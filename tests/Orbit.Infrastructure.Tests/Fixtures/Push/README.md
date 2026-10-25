# Push protocol fixtures

`WnsTokenResponse.json` records the complete example response from Microsoft's
[Windows App SDK push quickstart](https://learn.microsoft.com/en-us/windows/apps/develop/notifications/push-notifications/push-quickstart#step-1-request-an-access-token),
with only the access token replaced. It is a documentation exchange, not a live capture.
The [Entra client credentials response schema](https://learn.microsoft.com/en-us/entra/identity-platform/v2-oauth2-client-creds-grant-flow#successful-response-1)
also specifies numeric `expires_in`; tests exercise both representations. The sender
reads only `access_token` and the optional cache lifetime `expires_in`.

Request assertions follow the quickstart's Entra request and raw notification exchange.
Delivery outcomes follow Microsoft's
[WNS response status table](https://learn.microsoft.com/en-us/windows/apps/develop/notifications/push-notifications/push-request-response-headers#response-codes).
APNs response fixtures follow Apple's
[response tables and BadDeviceToken exchange](https://developer.apple.com/documentation/usernotifications/handling-notification-responses-from-apns).
Live exchanges require the owner's Apple and Azure credentials and belong to
`thomasluizon/orbit-tickets#1560`.

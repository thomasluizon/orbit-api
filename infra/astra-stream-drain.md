# Astra stream shutdown drain

The API stops admitting HTTP requests when shutdown starts, returning HTTP 503 before
the controller can acknowledge or dispatch new chat work. Requests already admitted
retain their client disconnect token and have up to 240 seconds to finish. The drain
runs in the host's `StoppingAsync` phase, before Kestrel and other hosted services stop.
It tracks the entire HTTP pipeline, including the controller's terminal event flush.

The host shutdown allowance is 270 seconds. Only the production API Render service has
`max_shutdown_delay_seconds = 300`, leaving 30 seconds after the request drain for
hosted service cleanup and another 30 seconds before Render's process kill. The free
staging API service keeps Render's default shutdown delay; it has no configurable
shutdown delay setting and does not carry the production drain allowance. The Render
provider [schema](https://github.com/render-oss/terraform-provider-render/blob/v1.9.1/internal/provider/webservice/resource/schema.go)
declares the setting, and its
[update implementation](https://github.com/render-oss/terraform-provider-render/blob/v1.9.1/internal/provider/webservice/resource/internal/update.go)
sends it to the service API. Render documents the
[300 second maximum](https://render.com/docs/deploys#setting-a-shutdown-delay).

## Apply before releasing

Use the existing infrastructure credentials and local variable file. Review a plan
targeting only the production API service, then apply that saved plan:

```sh
terraform -chdir=infra init
terraform -chdir=infra plan -var-file=local.tfvars \
  -target=render_web_service.production_api \
  -out=local.astra-drain.tfplan
terraform -chdir=infra show -json local.astra-drain.tfplan | node infra/check-web-plan.mjs
terraform -chdir=infra apply local.astra-drain.tfplan
terraform -chdir=infra state show render_web_service.production_api
```

In the production API resource readback, verify `max_shutdown_delay_seconds = 300`.
The plan must preserve the production API identity, domains and runtime configuration,
and must not update the staging API or either web service. Follow the infrastructure
README's deploy handling instructions if the API settings update triggers a release
of its tracked branch. Release the
approved API commit through the existing release workflow. The host behavior protects
replacement of instances already running this code; it cannot change shutdown logic
inside an older instance during the first rollout.

## Bounded completion and retry

A successful drain preserves `started`, intermediate events and the terminal `final`
or `error` event. A client disconnect still cancels work immediately. If the drain
deadline or an earlier host stop deadline expires, remaining request tokens are
cancelled. Cancellation is cooperative, and an unresponsive dependency or a process
kill can still truncate a reply without a terminal event. A host stopped with a
shorter external deadline has that shorter allowance.

Transport failure still requires the client's explicit manual Retry. The API does
not queue or replay a request during shutdown and does not promise that an interrupted
write had no effects. Idempotent replay belongs to its separate ticket.

The API unit suite sends a real SIGTERM to a generic host while the real chat controller
has flushed `started`, holds the reply for 31 seconds after the signal, and verifies
that the host waits until `delta` and `final` are flushed. Separate tests cover late
request rejection, client disconnect, drain expiry and an earlier host deadline.
The suite uses in-memory HTTP contexts and mocked command dispatch; it starts no
server and does not claim to reproduce Render networking or a deployed Kestrel socket.

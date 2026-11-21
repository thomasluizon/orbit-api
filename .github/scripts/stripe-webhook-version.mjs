import { resolve } from 'node:path'
import { fileURLToPath } from 'node:url'

export const checkName = 'stripe-webhook-version'

/** The segment after the first dot of `<date>.<release>`, which Stripe.net calls the major release. */
export function releaseName(version) {
  return version.split('.')[1] ?? null
}

/**
 * The rule of Stripe.net `EventUtility.IsCompatibleApiVersion(sdkApiVersion, eventApiVersion)`, which
 * `EventUtility.ParseEvent` applies: the same version, or the same major release unless the library is a preview.
 * `stripe-webhook-version.cases.json` holds the rows that this test and the C# test both check.
 */
export function accepts(libraryVersion, endpointVersion) {
  if (endpointVersion === libraryVersion) return true
  const major = releaseName(libraryVersion)
  return major !== null && major !== 'preview' && releaseName(endpointVersion) === major
}

function stripeCheck(body, url) {
  const check = Array.isArray(body?.checks) ? body.checks.find(item => item?.name === checkName) : undefined
  if (!check) throw new Error(`${url} has no ${checkName} check`)
  return check
}

function describe(error) {
  const cause = error?.cause ? ` (${error.cause.message ?? error.cause})` : ''
  return `${error?.message ?? error}${cause}`
}

export async function readHealth(baseUrl, fetchImpl = fetch) {
  const url = `${baseUrl}/health/stripe`
  let response
  try {
    response = await fetchImpl(url, { signal: AbortSignal.timeout(30_000), redirect: 'error' })
  } catch (error) {
    throw new Error(`${url}: ${describe(error)}`)
  }
  if (response.status === 404) {
    await response.body?.cancel()
    return { url, missing: true }
  }
  try {
    return { url, missing: false, body: await response.json() }
  } catch (error) {
    throw new Error(`${url}: HTTP ${response.status} body is not JSON: ${describe(error)}`)
  }
}

/**
 * Compares the Stripe.net version that the released commit serves at libraryUrl with the webhook endpoint versions that
 * endpointUrl reads from its Stripe account. Production before a deploy reads its endpoints from the build that runs now.
 * That build can predate /health/stripe after a rollback, so a missing route on another URL only warns.
 */
export async function verify(libraryUrl, endpointUrl, sha, fetchImpl = fetch) {
  const library = await readHealth(libraryUrl, fetchImpl)
  if (library.missing) throw new Error(`${library.url} is missing; the released build must serve it`)
  if (library.body?.commit !== sha) throw new Error(`${library.url} serves commit ${library.body?.commit}, not ${sha}`)
  const libraryVersion = stripeCheck(library.body, library.url).data?.libraryApiVersion
  if (typeof libraryVersion !== 'string' || !libraryVersion) throw new Error(`${library.url} reports no libraryApiVersion`)

  const endpoint = endpointUrl === libraryUrl ? library : await readHealth(endpointUrl, fetchImpl)
  if (endpoint.missing) {
    return { warning: `${endpoint.url} is missing, so the endpoint version of ${endpointUrl} is not verified before the deploy; the check after the deploy verifies it` }
  }
  const check = stripeCheck(endpoint.body, endpoint.url)
  const versions = check.data?.endpointApiVersions
  if (!Array.isArray(versions)) throw new Error(`${endpoint.url} could not read the webhook endpoints: ${check.description}`)
  if (!versions.some(version => typeof version === 'string' && accepts(libraryVersion, version))) {
    throw new Error(`Stripe.net at ${sha} expects ${libraryVersion}, but the enabled webhook endpoints of ${endpointUrl} send [${versions.join(', ')}]. Create an endpoint at ${libraryVersion} and put its signing secret in Stripe__WebhookSecret before the release`)
  }
  return { message: `${endpointUrl} has a webhook endpoint that Stripe.net ${libraryVersion} accepts: [${versions.join(', ')}]` }
}

/** The one log line of a run, as a GitHub workflow command when it is a warning or an error. */
export async function outcome(libraryUrl, endpointUrl, sha, fetchImpl = fetch) {
  try {
    if (!libraryUrl || !endpointUrl || !/^[a-f0-9]{40}$/.test(sha ?? '')) {
      throw new Error('Usage: COMMIT_SHA=<sha> stripe-webhook-version.mjs <library-base-url> <endpoint-base-url>')
    }
    const result = await verify(libraryUrl, endpointUrl, sha, fetchImpl)
    return { failed: false, line: result.warning ? `::warning::${result.warning}` : result.message }
  } catch (error) {
    return { failed: true, line: `::error::${error.message}` }
  }
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const [libraryUrl, endpointUrl] = process.argv.slice(2)
  const { failed, line } = await outcome(libraryUrl, endpointUrl, process.env.COMMIT_SHA)
  if (failed) {
    console.error(line)
    process.exitCode = 1
  } else {
    console.log(line)
  }
}

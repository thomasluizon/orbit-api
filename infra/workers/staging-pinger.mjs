const healthUrl = "https://api-staging.useorbit.org/health"

function insideWindow(timestamp) {
  const hour = new Date(timestamp).getUTCHours()
  return hour >= 11 || hour < 3
}

export default {
  async scheduled(controller) {
    const startedAt = Date.now()
    const run = {
      event: "staging-health-ping",
      scheduledAt: new Date(controller.scheduledTime).toISOString(),
      startedAt: new Date(startedAt).toISOString(),
    }

    if (!insideWindow(controller.scheduledTime) || !insideWindow(startedAt)) {
      console.log({ ...run, outcome: "skipped-outside-window" })
      return
    }

    const abort = new AbortController()
    const timeout = setTimeout(() => abort.abort(), 90_000)
    let status

    try {
      const response = await fetch(healthUrl, {
        cache: "no-store",
        redirect: "manual",
        signal: abort.signal,
      })
      status = response.status
      await response.text()
      if (!response.ok) throw new Error(`Staging health returned HTTP ${status}`)

      console.log({ ...run, outcome: "healthy", status, durationMs: Date.now() - startedAt })
    } catch (error) {
      console.error({
        ...run,
        outcome: "failed",
        status,
        durationMs: Date.now() - startedAt,
        error: String(error),
      })
      throw error
    } finally {
      clearTimeout(timeout)
    }
  },
}

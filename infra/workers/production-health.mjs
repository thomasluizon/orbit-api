export const endpoints = ["/health", "/health/jobs"]

export async function pollHealth(fetcher = fetch, emit = console.log, now = Date.now) {
  await Promise.all(endpoints.map(async endpoint => {
    let healthy = 0
    let status
    try {
      const response = await fetcher(`https://api.useorbit.org${endpoint}`, {
        cache: "no-store",
        redirect: "manual",
        signal: AbortSignal.timeout(20_000),
      })
      status = response.status
      await response.text()
      healthy = response.ok ? 1 : 0
    } catch {
      healthy = 0
    }
    emit(JSON.stringify({
      _aws: {
        Timestamp: now(),
        CloudWatchMetrics: [{
          Namespace: "Orbit/ProductionHealth",
          Dimensions: [["Endpoint"]],
          Metrics: [{ Name: "Healthy", Unit: "Count" }],
        }],
      },
      Endpoint: endpoint,
      Healthy: healthy,
      Status: status,
    }))
  }))
}

export const handler = async () => pollHealth()

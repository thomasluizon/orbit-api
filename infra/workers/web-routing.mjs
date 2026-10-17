export default {
  fetch(request, env) {
    const redesign = (request.headers.get("Cookie") ?? "")
      .split(";")
      .some(cookie => cookie.trim() === "orbit_design=redesign")

    if (!redesign) return fetch(request)

    const url = new URL(request.url)
    url.protocol = "https:"
    url.host = env.REDESIGN_HOST
    const upstream = new Request(url, request)
    upstream.headers.set("Host", env.REDESIGN_HOST)
    upstream.headers.set("X-Forwarded-Host", "app.useorbit.org")
    upstream.headers.set("X-Forwarded-Proto", "https")
    return fetch(upstream)
  },
}

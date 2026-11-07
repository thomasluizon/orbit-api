// Answers the API like OpenAI under /v1 so Astra turns pass moderation and load the tokenizer; every other request (Sentry, PostHog) gets a 200. See infra/memory-budget.md.
import https from 'node:https'
import fs from 'node:fs'
const counts = {}
const count = key => { counts[key] = (counts[key] ?? 0) + 1 }
setInterval(() => console.log(JSON.stringify({ at: new Date().toISOString(), counts })), 300_000).unref()
const usage = { input_tokens: 900, input_tokens_details: { cached_tokens: 0 }, output_tokens: 40, output_tokens_details: { reasoning_tokens: 0 }, total_tokens: 940 }
const text = 'Focus on the first habit on your list today, then log it when you finish.'
function response(model, status, output) {
  return { id: 'resp_fake', object: 'response', created_at: Math.floor(Date.now() / 1000), status, model, output,
    parallel_tool_calls: true, tool_choice: 'auto', tools: [], temperature: 1, top_p: 1, text: { format: { type: 'text' } },
    truncation: 'disabled', usage: status === 'completed' ? usage : null, error: null, incomplete_details: null, metadata: {} }
}
const message = (status, content) => ({ id: 'msg_fake', type: 'message', status, role: 'assistant', content })
const outputText = { type: 'output_text', text, annotations: [] }
https.createServer({ key: fs.readFileSync('/sink/sink.key'), cert: fs.readFileSync('/sink/sink.crt') }, (request, reply) => {
  const chunks = []
  request.on('data', chunk => chunks.push(chunk))
  request.on('end', () => {
    const path = request.url.split('?')[0]
    count(`${request.method} ${path.startsWith('/v1/') ? path : 'sink'}`)
    if (path === '/v1/moderations') {
      reply.writeHead(200, { 'content-type': 'application/json' })
      return reply.end(JSON.stringify({ id: 'modr_fake', model: 'omni-moderation-latest', results: [{ flagged: false, categories: { harassment: false, violence: false }, category_scores: { harassment: 0, violence: 0 } }] }))
    }
    if (path === '/v1/responses') {
      let body = {}
      try { body = JSON.parse(Buffer.concat(chunks).toString('utf8')) } catch { }
      const model = body.model ?? 'gpt-6-luna'
      if (!body.stream) {
        reply.writeHead(200, { 'content-type': 'application/json' })
        return reply.end(JSON.stringify(response(model, 'completed', [message('completed', [outputText])])))
      }
      reply.writeHead(200, { 'content-type': 'text/event-stream', 'cache-control': 'no-cache' })
      let sequence = 0
      const send = (type, data) => reply.write(`event: ${type}\ndata: ${JSON.stringify({ type, sequence_number: sequence++, ...data })}\n\n`)
      send('response.created', { response: response(model, 'in_progress', []) })
      send('response.output_item.added', { output_index: 0, item: message('in_progress', []) })
      send('response.content_part.added', { item_id: 'msg_fake', output_index: 0, content_index: 0, part: { type: 'output_text', text: '', annotations: [] } })
      for (const word of text.split(' '))
        send('response.output_text.delta', { item_id: 'msg_fake', output_index: 0, content_index: 0, delta: word + ' ', logprobs: [] })
      send('response.output_text.done', { item_id: 'msg_fake', output_index: 0, content_index: 0, text, logprobs: [] })
      send('response.content_part.done', { item_id: 'msg_fake', output_index: 0, content_index: 0, part: outputText })
      send('response.output_item.done', { output_index: 0, item: message('completed', [outputText]) })
      send('response.completed', { response: response(model, 'completed', [message('completed', [outputText])]) })
      return reply.end()
    }
    reply.writeHead(200, { 'content-type': 'application/json' })
    reply.end('{"status":1,"id":"00000000000000000000000000000000"}')
  })
}).listen(8443, () => console.log('sink listening'))

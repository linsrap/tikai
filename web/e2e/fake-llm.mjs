// Stands in for the Anthropic Messages API in the browser test's stack (service fake-llm in compose.e2e.yml), so
// review analyses run without a key or the internet. It answers from what the hub sent: every criterion id in the
// message is covered (the last one partially), the listed files form one story group, and the first file is high
// risk. A model whose id starts with "e2e-missing" gets the API's "model not found" error instead.
import http from 'node:http'

const port = Number(process.env.PORT ?? 8080)

function analysis(message) {
  const ids = [...message.matchAll(/^- id: ([0-9a-f-]{36})$/gm)].map((m) => m[1])
  const files = (message.split('<files>')[1]?.split('</files>')[0] ?? '')
    .split('\n')
    .filter((line) => line && !line.includes('not shown]'))
    .map((line) => line.replace(/^[AMDRT] (?:.* -> )?/, '').replace(/ \(\+.*$| \[.*$/, ''))
  const first = files[0]
  return {
    summary: `Changes ${files.length} file${files.length === 1 ? '' : 's'} (fake analysis).`,
    coverage: ids.map((id, i) => ({
      criterion_id: id,
      status: i === ids.length - 1 && ids.length > 1 ? 'partial' : 'covered',
      note: 'Checked by the fake analysis.',
      refs: first ? [{ path: first, line_start: 1, line_end: 1 }] : [],
    })),
    groups: files.length
      ? [{ title: 'The agent’s files', layer: 'domain', note: 'Everything it changed.', files }]
      : [],
    files: first
      ? [{ path: first, risk: 'high', reason: 'The fake analysis rates the first file high.' }]
      : [],
  }
}

function reply(response, status, body) {
  response.writeHead(status, { 'content-type': 'application/json' })
  response.end(JSON.stringify(body))
}

http
  .createServer((request, response) => {
    if (request.method !== 'POST' || !request.url?.startsWith('/v1/messages')) {
      return reply(response, 404, {
        type: 'error',
        error: { type: 'not_found_error', message: 'Not here.' },
      })
    }
    let body = ''
    request.on('data', (chunk) => (body += chunk))
    request.on('end', () => {
      const parsed = JSON.parse(body)
      if (parsed.model.startsWith('e2e-missing')) {
        return reply(response, 404, {
          type: 'error',
          error: { type: 'not_found_error', message: `model: ${parsed.model}` },
        })
      }
      const content = parsed.messages[0].content
      const text = typeof content === 'string' ? content : content.map((c) => c.text ?? '').join('')
      reply(response, 200, {
        id: 'msg_e2e',
        type: 'message',
        role: 'assistant',
        model: parsed.model,
        content: [{ type: 'text', text: JSON.stringify(analysis(text)) }],
        stop_reason: 'end_turn',
        stop_sequence: null,
        usage: { input_tokens: 1000, output_tokens: 200 },
      })
    })
  })
  .listen(port, () => console.log(`fake Messages API on :${port}`))

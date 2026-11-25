// Demo data for a preview of Tikai itself (seed step in tikai-preview.yml): the first user, who becomes admin, and a
// project with one ticket, all through the API like the web app. Throwaway values only; the login is the file's
// seed_hint. Usage: node scripts/preview-seed.mjs http://app:8080
const base = process.argv[2] ?? 'http://app:8080'
const cookies = new Map()

async function call(method, path, body) {
  const headers = { cookie: [...cookies].map(([k, v]) => `${k}=${v}`).join('; ') }
  if (cookies.has('XSRF-TOKEN')) headers['X-XSRF-TOKEN'] = cookies.get('XSRF-TOKEN')
  if (body) headers['content-type'] = 'application/json'
  const res = await fetch(base + path, { method, headers, body: body && JSON.stringify(body) })
  for (const line of res.headers.getSetCookie()) {
    const [pair] = line.split(';')
    const at = pair.indexOf('=')
    cookies.set(pair.slice(0, at), pair.slice(at + 1))
  }
  if (!res.ok) throw new Error(`${method} ${path}: ${res.status} ${await res.text()}`)
  return res
}

// The antiforgery token belongs to the signed-in user, so it is fetched again after signing up.
await call('GET', '/api/auth/antiforgery')
await call('POST', '/api/auth/signup', {
  email: 'demo@example.com',
  displayName: 'Demo',
  password: 'preview-preview',
})
await call('GET', '/api/auth/antiforgery')
await call('POST', '/api/projects', { name: 'Demo', slug: 'demo', key: 'DEMO' })
await call('POST', '/api/projects/demo/tickets', {
  title: 'Show the total hours per quarter',
  description: 'A card on the dashboard with the hours booked in the current quarter.',
  criteria: ['The card shows the sum of this quarter', 'It updates after a new booking'],
})
console.log('Signed up demo@example.com and created project Demo with ticket DEMO-1')

import { execSync } from 'node:child_process'
import path from 'node:path'
import { expect, test, type APIRequestContext, type BrowserContext } from '@playwright/test'

// The main loop through the browser, with the stub agent (tests/agent-stub, scenario plan-wait): create a ticket,
// assign it, approve the plan and answer the question in the inbox, watch the run on the board, restart the hub while
// its log streams, and see the run end in review. The page is loaded once and never reloaded: everything after that
// arrives through live updates. Review analyses go to the stack's fake Messages API (e2e/fake-llm.mjs).

const lines = 40

/** The stack's fake Messages API (service fake-llm); the hub allows http base URLs there. */
const fakeLlm = 'http://fake-llm:8080'

type RunEvent = { seq: number; kind: string; payload: { text?: string } }

async function xsrf(context: BrowserContext) {
  const cookie = (await context.cookies()).find((c) => c.name === 'XSRF-TOKEN')
  if (!cookie) throw new Error('No XSRF-TOKEN cookie; the web app fetches one after sign-up.')
  return { 'X-XSRF-TOKEN': decodeURIComponent(cookie.value) }
}

async function post(
  request: APIRequestContext,
  context: BrowserContext,
  url: string,
  data: object,
) {
  const response = await request.post(url, { data, headers: await xsrf(context) })
  expect(response.ok(), `${url}: ${response.status()} ${await response.text()}`).toBe(true)
  return response.json()
}

test('create ticket, assign, approve the plan, answer the question, live to review', async ({
  page,
  context,
}) => {
  // npm run e2e sets these; the defaults fit a stack left running with npm run e2e -- --keep.
  const restartHub =
    process.env.E2E_RESTART_HUB ??
    'docker compose -p tikai-e2e --env-file web/e2e/e2e.env -f compose.yml -f web/e2e/compose.e2e.yml restart hub'
  const root = process.env.E2E_ROOT ?? path.resolve(import.meta.dirname, '../..')

  // A fresh installation: the first sign-up is the admin.
  await page.goto('/signup')
  await page.getByLabel('Name').fill('Ada')
  await page.getByLabel('Email').fill('ada@example.org')
  await page.getByLabel('Password (at least 12 characters)').fill('correct horse battery')
  await page.getByRole('button', { name: 'Create admin account' }).click()
  await expect(page.getByRole('heading', { name: 'Inbox' })).toBeVisible()

  // Project, repository and provider (any key: the stub never calls the LLM, the analysis calls the fake API).
  const request = page.request
  await post(request, context, '/api/projects', { name: 'End to end', slug: 'e2e', key: 'E2E' })
  await post(request, context, '/api/projects/e2e/repositories', { name: 'app' })
  await post(request, context, '/api/projects/e2e/llm-providers', {
    name: 'Stub key',
    defaultModel: 'claude-haiku-5-5',
    apiKey: 'stub-key-not-used-0123456789',
    baseUrl: fakeLlm,
  })
  await expect
    .poll(
      async () => (await (await request.get('/api/projects/e2e/repositories')).json())[0]?.status,
    )
    .toBe('ready')

  // The last full page load. A reload would drop this marker.
  await page.goto('/projects/e2e/tickets/new')
  await page.evaluate(() => Object.assign(window, { loadedOnce: true }))

  await page.getByLabel('Title').fill('Write the chosen word')
  await page
    .getByPlaceholder('What should be done, and why?')
    .fill(`Ask which word to use and write it down. stub:plan-wait:${lines}`)
  await page.getByRole('button', { name: 'Add criterion' }).click()
  await page.getByLabel('Criterion 1', { exact: true }).fill('answer.txt holds the chosen word')
  await page.getByRole('button', { name: 'Create ticket' }).click()
  await expect(page.getByRole('heading', { name: /Write the chosen word/ })).toBeVisible()
  await page.getByRole('button', { name: 'Assign to agent (Claude Code)' }).click()

  // The plan arrives in the inbox without a reload.
  const nav = page.getByRole('navigation', { name: 'Main' })
  await nav.getByRole('link', { name: /^Inbox/ }).click()
  const plan = page.getByRole('article', { name: 'Plan to approve: E2E-1' })
  // The summary first; steps, risks and files on demand.
  await expect(
    plan.getByText('Write before.txt, then the word you pick to answer.txt.', { exact: true }),
  ).toBeVisible()
  const details = plan.getByRole('listitem').filter({ hasText: 'Write before.txt' })
  await expect(details).toBeHidden()
  await plan.getByText('Show plan (2 steps, 1 risk, 2 files)').click()
  await expect(details).toBeVisible()
  await expect(plan.getByText('2 files in the root')).toBeVisible()
  await expect(page.getByRole('link', { name: 'Inbox, 1 waiting' })).toBeVisible()
  await plan.getByRole('button', { name: 'Approve plan' }).click()
  await expect(plan).toBeHidden()

  // Then the question, with its options.
  const question = page.getByRole('article', { name: 'Question: E2E-1' })
  await expect(question.getByText('Which word?')).toBeVisible()
  await expect(question.getByText('It goes into')).toBeHidden()
  await question.getByText('Why it asks').click()
  await expect(question.getByText('It goes into')).toBeVisible()
  // The recommended option comes first.
  await expect(
    question.getByRole('group', { name: 'Options' }).getByRole('button').first(),
  ).toHaveAccessibleName('alpha recommended')
  await question
    .getByRole('group', { name: 'Options' })
    .getByRole('button', { name: 'beta' })
    .click()
  await expect(question).toBeHidden()
  await expect(page.getByRole('link', { name: 'Inbox, 0 waiting' })).toBeVisible()

  // The board: the run and its live log.
  await nav.getByRole('link', { name: 'Board', exact: true }).click()
  const run = page.getByRole('article', { name: 'Run of E2E-1' })
  await run.getByRole('button', { name: 'Live log' }).click()
  const log = run.getByRole('log', { name: 'Agent log' })
  await expect(log.getByText(/Line \d+ of 40/).first()).toBeVisible()

  // Restart the hub while the lines stream: the page loses its connection, says so, reconnects and catches up.
  // The statuses are recorded in the page, so a quick reconnect cannot slip past the check.
  await page.evaluate(() => {
    const shell = document.querySelector('[data-live-status]')
    const status = () => shell?.getAttribute('data-live-status') ?? 'missing'
    const seen = [status()]
    Object.assign(window, { liveStatuses: seen })
    if (shell) {
      new MutationObserver(() => seen.push(status())).observe(shell, {
        attributeFilter: ['data-live-status'],
      })
    }
  })
  execSync(restartHub, { cwd: root, stdio: 'inherit' })
  const statuses = () => page.evaluate(() => Reflect.get(window, 'liveStatuses') as string[])
  await expect.poll(statuses, { timeout: 120_000 }).toEqual(['live', 'reconnecting', 'live'])
  await expect(page.getByRole('status')).toBeHidden()
  await expect(run).toHaveAttribute('data-state', 'in_review', { timeout: 120_000 })
  await expect(run.getByText('In review')).toBeVisible()

  // The log holds every event exactly once: seqs 1…n with nothing missing, the same as the server's. The run is in
  // review from the submit call on; its container's last output and the hub's exit note come right after.
  const runId = (await (await request.get('/api/runs')).json())[0].id as string
  const readEvents = async () =>
    (await (await request.get(`/api/runs/${runId}/events?limit=1000`)).json()) as RunEvent[]
  await expect
    .poll(async () => (await readEvents()).some((e) => e.payload.text === 'Agent exited'))
    .toBe(true)
  const events = await readEvents()
  const last = events.length
  expect(events.map((e) => e.seq)).toEqual(Array.from({ length: last }, (_, i) => i + 1))
  await expect(log).toHaveAttribute('data-last-seq', String(last))
  await expect(log).toHaveAttribute('data-first-seq', '1')
  await expect(log).toHaveAttribute('data-count', String(last))
  const streamed = events.filter((e) => /^Line \d+ of 40$/.test(e.payload.text ?? ''))
  expect(streamed.map((e) => e.payload.text)).toEqual(
    Array.from({ length: lines }, (_, i) => `Line ${i + 1} of ${lines}`),
  )

  // The ticket page shows the run in review too.
  await run.getByRole('link', { name: /E2E-1/ }).click()
  await expect(
    page.getByRole('region', { name: 'Agent runs' }).getByText('In review').first(),
  ).toBeVisible()

  // Still the page loaded before the ticket was created: nothing reloaded it.
  expect(await page.evaluate(() => Reflect.get(window, 'loadedOnce'))).toBe(true)

  // The review screen, from the inbox. Monaco and its worker come from the hub, not from a CDN.
  const origin = new URL(page.url()).origin
  const foreign: string[] = []
  page.on('request', (r) => {
    if (new URL(r.url()).origin !== origin && !r.url().startsWith('data:')) foreign.push(r.url())
  })
  await nav.getByRole('link', { name: /^Inbox/ }).click()
  const review = page.getByRole('article', { name: 'Ready for review: E2E-1' })
  await review.getByRole('link', { name: 'Open review' }).click()
  await expect(page.getByRole('heading', { name: /Write the chosen word/ })).toBeVisible()
  const tree = page.getByRole('navigation', { name: 'Changed files' })
  await expect(tree.getByText('0 of 2 files viewed')).toBeVisible()

  // The analysis sits above the files: coverage next to the agent's claim, files in story order.
  const analysis = page.getByRole('region', { name: 'Analysis' })
  await expect(analysis.getByText(/fake analysis\)\.$/)).toBeVisible({ timeout: 30_000 })
  const row = analysis.getByRole('row', { name: /answer\.txt holds the chosen word/ })
  await expect(row.getByText('covered', { exact: true }).last()).toBeVisible()
  await expect(tree.getByRole('button', { name: 'Story order' })).toHaveAttribute(
    'aria-pressed',
    'true',
  )
  await expect(tree.getByText('The agent’s files')).toBeVisible()
  await expect(tree.getByText('high', { exact: true })).toBeVisible()
  // A ref opens its file in the diff.
  await row.getByRole('button', { name: /\.txt:1$/ }).click()
  await expect(page.locator('.monaco-diff-editor')).toBeVisible()
  await tree.getByRole('button', { name: 'Path order' }).click()
  await expect(tree.getByText('The agent’s files')).toHaveCount(0)

  await tree.getByRole('button', { name: /answer\.txt/ }).click()
  await expect(page.locator('.monaco-diff-editor')).toContainText('beta')

  // Viewed is stored on the hub: it is still there after a reload.
  await tree.getByLabel('Viewed answer.txt').check()
  await expect(tree.getByText('1 of 2 files viewed')).toBeVisible()
  await page.reload()
  await expect(tree.getByText('1 of 2 files viewed')).toBeVisible()
  await expect(tree.getByLabel('Viewed answer.txt')).toBeChecked()
  await expect(tree.getByLabel('Viewed before.txt')).not.toBeChecked()
  expect(foreign).toEqual([])
})

test('the review screen flags files, warns about uncommitted work, and rejects', async ({
  page,
  context,
}) => {
  // The stack of the first test: log in again, then a project of its own (one active run per repository).
  await page.goto('/login')
  await page.getByLabel('Email').fill('ada@example.org')
  await page.getByLabel('Password').fill('correct horse battery')
  await page.getByRole('button', { name: 'Log in' }).click()
  await expect(page.getByRole('heading', { name: 'Inbox' })).toBeVisible()
  const request = page.request
  await post(request, context, '/api/projects', { name: 'Flags', slug: 'flags', key: 'FL' })
  await post(request, context, '/api/projects/flags/repositories', { name: 'app' })
  await post(request, context, '/api/projects/flags/llm-providers', {
    name: 'Stub key',
    defaultModel: 'claude-haiku-5-5',
    apiKey: 'stub-key-not-used-0123456789',
    baseUrl: fakeLlm,
  })
  // A model the fake API does not know: this project's analysis fails, and the review works without it.
  const missing = 'e2e-missing-model'
  const settings = await request.put('/api/projects/flags/settings', {
    data: { analysisModels: { light: missing, normal: missing, strict: missing } },
    headers: await xsrf(context),
  })
  expect(settings.ok(), await settings.text()).toBe(true)
  await post(request, context, '/api/projects/flags/tickets', {
    title: 'Change some files',
    description: 'Every kind of change. stub:review',
    criteria: ['Files changed', 'Files renamed'],
  })
  await post(request, context, '/api/projects/flags/tickets/1/runs', {})

  // The ticket page links to the review as soon as the agent has submitted.
  await page.goto('/projects/flags/tickets/1')
  await page.getByRole('link', { name: 'Review', exact: true }).click()
  await expect(page.getByRole('heading', { name: /Change some files/ })).toBeVisible()

  // The agent's report, with what it left uncommitted.
  await expect(page.getByText('Left out: 1 uncommitted file')).toBeVisible()
  await expect(page.getByText('?? notes.txt')).toBeVisible()
  await expect(page.getByText('Criteria: 1 covered, 1 partial')).toBeVisible()
  const analysis = page.getByRole('region', { name: 'Analysis' })
  await expect(analysis.getByText('Analysis unavailable.')).toBeVisible({ timeout: 30_000 })
  await expect(analysis.getByText(/e2e-missing-model/)).toBeVisible()
  await expect(analysis.getByRole('button', { name: 'Retry analysis' })).toBeEnabled()

  // Lock files apart; binary and large files not shown until asked for.
  const tree = page.getByRole('navigation', { name: 'Changed files' })
  await expect(tree.getByText('Generated and lock files (1)')).toBeVisible()
  await tree.getByRole('button', { name: /image\.png/ }).click()
  const pane = page.getByRole('region', { name: 'Diff' })
  await expect(pane.getByText('A binary file.')).toBeVisible()
  await pane.getByRole('button', { name: 'Show diff' }).click()
  await expect(pane.getByText(/Binary file \(.*\); it cannot be shown as text/)).toBeVisible()
  await tree.getByRole('button', { name: /big\.txt/ }).click()
  await expect(pane.getByText('large', { exact: true })).toBeVisible()
  await pane.getByRole('button', { name: 'Show diff' }).click()
  await expect(pane.getByText(/Too large to show here/)).toBeVisible()

  // The Approve button says what it does (the third test approves); reject closes the run.
  const verdicts = page.getByLabel('Verdict')
  await expect(verdicts.getByRole('button', { name: 'Approve and squash into main' })).toBeEnabled()
  await verdicts.getByRole('button', { name: 'Reject' }).click()
  await page.getByRole('alertdialog').getByRole('button', { name: 'Reject change' }).click()
  await expect(page.getByText('rejected', { exact: true })).toBeVisible()
  await expect(page.getByText('Cancelled', { exact: true })).toBeVisible()
})

test('a comment sent to the agent comes back answered, and the next round shows only the interdiff', async ({
  page,
  context,
}) => {
  await page.goto('/login')
  await page.getByLabel('Email').fill('ada@example.org')
  await page.getByLabel('Password').fill('correct horse battery')
  await page.getByRole('button', { name: 'Log in' }).click()
  await expect(page.getByRole('heading', { name: 'Inbox' })).toBeVisible()
  const request = page.request
  await post(request, context, '/api/projects', { name: 'Fixes', slug: 'fixes', key: 'FX' })
  await post(request, context, '/api/projects/fixes/repositories', { name: 'app' })
  await post(request, context, '/api/projects/fixes/llm-providers', {
    name: 'Stub key',
    defaultModel: 'claude-haiku-5-5',
    apiKey: 'stub-key-not-used-0123456789',
    baseUrl: fakeLlm,
  })
  await post(request, context, '/api/projects/fixes/tickets', {
    title: 'Write fix.txt',
    description: 'A file to fix in review. stub:fix',
    criteria: ['fix.txt exists'],
  })
  await post(request, context, '/api/projects/fixes/tickets/1/runs', {})
  await page.goto('/projects/fixes/tickets/1')
  await page.getByRole('link', { name: 'Review', exact: true }).click()
  await expect(page.getByRole('heading', { name: /Write fix\.txt/ })).toBeVisible()

  // A comment for the agent on the first line of the new fix.txt.
  const tree = page.getByRole('navigation', { name: 'Changed files' })
  const pane = page.getByRole('region', { name: 'Diff' })
  await tree.getByRole('button', { name: /fix\.txt/ }).click()
  await pane.locator('.editor.modified .view-line').first().click()
  await pane.getByRole('button', { name: 'Comment on line 1' }).click()
  await pane.getByLabel('Comment on line 1 of the new version').fill('Say fixed.')
  await pane.getByRole('button', { name: 'Send to agent' }).click()
  const thread = pane.getByTestId('thread')
  await expect(thread.getByText('for the agent, not sent yet')).toBeVisible()
  await expect(tree.getByRole('img', { name: '1 open comment thread' })).toBeVisible()

  // Request changes: the comment goes to the agent, which resumes, replies and submits round 2.
  const verdicts = page.getByLabel('Verdict')
  await expect(verdicts.getByText('1 comment will be sent to the agent.')).toBeVisible()
  await verdicts.getByRole('button', { name: 'Request changes…' }).click()
  await verdicts.getByRole('button', { name: 'Send to the agent' }).click()
  await expect(page.getByTestId('verdict')).toContainText('changes requested')

  // Round 2 arrives live and opens on its interdiff: only fix.txt, with the thread on the left and the reply.
  const since = page.getByRole('button', { name: 'Changes since round 1' })
  await expect(since).toHaveAttribute('aria-pressed', 'true', { timeout: 60_000 })
  await expect(tree.getByText('0 of 1 files viewed')).toBeVisible()
  await expect(tree.getByText('Changes since round 1')).toBeVisible()
  await tree.getByRole('button', { name: /fix\.txt/ }).click()
  await expect(thread.getByText('sent to agent', { exact: true })).toBeVisible()
  await expect(thread.getByText('Fixed: Say fixed.')).toBeVisible()
  await expect(thread.getByText('Agent', { exact: true })).toBeVisible()
  const analysis = page.getByRole('region', { name: 'Analysis' })
  await expect(analysis.getByText(/fake interdiff analysis/)).toBeVisible({ timeout: 30_000 })

  // Resolved threads collapse; the full change still has both files.
  await thread.getByRole('button', { name: 'Resolve' }).click()
  await expect(thread).toHaveAttribute('data-status', 'resolved')
  await page.getByRole('button', { name: 'Full change' }).click()
  await expect(tree.getByText('0 of 2 files viewed')).toBeVisible()

  // The inbox card says how the agent answered.
  await page.goto('/')
  await expect(page.getByText('Round 2 · the agent answered 1 of 1 thread')).toBeVisible()

  // Approve: the hub squashes round 2 into main (a local repository: nothing to push), the run ends merged.
  await page
    .getByRole('article', { name: 'Ready for review: FX-1' })
    .getByRole('link', { name: 'Open review' })
    .click()
  await page
    .getByLabel('Verdict')
    .getByRole('button', { name: 'Approve and squash into main' })
    .click()
  await expect(page.getByTestId('verdict')).toContainText(/Merged into main as [0-9a-f]{8}\./)
  await expect(page.getByText('Merged', { exact: true })).toBeVisible()
  await page.goto('/')
  await expect(page.getByRole('article', { name: /FX-1/ })).toHaveCount(0)

  // main as a bundle, for a clone on people's own computers.
  const repositories = (await (await request.get('/api/projects/fixes/repositories')).json()) as {
    id: string
  }[]
  const bundle = await request.get(`/api/projects/fixes/repositories/${repositories[0].id}/bundle`)
  expect(bundle.ok()).toBe(true)
  expect(bundle.headers()['content-disposition']).toContain('app.bundle')
  expect((await bundle.body()).subarray(0, 16).toString()).toMatch(/^# v\d git bundle/)
})

test('a round is previewed at its own address and stopped', async ({ page, context }) => {
  await page.goto('/login')
  await page.getByLabel('Email').fill('ada@example.org')
  await page.getByLabel('Password').fill('correct horse battery')
  await page.getByRole('button', { name: 'Log in' }).click()
  await expect(page.getByRole('heading', { name: 'Inbox' })).toBeVisible()
  const request = page.request
  await post(request, context, '/api/projects', { name: 'Previews', slug: 'previews', key: 'PV' })
  await post(request, context, '/api/projects/previews/repositories', { name: 'app' })
  await post(request, context, '/api/projects/previews/llm-providers', {
    name: 'Stub key',
    defaultModel: 'claude-haiku-5-5',
    apiKey: 'stub-key-not-used-0123456789',
    baseUrl: fakeLlm,
  })
  // The stub commits a tiny node app with a tikai-preview.yml (setup step, app on port 8000).
  await post(request, context, '/api/projects/previews/tickets', {
    title: 'A page to click through',
    description: 'Something to look at. stub:preview',
    criteria: ['The page says hello'],
  })
  await post(request, context, '/api/projects/previews/tickets/1/runs', {})
  await page.goto('/projects/previews/tickets/1')
  await page.getByRole('link', { name: 'Review', exact: true }).click()
  await expect(page.getByRole('heading', { name: /A page to click through/ })).toBeVisible()

  // Start: clone, setup, app; the log shows it as it happens.
  await page.getByRole('button', { name: 'Preview', exact: true }).click()
  const panel = page.getByRole('region', { name: 'Preview' })
  await panel.getByRole('button', { name: 'Start preview' }).click()
  await expect(panel.getByText('Running', { exact: true })).toBeVisible({ timeout: 180_000 })
  await expect(panel.getByRole('log', { name: 'Preview log' })).toContainText('Preview running')

  // Open: a new tab on the preview's own host, entered with a one-time link.
  const opened = context.waitForEvent('page')
  await panel.getByRole('button', { name: 'Open preview' }).click()
  const app = await opened
  await expect(app.getByRole('heading', { name: 'Hello from the preview' })).toBeVisible()
  await expect(app.getByText('built by setup')).toBeVisible()
  const host = new URL(app.url()).host
  expect(host).toMatch(/^[0-9a-f]{32}\.preview\.localhost:18092$/)
  // That host has only its own session cookie; the hub's login stays with the hub.
  const cookies = await context.cookies(app.url())
  expect(cookies.map((c) => c.name)).toEqual(['tikai_preview'])
  expect(cookies[0].domain).toBe(host.split(':')[0])
  await app.close()

  // Stop: the host refuses from then on.
  await panel.getByRole('button', { name: 'Stop' }).click()
  await expect(panel.getByText('Stopped', { exact: true })).toBeVisible()
  await expect
    .poll(async () => (await request.get(`http://${host}/`)).status(), { timeout: 10_000 })
    .not.toBe(200)
})

test('a preview with two apps routes /api to the second one', async ({ page, context }) => {
  await page.goto('/login')
  await page.getByLabel('Email').fill('ada@example.org')
  await page.getByLabel('Password').fill('correct horse battery')
  await page.getByRole('button', { name: 'Log in' }).click()
  await expect(page.getByRole('heading', { name: 'Inbox' })).toBeVisible()
  const request = page.request
  await post(request, context, '/api/projects', { name: 'Two apps', slug: 'two-apps', key: 'TA' })
  await post(request, context, '/api/projects/two-apps/repositories', { name: 'app' })
  await post(request, context, '/api/projects/two-apps/llm-providers', {
    name: 'Stub key',
    defaultModel: 'claude-haiku-5-5',
    apiKey: 'stub-key-not-used-0123456789',
    baseUrl: fakeLlm,
  })
  // The stub commits web.js (on /) and api.js (on /api) with a tikai-preview.yml using apps.
  await post(request, context, '/api/projects/two-apps/tickets', {
    title: 'A frontend and an API',
    description: 'Two processes. stub:preview-apps',
    criteria: ['Both answer'],
  })
  await post(request, context, '/api/projects/two-apps/tickets/1/runs', {})
  await page.goto('/projects/two-apps/tickets/1')
  await page.getByRole('link', { name: 'Review', exact: true }).click()
  await expect(page.getByRole('heading', { name: /A frontend and an API/ })).toBeVisible()

  await page.getByRole('button', { name: 'Preview', exact: true }).click()
  const panel = page.getByRole('region', { name: 'Preview' })
  await panel.getByRole('button', { name: 'Start preview' }).click()
  await expect(panel.getByText('Running', { exact: true })).toBeVisible({ timeout: 180_000 })
  await expect(panel.getByText('Apps: web on /, api on /api')).toBeVisible()
  await panel.getByRole('group', { name: 'Log of' }).getByRole('button', { name: 'api' }).click()
  await expect(panel.getByRole('log', { name: 'Preview log' })).toContainText(
    'api listening with [redacted]',
  )
  await expect(panel.getByRole('log', { name: 'Preview log' })).not.toContainText('web listening')

  const opened = context.waitForEvent('page')
  await panel.getByRole('button', { name: 'Open preview' }).click()
  const app = await opened
  await expect(app.locator('body')).toContainText('"app":"web"')
  const origin = new URL(app.url()).origin
  // The hub reaches each app on the preview's network by its container; /api goes to the second, path unchanged.
  await app.goto(`${origin}/api/hello`)
  await expect(app.locator('body')).toContainText('"app":"api","path":"/api/hello"')
  await app.goto(`${origin}/from-api`)
  await expect(app.locator('body')).toContainText('"fromApi":{"app":"api"')
  await app.close()

  await panel.getByRole('button', { name: 'Stop' }).click()
  await expect(panel.getByText('Stopped', { exact: true })).toBeVisible()
})

import { execSync } from 'node:child_process'
import path from 'node:path'
import { expect, test, type APIRequestContext, type BrowserContext } from '@playwright/test'

// The main loop through the browser, with the stub agent (tests/agent-stub, scenario plan-wait): create a ticket,
// assign it, approve the plan and answer the question in the inbox, watch the run on the board, restart the hub while
// its log streams, and see the run end in review. The page is loaded once and never reloaded: everything after that
// arrives through live updates.

const lines = 40

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

  // Project, repository and provider (any key: the stub never calls the LLM).
  const request = page.request
  await post(request, context, '/api/projects', { name: 'End to end', slug: 'e2e', key: 'E2E' })
  await post(request, context, '/api/projects/e2e/repositories', { name: 'app' })
  await post(request, context, '/api/projects/e2e/llm-providers', {
    name: 'Stub key',
    defaultModel: 'claude-haiku-5-5',
    apiKey: 'stub-key-not-used-0123456789',
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
  const nav = page.getByRole('navigation')
  await nav.getByRole('link', { name: /^Inbox/ }).click()
  const plan = page.getByRole('article', { name: 'Plan to approve: E2E-1' })
  await expect(plan.getByText('Write before.txt')).toBeVisible()
  await expect(page.getByRole('link', { name: 'Inbox, 1 waiting' })).toBeVisible()
  await plan.getByRole('button', { name: 'Approve plan' }).click()
  await expect(plan).toBeHidden()

  // Then the question, with its options.
  const question = page.getByRole('article', { name: 'Question: E2E-1' })
  await expect(question.getByText('Which word?')).toBeVisible()
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
  })
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

  // Approve and request changes come later; reject closes the run.
  const verdicts = page.getByLabel('Verdict')
  await expect(verdicts.getByRole('button', { name: 'Approve' })).toBeDisabled()
  page.once('dialog', (dialog) => void dialog.accept())
  await verdicts.getByRole('button', { name: 'Reject' }).click()
  await expect(page.getByText('rejected', { exact: true })).toBeVisible()
  await expect(page.getByText('Cancelled', { exact: true })).toBeVisible()
})

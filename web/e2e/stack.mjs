// Runs the browser test against a throwaway compose stack: the hub image, the stub agent and a fake Messages API, as compose project
// tikai-e2e on port 18092 with test settings (e2e.env). Plain Node, so it works the same in PowerShell, bash and CI.
//   node e2e/stack.mjs          build images, start the stack, run Playwright, remove the stack (always)
//   node e2e/stack.mjs --keep   the same, but leave the stack running (for debugging: npm run e2e:test)
//   node e2e/stack.mjs down     remove the stack (containers, volumes including the runs volume, network)
//   node e2e/stack.mjs clean    remove the stack and the e2e images
// Extra arguments go to Playwright (e.g. --headed).
import { spawnSync } from 'node:child_process'
import path from 'node:path'
import { fileURLToPath } from 'node:url'

const web = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..')
const root = path.resolve(web, '..')
const project = 'tikai-e2e'
const runsVolume = `${project}_runs`
const images = [
  'tikai/hub:e2e',
  'tikai/agent-stub:e2e',
  'tikai/agent-base:e2e',
  'tikai/fake-llm:e2e',
]
const baseUrl = 'http://127.0.0.1:18092'
const compose = [
  'compose',
  '-p',
  project,
  '--env-file',
  'web/e2e/e2e.env',
  '-f',
  'compose.yml',
  '-f',
  'web/e2e/compose.e2e.yml',
]

function run(command, args, { cwd = root, check = true, env = process.env, capture = false } = {}) {
  console.log(`> ${command} ${args.join(' ')}`)
  const result = spawnSync(command, args, {
    cwd,
    env,
    stdio: capture ? ['ignore', 'pipe', 'inherit'] : 'inherit',
    encoding: 'utf8',
    shell: process.platform === 'win32' && command === 'npx',
  })
  if (check && result.status !== 0) {
    throw new Error(`${command} ${args[0]} failed with exit code ${result.status}`)
  }
  return result
}

const docker = (args, options) => run('docker', args, options)

/** Compose must resolve the hub to the e2e tag, never to a real installation's image. */
function checkImages() {
  const config = docker([...compose, 'config', '--images'], { capture: true })
    .stdout.split(/\r?\n/)
    .filter(Boolean)
  if (
    !config.includes('tikai/hub:e2e') ||
    config.some((image) => image.startsWith('tikai/hub:') && image !== 'tikai/hub:e2e')
  ) {
    throw new Error(`The e2e stack would use ${config.join(', ')}; expected tikai/hub:e2e.`)
  }
}

function build() {
  checkImages()
  docker(['build', '-t', 'tikai/agent-base:e2e', 'images/agent-base'])
  docker([
    'build',
    '--build-arg',
    'BASE_IMAGE=tikai/agent-base:e2e',
    '-t',
    'tikai/agent-stub:e2e',
    'tests/agent-stub',
  ])
  docker([...compose, 'build', 'hub', 'fake-llm'])
}

async function up() {
  docker([...compose, 'up', '-d', '--wait', 'hub', 'fake-llm'])
  for (let i = 0; i < 120; i++) {
    try {
      if ((await fetch(`${baseUrl}/health/ready`)).ok) return
    } catch {
      // not listening yet
    }
    await new Promise((resolve) => setTimeout(resolve, 1000))
  }
  throw new Error(`The hub at ${baseUrl} did not get ready.`)
}

function down({ logs = false } = {}) {
  // Agent containers are started by the hub, not by compose: remove those still mounting this stack's runs volume.
  const leftovers = docker(['ps', '-aq', '--filter', `volume=${runsVolume}`], { capture: true })
    .stdout.split(/\r?\n/)
    .filter(Boolean)
  if (leftovers.length > 0) docker(['rm', '-f', ...leftovers], { check: false })
  if (logs) docker([...compose, 'logs', '--no-color', '--tail', '300', 'hub'], { check: false })
  docker([...compose, 'down', '-v', '--remove-orphans'], { check: false })
  docker(['volume', 'rm', '-f', runsVolume], { check: false })
}

async function main() {
  const args = process.argv.slice(2)
  if (args[0] === 'down') return down()
  if (args[0] === 'clean') {
    down()
    docker(['image', 'rm', ...images], { check: false })
    return
  }
  const keep = args.includes('--keep')
  const playwrightArgs = args.filter((a) => a !== '--keep')

  build()
  let status = 1
  try {
    await up()
    const env = {
      ...process.env,
      E2E_BASE_URL: baseUrl,
      // The test restarts the hub mid-run to check that live updates and the log recover.
      E2E_RESTART_HUB: ['docker', ...compose, 'restart', 'hub'].join(' '),
      E2E_ROOT: root,
    }
    status =
      run('npx', ['playwright', 'test', ...playwrightArgs], { cwd: web, check: false, env })
        .status ?? 1
  } finally {
    // The hub's log helps when the test failed; the Playwright report has the browser's side.
    if (!keep) down({ logs: status !== 0 })
  }
  process.exitCode = status
}

main().catch((error) => {
  console.error(error.message)
  process.exitCode = 1
})

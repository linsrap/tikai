import { useQuery } from '@tanstack/react-query'
import { Link, useNavigate, useSearch } from '@tanstack/react-router'
import { useEffect, useRef } from 'react'
import { useAnswered } from '../inbox/answered'
import { AttentionCard } from '../inbox/AttentionCard'
import { inboxQuery } from '../inbox/inbox'
import { LogPanel, WorkingRuns } from '../inbox/WorkingRuns'
import { useWatch } from '../live/live'
import { boardQuery, isWorking } from '../runs/runs'
import { ErrorText } from '../ui/form'
import { EmptyState, Loading, PageBody, PageHeader, SectionLabel } from '../ui/layout'
import { Dot } from '../ui/Tag'
import { usePageTitle } from '../ui/title'
import { useMediaQuery } from '../ui/useMediaQuery'
import { AppShell } from './AppShell'

/**
 * The home screen: everything that needs a human. Items an agent waits for come first, in their own group (the server
 * sorts them that way). Above them the agents at work, and beside them the live log of one run (`?log=<run id>`), so
 * answering and watching the agent go on need no second page. Updates live.
 */
export function InboxPage() {
  useWatch('WatchAll')
  const items = useQuery(inboxQuery)
  const runs = useQuery(boardQuery)
  const waiting = items.data?.filter((i) => i.blocking) ?? []
  const others = items.data?.filter((i) => !i.blocking) ?? []
  const working = runs.data?.filter((run) => isWorking(run.state)) ?? []
  usePageTitle('Inbox')

  const { log } = useSearch({ from: '/' })
  const navigate = useNavigate({ from: '/' })
  const setLog = (runId: string | undefined) =>
    void navigate({ search: runId ? { log: runId } : {}, replace: true })
  const wide = useMediaQuery('(min-width: 1024px)')
  const logRun = log ? runs.data?.find((run) => run.id === log) : undefined
  const answered = useAnswered()

  // An answer sent from here opens that run's log, unless one is open: the person sees the agent pick it up.
  const seen = useRef(answered.last?.n ?? 0)
  useEffect(() => {
    const last = answered.last
    if (!last || last.n === seen.current) return
    seen.current = last.n
    if (!log) void navigate({ search: { log: last.runId }, replace: true })
  }, [answered.last, log, navigate])

  const panel = wide && logRun

  return (
    <AppShell>
      <PageHeader
        title="Inbox"
        subtitle={items.data && subtitle(waiting.length, others.length)}
        wide={panel ? 'full' : false}
      />
      <PageBody
        wide={panel ? 'full' : false}
        className={panel ? 'grid grid-cols-[minmax(26rem,1fr)_minmax(0,1.25fr)] gap-6' : undefined}
      >
        <div className="min-w-0">
          <WorkingRuns
            runs={working}
            openLog={log}
            onToggleLog={(runId) => setLog(log === runId ? undefined : runId)}
            inline={!wide}
            answered={answered.runs}
          />
          {items.error && <ErrorText>{items.error.message}</ErrorText>}
          {items.isPending && <Loading />}
          {items.data?.length === 0 && (
            <EmptyState title="Nothing needs you right now">
              Agents&apos; plans, questions and results show up here. See what they do on the{' '}
              <Link to="/board">board</Link>, or start with a <Link to="/projects">project</Link>.
            </EmptyState>
          )}
          <div aria-label="Open items" role="region">
            {waiting.length > 0 && (
              <>
                <SectionLabel>
                  <Dot tone="wait" />
                  Agent waiting
                </SectionLabel>
                <div className="rounded-lg border border-line bg-surface">
                  {waiting.map((item) => (
                    <AttentionCard key={item.id} item={item} />
                  ))}
                </div>
              </>
            )}
            {others.length > 0 && (
              <>
                <SectionLabel>
                  <Dot tone="neutral" />
                  For you to look at
                </SectionLabel>
                <div className="rounded-lg border border-line bg-surface">
                  {others.map((item) => (
                    <AttentionCard key={item.id} item={item} />
                  ))}
                </div>
              </>
            )}
          </div>
        </div>
        {panel && <LogPanel run={logRun} onClose={() => setLog(undefined)} />}
      </PageBody>
    </AppShell>
  )
}

function subtitle(waiting: number, others: number): string {
  const parts = []
  if (waiting > 0) parts.push(`${waiting} ${waiting === 1 ? 'agent waits' : 'agents wait'} for you`)
  if (others > 0) parts.push(`${others} other item${others === 1 ? '' : 's'}`)
  return parts.length > 0 ? parts.join(' · ') : 'All clear'
}

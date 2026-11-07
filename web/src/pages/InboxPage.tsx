import { useQuery } from '@tanstack/react-query'
import { Link } from '@tanstack/react-router'
import { AttentionCard } from '../inbox/AttentionCard'
import { inboxQuery } from '../inbox/inbox'
import { useWatch } from '../live/live'
import { ErrorText } from '../ui/form'
import { EmptyState, Loading, PageBody, PageHeader, SectionLabel } from '../ui/layout'
import { Dot } from '../ui/Tag'
import { AppShell } from './AppShell'

/**
 * The home screen: everything that needs a human. Items an agent waits for come first, in their own group (the server
 * sorts them that way). Updates live.
 */
export function InboxPage() {
  useWatch('WatchAll')
  const items = useQuery(inboxQuery)
  const waiting = items.data?.filter((i) => i.blocking) ?? []
  const others = items.data?.filter((i) => !i.blocking) ?? []

  return (
    <AppShell>
      <PageHeader title="Inbox" subtitle={items.data && subtitle(waiting.length, others.length)} />
      <PageBody>
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

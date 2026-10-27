import { useQuery } from '@tanstack/react-query'
import { Link } from '@tanstack/react-router'
import { AttentionCard } from '../inbox/AttentionCard'
import { inboxQuery } from '../inbox/inbox'
import { useWatch } from '../live/live'
import { AppShell } from './AppShell'

/** The home screen: everything that needs a human, those an agent waits for first. Updates live. */
export function InboxPage() {
  useWatch('WatchAll')
  const items = useQuery(inboxQuery)

  return (
    <AppShell>
      <h1>Inbox</h1>
      {items.error && <p className="error">{items.error.message}</p>}
      {items.isPending && <p>Loading…</p>}
      {items.data?.length === 0 && (
        <p className="muted">
          Nothing needs you right now. Agents' plans, questions and results show up here; see what
          they do on the <Link to="/board">board</Link>, or start with a{' '}
          <Link to="/projects">project</Link>.
        </p>
      )}
      <section className="stack" aria-label="Open items">
        {items.data?.map((item) => (
          <AttentionCard key={item.id} item={item} />
        ))}
      </section>
    </AppShell>
  )
}

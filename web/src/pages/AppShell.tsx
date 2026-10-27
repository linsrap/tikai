import { useQuery, useSuspenseQuery } from '@tanstack/react-query'
import { Link, useNavigate } from '@tanstack/react-router'
import type { ReactNode } from 'react'
import { authStatusQuery, useLogout } from '../auth/auth'
import { inboxQuery, waitingCount } from '../inbox/inbox'
import { live, useLiveConnection, useLiveStatus, useWatch } from '../live/live'

/** Top bar with navigation and the logged-in user, around a page. Keeps the live connection open. */
export function AppShell({ children, wide = false }: { children: ReactNode; wide?: boolean }) {
  const { data: status } = useSuspenseQuery(authStatusQuery)
  const logout = useLogout()
  const navigate = useNavigate()
  const user = status.user
  useLiveConnection()
  // The inbox count in the top bar is live on every page.
  useWatch('WatchAll')
  const inbox = useQuery(inboxQuery)
  const waiting = waitingCount(inbox.data)
  const liveStatus = useLiveStatus()

  return (
    <main className={wide ? 'shell wide' : 'shell'} data-live-status={liveStatus}>
      <header className="topbar">
        <nav className="row">
          <Link to="/">
            <strong>Tikai</strong>
          </Link>
          <Link to="/" aria-label={`Inbox, ${waiting} waiting`}>
            Inbox{waiting > 0 && <span className="count">{waiting}</span>}
          </Link>
          <Link to="/board">Board</Link>
          <Link to="/projects">Projects</Link>
          <Link to="/settings">Settings</Link>
        </nav>
        <span className="row">
          <span>
            {user?.displayName}
            {user?.isAdmin ? ' (admin)' : null}
          </span>
          <button
            type="button"
            disabled={logout.isPending}
            onClick={() =>
              logout.mutate(undefined, {
                onSuccess: () => {
                  live.stop()
                  void navigate({ to: '/login' })
                },
              })
            }
          >
            Log out
          </button>
        </span>
      </header>
      {liveStatus === 'reconnecting' && (
        <p className="banner" role="status">
          Live updates paused, reconnecting… The page still works; it catches up once connected.
        </p>
      )}
      {children}
    </main>
  )
}

import { useSuspenseQuery } from '@tanstack/react-query'
import { Link, useNavigate } from '@tanstack/react-router'
import type { ReactNode } from 'react'
import { authStatusQuery, useLogout } from '../auth/auth'

/** Top bar with navigation and the logged-in user, around a page. */
export function AppShell({ children }: { children: ReactNode }) {
  const { data: status } = useSuspenseQuery(authStatusQuery)
  const logout = useLogout()
  const navigate = useNavigate()
  const user = status.user

  return (
    <main className="shell">
      <header className="topbar">
        <nav className="row">
          <Link to="/">
            <strong>Tikai</strong>
          </Link>
          <Link to="/projects">Projects</Link>
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
              logout.mutate(undefined, { onSuccess: () => navigate({ to: '/login' }) })
            }
          >
            Log out
          </button>
        </span>
      </header>
      {children}
    </main>
  )
}

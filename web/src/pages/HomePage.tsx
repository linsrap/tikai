import { useSuspenseQuery } from '@tanstack/react-query'
import { useNavigate } from '@tanstack/react-router'
import { authStatusQuery, useLogout } from '../auth/auth'

export function HomePage() {
  const { data: status } = useSuspenseQuery(authStatusQuery)
  const logout = useLogout()
  const navigate = useNavigate()
  const user = status.user

  return (
    <main className="shell">
      <header className="topbar">
        <strong>Tikai</strong>
        <span>
          {user?.displayName}
          {user?.isAdmin ? ' (admin)' : null}
        </span>
        <button
          type="button"
          disabled={logout.isPending}
          onClick={() => logout.mutate(undefined, { onSuccess: () => navigate({ to: '/login' }) })}
        >
          Log out
        </button>
      </header>
      <h1>Welcome, {user?.displayName}</h1>
      <p>Tickets, agents and human review in one place. Projects and tickets come next.</p>
    </main>
  )
}

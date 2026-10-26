import { useSuspenseQuery } from '@tanstack/react-query'
import { Link } from '@tanstack/react-router'
import { authStatusQuery } from '../auth/auth'
import { AppShell } from './AppShell'

export function HomePage() {
  const { data: status } = useSuspenseQuery(authStatusQuery)

  return (
    <AppShell>
      <h1>Welcome, {status.user?.displayName}</h1>
      <p>
        Tickets, agents and human review in one place. Start with a{' '}
        <Link to="/projects">project and its repositories</Link>; tickets come next.
      </p>
    </AppShell>
  )
}

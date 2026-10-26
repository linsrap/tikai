import type { QueryClient } from '@tanstack/react-query'
import {
  createRootRouteWithContext,
  createRoute,
  createRouter,
  Outlet,
  redirect,
} from '@tanstack/react-router'
import { authStatusQuery, entryRouteFor } from './auth/auth'
import { HomePage } from './pages/HomePage'
import { LoginPage } from './pages/LoginPage'
import { ProjectPage } from './pages/ProjectPage'
import { ProjectsPage } from './pages/ProjectsPage'
import { SettingsPage } from './pages/SettingsPage'
import { SignUpPage } from './pages/SignUpPage'
import { TicketPage } from './pages/TicketPage'
import { NewTicketPage, TicketsPage } from './pages/TicketsPage'

type RouterContext = { queryClient: QueryClient }
type EntryPath = '/' | '/login' | '/signup'

const rootRoute = createRootRouteWithContext<RouterContext>()({ component: Outlet })

/** Sends people where they belong: sign-up before any account exists, login without a session, home with one. */
function guard(path: EntryPath) {
  return async ({ context }: { context: RouterContext }) => {
    const status = await context.queryClient.fetchQuery(authStatusQuery)
    const target = entryRouteFor(status)
    if (target !== path) {
      throw redirect({ to: target })
    }
  }
}

/** Pages behind the login: people without a session go to sign-up or login. */
const requireUser = guard('/')

const homeRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: '/',
  beforeLoad: guard('/'),
  component: HomePage,
})

const loginRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: '/login',
  beforeLoad: guard('/login'),
  component: LoginPage,
})

const signUpRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: '/signup',
  beforeLoad: guard('/signup'),
  component: SignUpPage,
})

const projectsRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: '/projects',
  beforeLoad: requireUser,
  component: ProjectsPage,
})

const projectRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: '/projects/$slug',
  beforeLoad: requireUser,
  component: function ProjectRouteComponent() {
    const { slug } = projectRoute.useParams()
    return <ProjectPage slug={slug} />
  },
})

const ticketsRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: '/projects/$slug/tickets',
  beforeLoad: requireUser,
  component: function TicketsRouteComponent() {
    const { slug } = ticketsRoute.useParams()
    return <TicketsPage slug={slug} />
  },
})

const newTicketRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: '/projects/$slug/tickets/new',
  beforeLoad: requireUser,
  component: function NewTicketRouteComponent() {
    const { slug } = newTicketRoute.useParams()
    return <NewTicketPage slug={slug} />
  },
})

const ticketRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: '/projects/$slug/tickets/$number',
  beforeLoad: requireUser,
  component: function TicketRouteComponent() {
    const { slug, number } = ticketRoute.useParams()
    // Keyed by number, so moving to another ticket starts outside edit mode.
    return /^[1-9][0-9]{0,9}$/.test(number) ? (
      <TicketPage key={number} slug={slug} number={Number(number)} />
    ) : (
      <p className="shell">No ticket {number}.</p>
    )
  },
})

const settingsRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: '/settings',
  beforeLoad: requireUser,
  component: SettingsPage,
})

export function createAppRouter(queryClient: QueryClient) {
  return createRouter({
    routeTree: rootRoute.addChildren([
      homeRoute,
      loginRoute,
      signUpRoute,
      projectsRoute,
      projectRoute,
      ticketsRoute,
      newTicketRoute,
      ticketRoute,
      settingsRoute,
    ]),
    context: { queryClient },
  })
}

declare module '@tanstack/react-router' {
  interface Register {
    router: ReturnType<typeof createAppRouter>
  }
}

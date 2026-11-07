import type { QueryClient } from '@tanstack/react-query'
import {
  createRootRouteWithContext,
  createRoute,
  createRouter,
  Outlet,
  redirect,
} from '@tanstack/react-router'
import { authStatusQuery, entryRouteFor } from './auth/auth'
import { inboxQuery } from './inbox/inbox'
import { projectQuery } from './projects/projects'
import { BoardPage } from './pages/BoardPage'
import { InboxPage } from './pages/InboxPage'
import { LoginPage } from './pages/LoginPage'
import { Missing } from './pages/Missing'
import { ProjectPage } from './pages/ProjectPage'
import { ProjectsPage } from './pages/ProjectsPage'
import { ReviewPage } from './pages/ReviewPage'
import { SettingsPage } from './pages/SettingsPage'
import { SignUpPage } from './pages/SignUpPage'
import { TicketPage } from './pages/TicketPage'
import { NewTicketPage, TicketsPage } from './pages/TicketsPage'
import { changeSetQuery } from './review/review'
import { ticketQuery } from './tickets/tickets'

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
    if (path === '/') {
      // Every logged-in page has the waiting count in its tab title.
      await loadForTitle(() => context.queryClient.ensureQueryData({ ...inboxQuery, retry: false }))
    }
  }
}

/** Pages behind the login: people without a session go to sign-up or login. */
const requireUser = guard('/')

/**
 * Loads what a page's tab title needs before the page renders, so the title is right in its first frame (cached data
 * counts, stale or not). A failed load is the page's to show: it fetches again and shows the error.
 */
async function loadForTitle(load: () => Promise<unknown>) {
  try {
    await load()
  } catch {
    // The page shows it.
  }
}

const loadProject = ({ context, params }: { context: RouterContext; params: { slug: string } }) =>
  loadForTitle(() =>
    context.queryClient.ensureQueryData({ ...projectQuery(params.slug), retry: false }),
  )

const ticketNumber = /^[1-9][0-9]{0,9}$/
const reviewId = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i

/** The inbox is the home screen. */
const homeRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: '/',
  beforeLoad: guard('/'),
  component: InboxPage,
})

const boardRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: '/board',
  beforeLoad: requireUser,
  component: BoardPage,
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
  loader: loadProject,
  component: function ProjectRouteComponent() {
    const { slug } = projectRoute.useParams()
    return <ProjectPage slug={slug} />
  },
})

const ticketsRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: '/projects/$slug/tickets',
  beforeLoad: requireUser,
  loader: loadProject,
  component: function TicketsRouteComponent() {
    const { slug } = ticketsRoute.useParams()
    return <TicketsPage slug={slug} />
  },
})

const newTicketRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: '/projects/$slug/tickets/new',
  beforeLoad: requireUser,
  loader: loadProject,
  component: function NewTicketRouteComponent() {
    const { slug } = newTicketRoute.useParams()
    return <NewTicketPage slug={slug} />
  },
})

const ticketRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: '/projects/$slug/tickets/$number',
  beforeLoad: requireUser,
  loader: ({ context, params }) =>
    ticketNumber.test(params.number)
      ? loadForTitle(() =>
          context.queryClient.ensureQueryData({
            ...ticketQuery(params.slug, Number(params.number)),
            retry: false,
          }),
        )
      : undefined,
  component: function TicketRouteComponent() {
    const { slug, number } = ticketRoute.useParams()
    // Keyed by number, so moving to another ticket starts outside edit mode.
    return ticketNumber.test(number) ? (
      <TicketPage key={number} slug={slug} number={Number(number)} />
    ) : (
      <Missing>{`No ticket ${number}.`}</Missing>
    )
  },
})

const reviewRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: '/reviews/$id',
  beforeLoad: requireUser,
  loader: ({ context, params }) =>
    reviewId.test(params.id)
      ? loadForTitle(() =>
          context.queryClient.ensureQueryData({ ...changeSetQuery(params.id), retry: false }),
        )
      : undefined,
  component: function ReviewRouteComponent() {
    const { id } = reviewRoute.useParams()
    return reviewId.test(id) ? (
      <ReviewPage key={id} id={id} />
    ) : (
      <Missing>{`No review ${id}.`}</Missing>
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
      boardRoute,
      loginRoute,
      signUpRoute,
      projectsRoute,
      projectRoute,
      ticketsRoute,
      newTicketRoute,
      ticketRoute,
      reviewRoute,
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

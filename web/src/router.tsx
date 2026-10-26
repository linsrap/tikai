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
import { SignUpPage } from './pages/SignUpPage'

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

export function createAppRouter(queryClient: QueryClient) {
  return createRouter({
    routeTree: rootRoute.addChildren([homeRoute, loginRoute, signUpRoute]),
    context: { queryClient },
  })
}

declare module '@tanstack/react-router' {
  interface Register {
    router: ReturnType<typeof createAppRouter>
  }
}

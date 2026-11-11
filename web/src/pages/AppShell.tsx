import { useQuery, useSuspenseQuery } from '@tanstack/react-query'
import { Link, useNavigate, useParams } from '@tanstack/react-router'
import type { ReactNode } from 'react'
import { authStatusQuery, useLogout } from '../auth/auth'
import { inboxQuery, waitingCount } from '../inbox/inbox'
import { live, useLiveConnection, useLiveStatus, useWatch, type LiveStatus } from '../live/live'
import { isOpen, projectsQuery } from '../projects/projects'
import { cx } from '../ui/cx'
import {
  BoardIcon,
  FolderIcon,
  InboxIcon,
  LogOutIcon,
  PlusIcon,
  SettingsIcon,
  MonitorIcon,
  MoonIcon,
  SunIcon,
} from '../ui/icons'
import { Tooltip } from '../ui/overlay'
import { Segmented } from '../ui/Segmented'
import { setTheme, useThemeChoice, type ThemeChoice } from '../ui/theme'
import { useWaitingInTitle } from '../ui/title'
import { Dot } from '../ui/Tag'

// TanStack Router marks the link of the current page with data-status='active'.
const navItem =
  'flex h-8 items-center gap-2.5 rounded-md px-2 font-medium text-fg-2 hover:text-fg hover:no-underline not-data-[status=active]:hover:bg-hover [&_svg]:text-fg-subtle data-[status=active]:bg-surface data-[status=active]:text-fg data-[status=active]:shadow-[0_0_0_1px_var(--c-line)] data-[status=active]:[&_svg]:text-fg'

const projectCurrent =
  'data-[current=true]:bg-surface data-[current=true]:font-medium data-[current=true]:text-fg data-[current=true]:shadow-[0_0_0_1px_var(--c-line)] data-[current=true]:hover:bg-surface'

const railItem =
  'relative flex size-9 items-center justify-center rounded-md text-fg-subtle hover:text-fg not-data-[status=active]:hover:bg-hover data-[status=active]:bg-surface data-[status=active]:text-fg data-[status=active]:shadow-[0_0_0_1px_var(--c-line)]'

const liveLabel: Record<LiveStatus, string> = {
  off: 'Offline',
  connecting: 'Connecting',
  live: 'Live',
  reconnecting: 'Reconnecting',
}

/**
 * The frame of every logged-in page: a sidebar with navigation, projects and the user, and the page next to it.
 * `rail` shrinks the sidebar to icons (the review screen needs the width). Keeps the live connection open.
 */
export function AppShell({ children, rail = false }: { children: ReactNode; rail?: boolean }) {
  const { data: status } = useSuspenseQuery(authStatusQuery)
  const user = status.user
  useLiveConnection()
  // The inbox count in the sidebar is live on every page.
  useWatch('WatchAll')
  const inbox = useQuery(inboxQuery)
  const waiting = waitingCount(inbox.data)
  useWaitingInTitle(waiting)
  const liveStatus = useLiveStatus()

  return (
    <div className="flex min-h-screen flex-col md:flex-row" data-live-status={liveStatus}>
      {rail ? (
        <Rail waiting={waiting} />
      ) : (
        <Sidebar
          waiting={waiting}
          userName={user?.displayName}
          isAdmin={user?.isAdmin ?? false}
          liveStatus={liveStatus}
        />
      )}
      <main className="flex min-w-0 flex-1 flex-col">
        {liveStatus === 'reconnecting' && (
          <p
            role="status"
            className="m-0 flex items-center gap-2 border-b border-line bg-wait-bg px-4 py-2 text-sm text-wait sm:px-10"
          >
            <Dot tone="wait" />
            Live updates paused, reconnecting… The page still works; it catches up once connected.
          </p>
        )}
        {children}
      </main>
    </div>
  )
}

function Brand({ compact = false }: { compact?: boolean }) {
  return (
    <Link
      to="/"
      aria-label="Tikai home"
      className={cx(
        'flex items-center gap-2 px-2 text-[15px] font-semibold tracking-[-0.01em] text-fg hover:no-underline',
        compact && 'justify-center',
      )}
    >
      <span
        aria-hidden="true"
        className="flex size-5 items-center justify-center rounded-[5px] bg-primary font-mono text-xs font-medium text-on-primary"
      >
        t
      </span>
      <span className={compact ? 'sr-only' : 'max-md:hidden'}>Tikai</span>
    </Link>
  )
}

function WaitingBadge({ waiting }: { waiting: number }) {
  if (waiting === 0) return null
  return (
    <span className="ml-auto flex h-[18px] min-w-5 items-center justify-center rounded-full bg-wait-solid px-1.5 text-xs font-semibold text-white tabular-nums">
      {waiting}
    </span>
  )
}

function Sidebar({
  waiting,
  userName,
  isAdmin,
  liveStatus,
}: {
  waiting: number
  userName: string | undefined
  isAdmin: boolean
  liveStatus: LiveStatus
}) {
  const projects = useQuery(projectsQuery)
  const currentSlug = useParams({ strict: false }).slug

  return (
    <aside className="flex shrink-0 items-center gap-2 border-b border-line bg-sidebar px-3 py-2 md:sticky md:top-0 md:h-screen md:w-[232px] md:flex-col md:items-stretch md:gap-0.5 md:border-r md:border-b-0 md:px-2.5 md:py-3.5">
      <div className="md:pb-3.5">
        <Brand />
      </div>
      <nav
        aria-label="Main"
        className="flex min-w-0 flex-1 items-center gap-0.5 overflow-x-auto md:flex-col md:items-stretch md:overflow-x-visible md:overflow-y-auto"
      >
        <Link
          to="/"
          activeOptions={{ exact: true }}
          className={navItem}
          aria-label={`Inbox, ${waiting} waiting`}
        >
          <InboxIcon />
          Inbox
          <WaitingBadge waiting={waiting} />
        </Link>
        <Link to="/board" className={navItem}>
          <BoardIcon />
          Board
        </Link>
        <Link to="/projects" activeOptions={{ exact: true }} className={cx(navItem, 'md:hidden')}>
          <FolderIcon />
          Projects
        </Link>

        <div className="max-md:hidden">
          <div className="mx-2 mt-5 mb-1 flex items-center justify-between">
            <Link
              to="/projects"
              className="text-[11px] font-semibold tracking-[0.06em] text-fg-subtle uppercase hover:text-fg hover:no-underline"
            >
              Projects
            </Link>
          </div>
          {projects.data?.filter(isOpen).map((project) => (
            <Link
              key={project.id}
              to="/projects/$slug/tickets"
              params={{ slug: project.slug }}
              // Highlighted on every page of the project (tickets, a ticket, settings), not only its list.
              data-current={project.slug === currentSlug}
              className={cx(navItem, projectCurrent, 'h-[30px] font-normal')}
            >
              <span className="w-8 shrink-0 font-mono text-[11px] text-fg-subtle">
                {project.key}
              </span>
              <span className="truncate">{project.name}</span>
            </Link>
          ))}
          <Link
            to="/projects"
            hash="new"
            activeOptions={{ exact: true, includeHash: true }}
            className={cx(navItem, 'h-[30px] font-normal text-fg-subtle')}
          >
            <PlusIcon size={14} />
            New project
          </Link>
        </div>

        <div className="md:mt-3">
          <Link to="/settings" className={navItem}>
            <SettingsIcon />
            Settings
          </Link>
        </div>
      </nav>

      <ThemeSwitch />
      <UserFooter
        userName={userName}
        isAdmin={isAdmin}
        liveStatus={liveStatus}
        className="md:border-t md:border-line md:px-2 md:pt-2.5"
      />
    </aside>
  )
}

function UserFooter({
  userName,
  isAdmin,
  liveStatus,
  className,
}: {
  userName: string | undefined
  isAdmin: boolean
  liveStatus: LiveStatus
  className?: string
}) {
  const initials = (userName ?? '?')
    .split(/\s+/)
    .map((part) => part[0])
    .join('')
    .slice(0, 2)
    .toUpperCase()
  return (
    <div className={cx('flex items-center gap-2.5', className)}>
      <span
        aria-hidden="true"
        className="flex size-[26px] shrink-0 items-center justify-center rounded-full bg-line-strong text-[11px] font-semibold text-fg-2 max-md:hidden"
      >
        {initials}
      </span>
      <span className="flex min-w-0 flex-col leading-4 max-md:hidden">
        <span className="truncate text-sm font-medium">
          {userName}
          {isAdmin && <span className="font-normal text-fg-subtle"> · admin</span>}
        </span>
        <LiveIndicator status={liveStatus} />
      </span>
      <LogoutButton />
    </div>
  )
}

function LiveIndicator({ status }: { status: LiveStatus }) {
  return (
    <span className="flex items-center gap-1.5 text-xs text-fg-muted">
      <Dot tone={status === 'live' ? 'ok' : status === 'reconnecting' ? 'wait' : 'neutral'} />
      {liveLabel[status]}
    </span>
  )
}

function LogoutButton() {
  const logout = useLogout()
  const navigate = useNavigate()
  return (
    <Tooltip content="Log out" side="top">
      <button
        type="button"
        aria-label="Log out"
        className="ml-auto flex size-7 cursor-pointer items-center justify-center rounded-md text-fg-subtle hover:bg-hover hover:text-fg disabled:opacity-50"
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
        <LogOutIcon size={15} />
      </button>
    </Tooltip>
  )
}

function Rail({ waiting }: { waiting: number }) {
  return (
    <aside className="flex shrink-0 items-center gap-1 border-b border-line bg-sidebar px-3 py-2 md:sticky md:top-0 md:h-screen md:w-14 md:flex-col md:border-r md:border-b-0 md:px-0 md:py-3.5">
      <div className="md:mb-3">
        <Brand compact />
      </div>
      <nav aria-label="Main" className="flex items-center gap-1 md:flex-col">
        <Tooltip content={waiting > 0 ? `Inbox · ${waiting} waiting` : 'Inbox'}>
          <Link
            to="/"
            activeOptions={{ exact: true }}
            className={railItem}
            aria-label={`Inbox, ${waiting} waiting`}
          >
            <InboxIcon size={17} />
            {waiting > 0 && (
              <span className="absolute top-1 right-1 size-2 rounded-full border-2 border-sidebar bg-wait-solid" />
            )}
          </Link>
        </Tooltip>
        <Tooltip content="Board">
          <Link to="/board" className={railItem} aria-label="Board">
            <BoardIcon size={17} />
          </Link>
        </Tooltip>
        <Tooltip content="Projects">
          <Link to="/projects" className={railItem} aria-label="Projects">
            <FolderIcon size={17} />
          </Link>
        </Tooltip>
        <Tooltip content="Settings">
          <Link to="/settings" className={railItem} aria-label="Settings">
            <SettingsIcon size={17} />
          </Link>
        </Tooltip>
      </nav>
      <div className="ml-auto flex items-center gap-1 md:mt-auto md:ml-0 md:flex-col">
        <ThemeCycle />
        <LogoutButton />
      </div>
    </aside>
  )
}

const themeIcon = { system: MonitorIcon, light: SunIcon, dark: MoonIcon }
const themeName: Record<ThemeChoice, string> = {
  system: 'Theme: as the system',
  light: 'Theme: light',
  dark: 'Theme: dark',
}

/** System, light or dark, in the sidebar above the user. */
function ThemeSwitch() {
  const choice = useThemeChoice()
  return (
    <div className="max-md:hidden md:mt-auto md:mb-2 md:px-2">
      <Segmented
        label="Theme"
        size="sm"
        value={choice}
        onChange={setTheme}
        options={(['system', 'light', 'dark'] as const).map((value) => {
          const Icon = themeIcon[value]
          return { value, title: themeName[value], label: <Icon size={13} /> }
        })}
      />
    </div>
  )
}

/** The rail has no room for three buttons: one that steps system → light → dark. */
function ThemeCycle() {
  const choice = useThemeChoice()
  const next: ThemeChoice = choice === 'system' ? 'light' : choice === 'light' ? 'dark' : 'system'
  const Icon = themeIcon[choice]
  return (
    <Tooltip content={`${themeName[choice]} (click for ${next})`}>
      <button
        type="button"
        aria-label={`${themeName[choice]}, switch to ${next}`}
        className="flex size-7 cursor-pointer items-center justify-center rounded-md text-fg-subtle hover:bg-hover hover:text-fg"
        onClick={() => setTheme(next)}
      >
        <Icon size={15} />
      </button>
    </Tooltip>
  )
}

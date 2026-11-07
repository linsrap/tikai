import { useQuery } from '@tanstack/react-query'
import { Link } from '@tanstack/react-router'
import type { ReactNode } from 'react'
import { projectQuery } from '../projects/projects'
import { CrumbSep, PageHeader } from '../ui/layout'

const tab =
  'border-b-2 border-transparent pt-2 pb-2.5 font-medium whitespace-nowrap text-fg-muted hover:text-fg hover:no-underline data-[status=active]:border-fg data-[status=active]:text-fg'

/** The header of a project's pages: name, key, and tabs for its tickets and its settings. */
export function ProjectHeader({ slug, actions }: { slug: string; actions?: ReactNode }) {
  const project = useQuery(projectQuery(slug))
  return (
    <PageHeader
      crumbs={
        <>
          <Link to="/projects">Projects</Link>
          <CrumbSep />
          <span>{project.data?.name ?? slug}</span>
        </>
      }
      title={project.data?.name ?? slug}
      titleExtra={
        project.data && (
          <span className="rounded-[4px] bg-neutral-bg px-1.5 font-mono text-xs leading-5 text-fg-muted">
            {project.data.key}
          </span>
        )
      }
      actions={actions}
      tabs={
        <>
          <Link
            to="/projects/$slug/tickets"
            params={{ slug }}
            className={tab}
            activeOptions={{ exact: true }}
          >
            Tickets
          </Link>
          <Link
            to="/projects/$slug"
            params={{ slug }}
            className={tab}
            activeOptions={{ exact: true }}
          >
            Repositories and settings
          </Link>
        </>
      }
    />
  )
}

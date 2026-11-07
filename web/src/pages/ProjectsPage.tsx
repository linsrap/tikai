import { useQuery } from '@tanstack/react-query'
import { Link, useNavigate } from '@tanstack/react-router'
import { useState, type FormEvent } from 'react'
import { projectsQuery, suggestKey, suggestSlug, useCreateProject } from '../projects/projects'
import { Button } from '../ui/Button'
import { ErrorText, Field, Input } from '../ui/form'
import { ChevronRightIcon } from '../ui/icons'
import { EmptyState, FormPanel, Loading, PageBody, PageHeader, SectionLabel } from '../ui/layout'
import { usePageTitle } from '../ui/title'
import { AppShell } from './AppShell'

export function ProjectsPage() {
  const projects = useQuery(projectsQuery)
  usePageTitle('Projects')

  return (
    <AppShell>
      <PageHeader title="Projects" />
      <PageBody>
        {projects.isPending && <Loading />}
        {projects.error && <ErrorText>{projects.error.message}</ErrorText>}
        {projects.data?.length === 0 && (
          <EmptyState title="No projects yet">Create the first one below.</EmptyState>
        )}
        {projects.data && projects.data.length > 0 && (
          <ul className="m-0 list-none rounded-lg border border-line bg-surface p-0">
            {projects.data.map((p) => (
              <li key={p.id} className="border-t border-line-soft first:border-t-0">
                <Link
                  to="/projects/$slug/tickets"
                  params={{ slug: p.slug }}
                  className="group flex items-center gap-3 px-4 py-3 text-fg hover:bg-bg hover:no-underline"
                >
                  <span className="w-12 shrink-0 font-mono text-mono text-fg-muted">{p.key}</span>
                  <span className="min-w-0 flex-1">
                    <span className="block font-semibold">{p.name}</span>
                    <span className="block text-sm text-fg-subtle">/{p.slug}</span>
                  </span>
                  <ChevronRightIcon className="text-fg-faint group-hover:text-fg-muted" />
                </Link>
              </li>
            ))}
          </ul>
        )}
        <SectionLabel className="mt-10">New project</SectionLabel>
        <CreateProjectForm />
      </PageBody>
    </AppShell>
  )
}

function CreateProjectForm() {
  const create = useCreateProject()
  const navigate = useNavigate()
  const [name, setName] = useState('')
  // Slug and key follow the name until someone edits them.
  const [slug, setSlug] = useState<string | null>(null)
  const [key, setKey] = useState<string | null>(null)
  const effectiveSlug = slug ?? suggestSlug(name)
  const effectiveKey = key ?? suggestKey(name)

  function submit(event: FormEvent) {
    event.preventDefault()
    create.mutate(
      { name, slug: effectiveSlug, key: effectiveKey },
      { onSuccess: (p) => navigate({ to: '/projects/$slug', params: { slug: p.slug } }) },
    )
  }

  return (
    <FormPanel
      id="new"
      onSubmit={submit}
      aria-label="New project"
      footer={
        <>
          <Button type="submit" variant="primary" disabled={create.isPending}>
            {create.isPending ? 'Creating…' : 'Create project'}
          </Button>
          {create.error && <ErrorText>{create.error.message}</ErrorText>}
        </>
      }
    >
      <Field label="Name">
        <Input required value={name} onChange={(e) => setName(e.target.value)} />
      </Field>
      <div className="grid gap-4 sm:grid-cols-2">
        <Field label="Slug (URL name, cannot be changed later)">
          <Input
            required
            className="font-mono text-mono"
            value={effectiveSlug}
            onChange={(e) => setSlug(e.target.value)}
          />
        </Field>
        <Field label={`Ticket key (as in ${effectiveKey || 'KEY'}-42, cannot be changed later)`}>
          <Input
            required
            className="font-mono text-mono"
            value={effectiveKey}
            onChange={(e) => setKey(e.target.value.toUpperCase())}
          />
        </Field>
      </div>
    </FormPanel>
  )
}

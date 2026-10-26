import { useQuery } from '@tanstack/react-query'
import { Link, useNavigate } from '@tanstack/react-router'
import { useState, type FormEvent } from 'react'
import { projectsQuery, suggestKey, suggestSlug, useCreateProject } from '../projects/projects'
import { AppShell } from './AppShell'

export function ProjectsPage() {
  const projects = useQuery(projectsQuery)

  return (
    <AppShell>
      <h1>Projects</h1>
      {projects.isPending && <p>Loading…</p>}
      {projects.error && (
        <p role="alert" className="error">
          {projects.error.message}
        </p>
      )}
      {projects.data?.length === 0 && <p>No projects yet. Create the first one below.</p>}
      {projects.data && projects.data.length > 0 && (
        <ul className="list">
          {projects.data.map((p) => (
            <li key={p.id}>
              <Link to="/projects/$slug" params={{ slug: p.slug }}>
                {p.name}
              </Link>{' '}
              <span className="muted">
                {p.key} · /{p.slug}
              </span>
            </li>
          ))}
        </ul>
      )}
      <CreateProjectForm />
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
    <form onSubmit={submit} className="stack card">
      <h2>New project</h2>
      <label className="field">
        <span>Name</span>
        <input required value={name} onChange={(e) => setName(e.target.value)} />
      </label>
      <label className="field">
        <span>Slug (URL name, cannot be changed later)</span>
        <input required value={effectiveSlug} onChange={(e) => setSlug(e.target.value)} />
      </label>
      <label className="field">
        <span>Ticket key (as in {effectiveKey || 'KEY'}-42, cannot be changed later)</span>
        <input
          required
          value={effectiveKey}
          onChange={(e) => setKey(e.target.value.toUpperCase())}
        />
      </label>
      {create.error && (
        <p role="alert" className="error">
          {create.error.message}
        </p>
      )}
      <button type="submit" disabled={create.isPending}>
        {create.isPending ? 'Creating…' : 'Create project'}
      </button>
    </form>
  )
}

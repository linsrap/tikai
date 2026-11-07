import { useQuery } from '@tanstack/react-query'
import { Link, useNavigate } from '@tanstack/react-router'
import { useState, type FormEvent } from 'react'
import {
  branchesQuery,
  bundleUrl,
  emptyRepositoryForm,
  projectQuery,
  repositoriesQuery,
  toCreateRepositoryRequest,
  toCredentialInput,
  useCreateRepository,
  useDeleteProject,
  useConnectRemote,
  useDeleteRepository,
  useFetchRepository,
  useSetCredential,
  type RepositoryForm,
  type RepositoryInfo,
} from '../projects/projects'
import { AnalysisSettings } from './AnalysisSettings'
import { AppShell } from './AppShell'
import { DeliverySettingsForm } from './DeliverySettings'
import { ProvidersSection } from './ProvidersSection'

export function ProjectPage({ slug }: { slug: string }) {
  const project = useQuery(projectQuery(slug))
  const repositories = useQuery(repositoriesQuery(slug))
  const deleteProject = useDeleteProject(slug)
  const navigate = useNavigate()

  if (project.error) {
    return (
      <AppShell>
        <p role="alert" className="error">
          {project.error.message}
        </p>
      </AppShell>
    )
  }

  return (
    <AppShell>
      <h1>{project.data?.name ?? 'Loading…'}</h1>
      {project.data && (
        <p className="muted">
          Ticket key {project.data.key} · /{project.data.slug}
        </p>
      )}
      <p>
        <Link to="/projects/$slug/tickets" params={{ slug }}>
          Tickets
        </Link>
      </p>
      <h2>Repositories</h2>
      {repositories.data?.length === 0 && <p>No repositories yet.</p>}
      {repositories.data?.map((r) => (
        <RepositoryCard key={r.id} slug={slug} repository={r} />
      ))}
      <AddRepositoryForm slug={slug} />
      <h2>LLM providers of this project</h2>
      <p className="muted">
        A provider here is shared by everyone working on the project. Your own providers are under
        Settings.
      </p>
      <ProvidersSection owner={{ kind: 'project', slug }} />
      <h2>Approving a change</h2>
      <DeliverySettingsForm slug={slug} />
      <h2>Review analysis</h2>
      <AnalysisSettings slug={slug} />
      {repositories.data?.length === 0 && (
        <p>
          <button
            type="button"
            className="danger"
            disabled={deleteProject.isPending}
            onClick={() => {
              if (window.confirm('Delete this project?')) {
                deleteProject.mutate(undefined, { onSuccess: () => navigate({ to: '/projects' }) })
              }
            }}
          >
            Delete project
          </button>
          {deleteProject.error && <span className="error"> {deleteProject.error.message}</span>}
        </p>
      )}
    </AppShell>
  )
}

const statusLabel: Record<RepositoryInfo['status'], string> = {
  cloning: 'Cloning…',
  ready: 'Ready',
  failed: 'Clone failed',
}

function RepositoryCard({ slug, repository: r }: { slug: string; repository: RepositoryInfo }) {
  const fetch = useFetchRepository(slug)
  const remove = useDeleteRepository(slug)
  const [showBranches, setShowBranches] = useState(false)
  const error = fetch.error ?? remove.error

  return (
    <section className="card stack">
      <div className="row spread">
        <strong>{r.name}</strong>
        <span className={`badge ${r.status}`}>{statusLabel[r.status]}</span>
      </div>
      <p className="muted">
        {r.remoteUrl ? `Mirror of ${r.remoteUrl}` : 'Local repository (no remote)'}
        {r.defaultBranch && ` · default branch ${r.defaultBranch}`}
        {r.lastFetchedAt && ` · fetched ${new Date(r.lastFetchedAt).toLocaleString()}`}
      </p>
      {r.lastError && (
        <p role="alert" className="error">
          {r.lastError}
        </p>
      )}
      {error && (
        <p role="alert" className="error">
          {error.message}
        </p>
      )}
      <div className="row">
        {r.status === 'ready' && (
          <button type="button" onClick={() => setShowBranches(!showBranches)}>
            {showBranches ? 'Hide branches' : 'Branches'}
          </button>
        )}
        {r.remoteUrl && r.status !== 'cloning' && (
          <button type="button" disabled={fetch.isPending} onClick={() => fetch.mutate(r.id)}>
            {r.status === 'failed' ? 'Retry clone' : 'Fetch now'}
          </button>
        )}
        {r.status === 'ready' && (
          <a
            className="button"
            href={bundleUrl(slug, r.id)}
            download
            title={`git clone ${r.name}.bundle, later git pull ${r.name}.bundle ${r.defaultBranch}`}
          >
            Download as bundle
          </a>
        )}
        <button
          type="button"
          className="danger"
          disabled={remove.isPending}
          onClick={() => {
            if (window.confirm(`Delete the repository "${r.name}" from Tikai?`)) {
              remove.mutate(r.id)
            }
          }}
        >
          Delete
        </button>
      </div>
      {showBranches && <BranchList slug={slug} id={r.id} />}
      {r.remoteUrl && <CredentialForm slug={slug} repository={r} />}
      {r.status === 'ready' && <RemoteForm slug={slug} repository={r} />}
    </section>
  )
}

function BranchList({ slug, id }: { slug: string; id: string }) {
  const branches = useQuery(branchesQuery(slug, id))
  if (branches.error) {
    return <p className="error">{branches.error.message}</p>
  }
  return (
    <ul className="list mono">
      {branches.data?.map((b) => (
        <li key={`${b.isRemote}-${b.name}`}>
          {b.isRemote ? `origin/${b.name}` : b.name}{' '}
          <span className="muted">{b.sha.slice(0, 10)}</span>
        </li>
      ))}
    </ul>
  )
}

function CredentialForm({ slug, repository }: { slug: string; repository: RepositoryInfo }) {
  const setCredential = useSetCredential(slug)
  const [form, setForm] = useState<RepositoryForm>({
    ...emptyRepositoryForm,
    credentialKind: repository.credentialKind,
  })

  function submit(event: FormEvent) {
    event.preventDefault()
    setCredential.mutate(
      { id: repository.id, credential: toCredentialInput(form) ?? { kind: 'none' } },
      { onSuccess: () => setForm({ ...form, token: '', privateKey: '' }) },
    )
  }

  return (
    <details>
      <summary>Change credential (currently: {credentialLabel[repository.credentialKind]})</summary>
      <form onSubmit={submit} className="stack">
        <CredentialFields form={form} onChange={setForm} />
        {setCredential.error && <p className="error">{setCredential.error.message}</p>}
        {setCredential.isSuccess && <p>Saved. Use “Fetch now” or “Retry clone” to use it.</p>}
        <button type="submit" disabled={setCredential.isPending}>
          Save credential
        </button>
      </form>
    </details>
  )
}

/**
 * A local repository gets its first remote, a remote one a new URL. The hub pushes its default branch when the remote
 * is empty or behind, and refuses a remote with a history of its own.
 */
function RemoteForm({ slug, repository }: { slug: string; repository: RepositoryInfo }) {
  const connect = useConnectRemote(slug)
  const local = !repository.remoteUrl
  const [form, setForm] = useState<RepositoryForm>({
    ...emptyRepositoryForm,
    remoteUrl: repository.remoteUrl ?? '',
    credentialKind: local ? 'none' : repository.credentialKind,
  })
  const [keepCredential, setKeepCredential] = useState(!local)

  function submit(event: FormEvent) {
    event.preventDefault()
    connect.mutate(
      {
        id: repository.id,
        remoteUrl: form.remoteUrl.trim(),
        credential: keepCredential ? null : (toCredentialInput(form) ?? null),
      },
      { onSuccess: () => setForm({ ...form, token: '', privateKey: '' }) },
    )
  }

  return (
    <details>
      <summary>{local ? 'Connect a remote' : 'Change remote'}</summary>
      <form
        onSubmit={submit}
        className="stack"
        aria-label={local ? 'Connect a remote' : 'Change remote'}
      >
        <p className="muted">
          {local
            ? `Use an empty repository (no README or license). The hub pushes ${repository.defaultBranch} to it, and approved changes from then on.`
            : `The new remote must have ${repository.defaultBranch} with the hub's history, or be empty.`}
        </p>
        <label className="field">
          <span>Remote URL</span>
          <input
            required
            value={form.remoteUrl}
            placeholder="https://github.com/you/app.git"
            spellCheck={false}
            onChange={(e) => setForm({ ...form, remoteUrl: e.target.value })}
          />
        </label>
        {!local && (
          <label className="row">
            <input
              type="checkbox"
              checked={keepCredential}
              onChange={(e) => setKeepCredential(e.target.checked)}
            />
            Keep the current credential
          </label>
        )}
        {!keepCredential && <CredentialFields form={form} onChange={setForm} />}
        {connect.error && <p className="error">{connect.error.message}</p>}
        {connect.data && (
          <p className="success">
            Connected.
            {connect.data.pushed && ` The hub pushed ${repository.defaultBranch} to it.`}
          </p>
        )}
        <button type="submit" disabled={connect.isPending}>
          {connect.isPending ? 'Connecting…' : local ? 'Connect' : 'Change remote'}
        </button>
      </form>
    </details>
  )
}

const credentialLabel: Record<RepositoryInfo['credentialKind'], string> = {
  none: 'none',
  https_token: 'access token',
  ssh_key: 'SSH key',
}

function CredentialFields({
  form,
  onChange,
}: {
  form: RepositoryForm
  onChange: (form: RepositoryForm) => void
}) {
  return (
    <>
      <label className="field">
        <span>Credential (stored encrypted, never shown again)</span>
        <select
          value={form.credentialKind}
          onChange={(e) =>
            onChange({
              ...form,
              credentialKind: e.target.value as RepositoryForm['credentialKind'],
            })
          }
        >
          <option value="none">None (public HTTPS repository)</option>
          <option value="https_token">HTTPS access token</option>
          <option value="ssh_key">SSH private key</option>
        </select>
      </label>
      {form.credentialKind === 'https_token' && (
        <>
          <label className="field">
            <span>User name (optional; some servers need yours)</span>
            <input
              value={form.username}
              placeholder="x-access-token"
              autoComplete="off"
              onChange={(e) => onChange({ ...form, username: e.target.value })}
            />
          </label>
          <label className="field">
            <span>Access token (with write access: approved changes are pushed)</span>
            <input
              type="password"
              required
              autoComplete="off"
              value={form.token}
              onChange={(e) => onChange({ ...form, token: e.target.value })}
            />
          </label>
        </>
      )}
      {form.credentialKind === 'ssh_key' && (
        <label className="field">
          <span>
            Private key in OpenSSH format, without passphrase. The server's host key is trusted on
            first use.
          </span>
          <textarea
            required
            rows={6}
            className="mono"
            value={form.privateKey}
            placeholder="-----BEGIN OPENSSH PRIVATE KEY-----"
            onChange={(e) => onChange({ ...form, privateKey: e.target.value })}
          />
        </label>
      )}
    </>
  )
}

function AddRepositoryForm({ slug }: { slug: string }) {
  const create = useCreateRepository(slug)
  const [form, setForm] = useState<RepositoryForm>(emptyRepositoryForm)

  function submit(event: FormEvent) {
    event.preventDefault()
    create.mutate(toCreateRepositoryRequest(form), {
      onSuccess: () => setForm(emptyRepositoryForm),
    })
  }

  return (
    <form onSubmit={submit} className="stack card">
      <h2>Add a repository</h2>
      <label className="field">
        <span>Name</span>
        <input
          required
          value={form.name}
          onChange={(e) => setForm({ ...form, name: e.target.value })}
        />
      </label>
      <fieldset className="row">
        <label>
          <input
            type="radio"
            name="source"
            checked={form.source === 'local'}
            onChange={() => setForm({ ...form, source: 'local' })}
          />{' '}
          New local repository
        </label>
        <label>
          <input
            type="radio"
            name="source"
            checked={form.source === 'remote'}
            onChange={() => setForm({ ...form, source: 'remote' })}
          />{' '}
          Mirror a remote
        </label>
      </fieldset>
      {form.source === 'remote' && (
        <label className="field">
          <span>Remote URL (https://… or git@host:path)</span>
          <input
            required
            value={form.remoteUrl}
            placeholder="https://github.com/owner/repo.git"
            onChange={(e) => setForm({ ...form, remoteUrl: e.target.value })}
          />
        </label>
      )}
      <label className="field">
        <span>
          Default branch (optional;{' '}
          {form.source === 'local' ? '"main" if empty' : 'taken from the remote if empty'})
        </span>
        <input
          value={form.defaultBranch}
          onChange={(e) => setForm({ ...form, defaultBranch: e.target.value })}
        />
      </label>
      {form.source === 'remote' && <CredentialFields form={form} onChange={setForm} />}
      {create.error && (
        <p role="alert" className="error">
          {create.error.message}
        </p>
      )}
      <button type="submit" disabled={create.isPending}>
        {create.isPending ? 'Adding…' : 'Add repository'}
      </button>
    </form>
  )
}

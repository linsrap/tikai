import { useQuery } from '@tanstack/react-query'
import { useNavigate } from '@tanstack/react-router'
import { useState, type FormEvent, type ReactNode } from 'react'
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
import { Button } from '../ui/Button'
import { buttonClass } from '../ui/buttonClass'
import { Check, ErrorText, Field, Hint, Input, Select, Textarea } from '../ui/form'
import { ChevronRightIcon, DownloadIcon } from '../ui/icons'
import { FormPanel, PageBody, SettingsSection } from '../ui/layout'
import { ConfirmDialog } from '../ui/overlay'
import { Tag, type Tone } from '../ui/Tag'
import { AnalysisSettings } from './AnalysisSettings'
import { usePageTitle } from '../ui/title'
import { AppShell } from './AppShell'
import { DeliverySettingsForm } from './DeliverySettings'
import { ProjectHeader } from './ProjectHeader'
import { ProvidersSection } from './ProvidersSection'

export function ProjectPage({ slug }: { slug: string }) {
  const project = useQuery(projectQuery(slug))
  usePageTitle(`Settings · ${project.data?.name ?? slug}`)
  const repositories = useQuery(repositoriesQuery(slug))
  const deleteProject = useDeleteProject(slug)
  const navigate = useNavigate()

  return (
    <AppShell>
      <ProjectHeader slug={slug} />
      <PageBody>
        {project.error ? (
          <ErrorText>{project.error.message}</ErrorText>
        ) : (
          <>
            <SettingsSection
              title="Repositories"
              description="Agents clone a repository's default branch for each run; approved changes go back into it."
            >
              {repositories.data && repositories.data.length > 0 && (
                <div className="rounded-lg border border-line bg-surface">
                  {repositories.data.map((r) => (
                    <RepositoryCard key={r.id} slug={slug} repository={r} />
                  ))}
                </div>
              )}
              {repositories.data?.length === 0 && <Hint>No repositories yet.</Hint>}
              <AddRepositoryForm slug={slug} />
            </SettingsSection>
            <SettingsSection
              title="LLM providers of this project"
              description="A provider here is shared by everyone working on the project. Your own providers are under Settings."
            >
              <ProvidersSection owner={{ kind: 'project', slug }} />
            </SettingsSection>
            <SettingsSection
              title="Approving a change"
              description="What happens when you approve an agent's change."
            >
              <DeliverySettingsForm slug={slug} />
            </SettingsSection>
            <SettingsSection
              title="Review analysis"
              description="Every submitted change is analysed with the run's provider before you read it. The tier depends on the change; each tier uses its own model. Empty fields use the default shown."
            >
              <AnalysisSettings slug={slug} />
            </SettingsSection>
            {repositories.data?.length === 0 && (
              <SettingsSection
                title="Delete project"
                description="Possible while the project has no repositories and no tickets."
              >
                <div className="flex flex-wrap items-center gap-3">
                  <ConfirmDialog
                    trigger={
                      <Button
                        variant="danger"
                        className="border-line-strong"
                        disabled={deleteProject.isPending}
                      >
                        Delete project
                      </Button>
                    }
                    title={`Delete ${project.data?.name ?? 'this project'}?`}
                    description="This cannot be undone."
                    confirmLabel="Delete project"
                    danger
                    onConfirm={() =>
                      deleteProject.mutate(undefined, {
                        onSuccess: () => navigate({ to: '/projects' }),
                      })
                    }
                  />
                  {deleteProject.error && <ErrorText>{deleteProject.error.message}</ErrorText>}
                </div>
              </SettingsSection>
            )}
          </>
        )}
      </PageBody>
    </AppShell>
  )
}

const statusLabel: Record<RepositoryInfo['status'], string> = {
  cloning: 'Cloning…',
  ready: 'Ready',
  failed: 'Clone failed',
}

const statusTone: Record<RepositoryInfo['status'], Tone> = {
  cloning: 'run',
  ready: 'ok',
  failed: 'bad',
}

/** A disclosure inside a repository row (branches, credential, remote). */
function Disclosure({ summary, children }: { summary: string; children: ReactNode }) {
  return (
    <details className="group">
      <summary className="flex w-fit cursor-pointer list-none items-center gap-1 text-sm font-medium text-fg-muted hover:text-fg [&::-webkit-details-marker]:hidden">
        <ChevronRightIcon size={14} className="transition-transform group-open:rotate-90" />
        {summary}
      </summary>
      <div className="mt-3 ml-[18px]">{children}</div>
    </details>
  )
}

function RepositoryCard({ slug, repository: r }: { slug: string; repository: RepositoryInfo }) {
  const fetch = useFetchRepository(slug)
  const remove = useDeleteRepository(slug)
  const [showBranches, setShowBranches] = useState(false)
  const error = fetch.error ?? remove.error

  return (
    <section
      aria-label={r.name}
      className="flex flex-col gap-3 border-t border-line-soft px-4 py-3.5 first:border-t-0"
    >
      <div className="flex flex-wrap items-start gap-x-3 gap-y-2">
        <div className="min-w-0">
          <div className="flex items-center gap-2">
            <strong className="font-semibold">{r.name}</strong>
            <Tag tone={statusTone[r.status]}>{statusLabel[r.status]}</Tag>
          </div>
          <p className="m-0 mt-0.5 text-sm [overflow-wrap:anywhere] text-fg-muted">
            {r.remoteUrl ? (
              <>
                Mirror of <span className="font-mono text-mono">{r.remoteUrl}</span>
              </>
            ) : (
              'Local repository (no remote)'
            )}
            {r.defaultBranch && (
              <>
                {' · default branch '}
                <span className="font-mono text-mono">{r.defaultBranch}</span>
              </>
            )}
            {r.lastFetchedAt && ` · fetched ${new Date(r.lastFetchedAt).toLocaleString()}`}
          </p>
        </div>
        <div className="ml-auto flex flex-wrap gap-1.5">
          {r.status === 'ready' && (
            <Button size="sm" variant="ghost" onClick={() => setShowBranches(!showBranches)}>
              {showBranches ? 'Hide branches' : 'Branches'}
            </Button>
          )}
          {r.remoteUrl && r.status !== 'cloning' && (
            <Button size="sm" disabled={fetch.isPending} onClick={() => fetch.mutate(r.id)}>
              {r.status === 'failed' ? 'Retry clone' : 'Fetch now'}
            </Button>
          )}
          {r.status === 'ready' && (
            <a
              className={buttonClass('secondary', 'sm')}
              href={bundleUrl(slug, r.id)}
              download
              title={`git clone ${r.name}.bundle, later git pull ${r.name}.bundle ${r.defaultBranch}`}
            >
              <DownloadIcon size={13} />
              Download as bundle
            </a>
          )}
          <ConfirmDialog
            trigger={
              <Button size="sm" variant="danger" disabled={remove.isPending}>
                Delete
              </Button>
            }
            title={`Delete the repository “${r.name}” from Tikai?`}
            description="The hub's copy is removed. A remote repository itself is not touched."
            confirmLabel="Delete repository"
            danger
            onConfirm={() => remove.mutate(r.id)}
          />
        </div>
      </div>
      {r.lastError && <ErrorText>{r.lastError}</ErrorText>}
      {error && <ErrorText>{error.message}</ErrorText>}
      {showBranches && <BranchList slug={slug} id={r.id} />}
      {(r.remoteUrl || r.status === 'ready') && (
        <div className="flex flex-col gap-2">
          {r.remoteUrl && <CredentialForm slug={slug} repository={r} />}
          {r.status === 'ready' && <RemoteForm slug={slug} repository={r} />}
        </div>
      )}
    </section>
  )
}

function BranchList({ slug, id }: { slug: string; id: string }) {
  const branches = useQuery(branchesQuery(slug, id))
  if (branches.error) {
    return <ErrorText>{branches.error.message}</ErrorText>
  }
  return (
    <ul className="m-0 list-none space-y-0.5 rounded-md border border-line-soft bg-bg px-3 py-2 font-mono text-mono">
      {branches.data?.map((b) => (
        <li key={`${b.isRemote}-${b.name}`} className="flex gap-3">
          <span className="min-w-0 flex-1 truncate">
            {b.isRemote ? `origin/${b.name}` : b.name}
          </span>
          <span className="text-fg-subtle">{b.sha.slice(0, 10)}</span>
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
    <Disclosure
      summary={`Change credential (currently: ${credentialLabel[repository.credentialKind]})`}
    >
      <form onSubmit={submit} className="flex max-w-xl flex-col gap-4">
        <CredentialFields form={form} onChange={setForm} />
        {setCredential.error && <ErrorText>{setCredential.error.message}</ErrorText>}
        {setCredential.isSuccess && (
          <p className="m-0 text-sm text-ok">Saved. Use “Fetch now” or “Retry clone” to use it.</p>
        )}
        <div>
          <Button type="submit" variant="primary" disabled={setCredential.isPending}>
            Save credential
          </Button>
        </div>
      </form>
    </Disclosure>
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
    <Disclosure summary={local ? 'Connect a remote' : 'Change remote'}>
      <form
        onSubmit={submit}
        className="flex max-w-xl flex-col gap-4"
        aria-label={local ? 'Connect a remote' : 'Change remote'}
      >
        <Hint>
          {local
            ? `Use an empty repository (no README or license). The hub pushes ${repository.defaultBranch} to it, and approved changes from then on.`
            : `The new remote must have ${repository.defaultBranch} with the hub's history, or be empty.`}
        </Hint>
        <Field label="Remote URL">
          <Input
            required
            className="font-mono text-mono"
            value={form.remoteUrl}
            placeholder="https://github.com/you/app.git"
            spellCheck={false}
            onChange={(e) => setForm({ ...form, remoteUrl: e.target.value })}
          />
        </Field>
        {!local && (
          <Check
            type="checkbox"
            checked={keepCredential}
            onChange={(e) => setKeepCredential(e.target.checked)}
            label="Keep the current credential"
          />
        )}
        {!keepCredential && <CredentialFields form={form} onChange={setForm} />}
        {connect.error && <ErrorText>{connect.error.message}</ErrorText>}
        {connect.data && (
          <p className="m-0 text-sm text-ok">
            Connected.
            {connect.data.pushed && ` The hub pushed ${repository.defaultBranch} to it.`}
          </p>
        )}
        <div>
          <Button type="submit" variant="primary" disabled={connect.isPending}>
            {connect.isPending ? 'Connecting…' : local ? 'Connect' : 'Change remote'}
          </Button>
        </div>
      </form>
    </Disclosure>
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
      <Field label="Credential (stored encrypted, never shown again)">
        <Select
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
        </Select>
      </Field>
      {form.credentialKind === 'https_token' && (
        <>
          <Field label="User name (optional; some servers need yours)">
            <Input
              value={form.username}
              placeholder="x-access-token"
              autoComplete="off"
              onChange={(e) => onChange({ ...form, username: e.target.value })}
            />
          </Field>
          <Field label="Access token (with write access: approved changes are pushed)">
            <Input
              type="password"
              required
              autoComplete="off"
              value={form.token}
              onChange={(e) => onChange({ ...form, token: e.target.value })}
            />
          </Field>
        </>
      )}
      {form.credentialKind === 'ssh_key' && (
        <Field label="Private key in OpenSSH format, without passphrase. The server's host key is trusted on first use.">
          <Textarea
            required
            rows={6}
            mono
            value={form.privateKey}
            placeholder="-----BEGIN OPENSSH PRIVATE KEY-----"
            onChange={(e) => onChange({ ...form, privateKey: e.target.value })}
          />
        </Field>
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
    <FormPanel
      onSubmit={submit}
      aria-label="Add a repository"
      footer={
        <>
          <Button type="submit" variant="primary" disabled={create.isPending}>
            {create.isPending ? 'Adding…' : 'Add repository'}
          </Button>
          {create.error && <ErrorText>{create.error.message}</ErrorText>}
        </>
      }
    >
      <h3 className="m-0 text-sm font-semibold">Add a repository</h3>
      <Field label="Name">
        <Input
          required
          value={form.name}
          onChange={(e) => setForm({ ...form, name: e.target.value })}
        />
      </Field>
      <fieldset className="flex flex-wrap gap-x-6 gap-y-2">
        <legend className="sr-only">Source</legend>
        <Check
          type="radio"
          name="source"
          checked={form.source === 'local'}
          onChange={() => setForm({ ...form, source: 'local' })}
          label="New local repository"
        />
        <Check
          type="radio"
          name="source"
          checked={form.source === 'remote'}
          onChange={() => setForm({ ...form, source: 'remote' })}
          label="Mirror a remote"
        />
      </fieldset>
      {form.source === 'remote' && (
        <Field label="Remote URL (https://… or git@host:path)">
          <Input
            required
            className="font-mono text-mono"
            value={form.remoteUrl}
            placeholder="https://github.com/owner/repo.git"
            onChange={(e) => setForm({ ...form, remoteUrl: e.target.value })}
          />
        </Field>
      )}
      <Field
        label={`Default branch (optional; ${form.source === 'local' ? '"main" if empty' : 'taken from the remote if empty'})`}
      >
        <Input
          className="font-mono text-mono"
          value={form.defaultBranch}
          onChange={(e) => setForm({ ...form, defaultBranch: e.target.value })}
        />
      </Field>
      {form.source === 'remote' && <CredentialFields form={form} onChange={setForm} />}
    </FormPanel>
  )
}

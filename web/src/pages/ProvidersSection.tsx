import { useQuery } from '@tanstack/react-query'
import { useState, type FormEvent } from 'react'
import {
  emptyProviderForm,
  providersQuery,
  toCreateRequest,
  toUpdateRequest,
  useCreateProvider,
  useDeleteProvider,
  useTestProvider,
  useUpdateProvider,
  type LlmProviderInfo,
  type ProviderForm,
  type ProviderOwner,
} from '../llm/llm'

/** LLM providers of one owner (the user, or a project): list, add, change, test, delete. */
export function ProvidersSection({ owner }: { owner: ProviderOwner }) {
  const providers = useQuery(providersQuery(owner))

  return (
    <>
      {providers.error && <p className="error">{providers.error.message}</p>}
      {providers.data?.length === 0 && <p>No providers yet.</p>}
      {providers.data?.map((p) => (
        <ProviderCard key={p.id} owner={owner} provider={p} />
      ))}
      <AddProviderForm owner={owner} />
    </>
  )
}

function ProviderCard({ owner, provider: p }: { owner: ProviderOwner; provider: LlmProviderInfo }) {
  const test = useTestProvider()
  const remove = useDeleteProvider(owner)
  const [editing, setEditing] = useState(false)

  return (
    <section className="card stack">
      <div className="row spread">
        <strong>{p.name}</strong>
        <span className="muted">Anthropic</span>
      </div>
      <p className="muted">
        Model {p.defaultModel} · key …{p.keyHint || '(short)'}
        {p.baseUrl && ` · via ${p.baseUrl}`}
      </p>
      {test.data && (
        <p role="status" className={test.data.ok ? 'success' : 'error'}>
          {test.data.ok
            ? `Connected in ${(test.data.durationMs / 1000).toFixed(1)} s. The model answered: “${test.data.reply}”`
            : test.data.message}
        </p>
      )}
      {(test.error ?? remove.error) && (
        <p role="alert" className="error">
          {(test.error ?? remove.error)?.message}
        </p>
      )}
      <div className="row">
        <button type="button" disabled={test.isPending} onClick={() => test.mutate(p.id)}>
          {test.isPending ? 'Testing…' : 'Test connection'}
        </button>
        <button type="button" onClick={() => setEditing(!editing)}>
          {editing ? 'Cancel' : 'Change'}
        </button>
        <button
          type="button"
          className="danger"
          disabled={remove.isPending}
          onClick={() => {
            if (window.confirm(`Delete the provider "${p.name}" and its stored key?`)) {
              remove.mutate(p.id)
            }
          }}
        >
          Delete
        </button>
      </div>
      {editing && <EditProviderForm owner={owner} provider={p} onDone={() => setEditing(false)} />}
    </section>
  )
}

function EditProviderForm({
  owner,
  provider,
  onDone,
}: {
  owner: ProviderOwner
  provider: LlmProviderInfo
  onDone: () => void
}) {
  const update = useUpdateProvider(owner)
  const [form, setForm] = useState<ProviderForm>({
    name: provider.name,
    defaultModel: provider.defaultModel,
    apiKey: '',
    baseUrl: provider.baseUrl ?? '',
  })

  function submit(event: FormEvent) {
    event.preventDefault()
    update.mutate({ id: provider.id, body: toUpdateRequest(form) }, { onSuccess: onDone })
  }

  return (
    <form onSubmit={submit} className="stack">
      <ProviderFields form={form} onChange={setForm} keyRequired={false} />
      {update.error && <p className="error">{update.error.message}</p>}
      <button type="submit" disabled={update.isPending}>
        Save
      </button>
    </form>
  )
}

function AddProviderForm({ owner }: { owner: ProviderOwner }) {
  const create = useCreateProvider(owner)
  const [form, setForm] = useState<ProviderForm>(emptyProviderForm)

  function submit(event: FormEvent) {
    event.preventDefault()
    create.mutate(toCreateRequest(form), { onSuccess: () => setForm(emptyProviderForm) })
  }

  return (
    <form onSubmit={submit} className="stack card">
      <h3>Add a provider</h3>
      <ProviderFields form={form} onChange={setForm} keyRequired />
      {create.error && (
        <p role="alert" className="error">
          {create.error.message}
        </p>
      )}
      <button type="submit" disabled={create.isPending}>
        {create.isPending ? 'Adding…' : 'Add provider'}
      </button>
    </form>
  )
}

function ProviderFields({
  form,
  onChange,
  keyRequired,
}: {
  form: ProviderForm
  onChange: (form: ProviderForm) => void
  keyRequired: boolean
}) {
  return (
    <>
      <label className="field">
        <span>Name</span>
        <input
          required
          value={form.name}
          onChange={(e) => onChange({ ...form, name: e.target.value })}
        />
      </label>
      <label className="field">
        <span>Model</span>
        <input
          required
          className="mono"
          value={form.defaultModel}
          onChange={(e) => onChange({ ...form, defaultModel: e.target.value })}
        />
      </label>
      <label className="field">
        <span>
          {keyRequired
            ? 'Anthropic API key (stored encrypted, never shown again)'
            : 'New API key (leave empty to keep the stored one)'}
        </span>
        <input
          type="password"
          autoComplete="off"
          required={keyRequired}
          placeholder="sk-ant-…"
          value={form.apiKey}
          onChange={(e) => onChange({ ...form, apiKey: e.target.value })}
        />
      </label>
      <details>
        <summary>Advanced</summary>
        <label className="field">
          <span>
            Base URL (optional, for a gateway or proxy; it must accept the key as a bearer token)
          </span>
          <input
            value={form.baseUrl}
            placeholder="https://api.anthropic.com"
            onChange={(e) => onChange({ ...form, baseUrl: e.target.value })}
          />
        </label>
      </details>
    </>
  )
}

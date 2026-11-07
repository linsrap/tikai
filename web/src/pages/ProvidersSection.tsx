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
import { Button } from '../ui/Button'
import { ErrorText, Field, Input } from '../ui/form'
import { ChevronRightIcon } from '../ui/icons'
import { FormPanel } from '../ui/layout'
import { ConfirmDialog } from '../ui/overlay'

/** LLM providers of one owner (the user, or a project): list, add, change, test, delete. */
export function ProvidersSection({ owner }: { owner: ProviderOwner }) {
  const providers = useQuery(providersQuery(owner))

  return (
    <>
      {providers.error && <ErrorText>{providers.error.message}</ErrorText>}
      {providers.data && providers.data.length > 0 && (
        <div className="rounded-lg border border-line bg-surface">
          {providers.data.map((p) => (
            <ProviderRow key={p.id} owner={owner} provider={p} />
          ))}
        </div>
      )}
      {providers.data?.length === 0 && (
        <p className="m-0 text-sm text-fg-subtle">No providers yet.</p>
      )}
      <AddProviderForm owner={owner} />
    </>
  )
}

function ProviderRow({ owner, provider: p }: { owner: ProviderOwner; provider: LlmProviderInfo }) {
  const test = useTestProvider()
  const remove = useDeleteProvider(owner)
  const [editing, setEditing] = useState(false)

  return (
    <section
      aria-label={p.name}
      className="flex flex-col gap-3 border-t border-line-soft px-4 py-3.5 first:border-t-0"
    >
      <div className="flex flex-wrap items-center gap-x-3 gap-y-2">
        <div className="min-w-0">
          <div className="flex items-baseline gap-2">
            <strong className="font-semibold">{p.name}</strong>
            <span className="text-sm text-fg-subtle">Anthropic</span>
          </div>
          <p className="m-0 mt-0.5 text-sm text-fg-muted">
            <span className="font-mono text-mono">{p.defaultModel}</span> · key …
            {p.keyHint || '(short)'}
            {p.baseUrl && ` · via ${p.baseUrl}`}
          </p>
        </div>
        <div className="ml-auto flex flex-wrap gap-1.5">
          <Button size="sm" disabled={test.isPending} onClick={() => test.mutate(p.id)}>
            {test.isPending ? 'Testing…' : 'Test connection'}
          </Button>
          <Button size="sm" variant="ghost" onClick={() => setEditing(!editing)}>
            {editing ? 'Cancel' : 'Change'}
          </Button>
          <ConfirmDialog
            trigger={
              <Button size="sm" variant="danger" disabled={remove.isPending}>
                Delete
              </Button>
            }
            title={`Delete the provider “${p.name}”?`}
            description="Its stored key is deleted too. Runs and reviews that use it can no longer call the model."
            confirmLabel="Delete provider"
            danger
            onConfirm={() => remove.mutate(p.id)}
          />
        </div>
      </div>
      {test.data && (
        <p
          role="status"
          className={
            test.data.ok
              ? 'm-0 rounded-md bg-ok-bg px-3 py-2 text-sm text-ok'
              : 'm-0 rounded-md bg-bad-bg px-3 py-2 text-sm text-bad'
          }
        >
          {test.data.ok
            ? `Connected in ${(test.data.durationMs / 1000).toFixed(1)} s. The model answered: “${test.data.reply}”`
            : test.data.message}
        </p>
      )}
      {(test.error ?? remove.error) && (
        <ErrorText>{(test.error ?? remove.error)?.message}</ErrorText>
      )}
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
    <form onSubmit={submit} className="flex flex-col gap-4 rounded-md bg-bg p-4">
      <ProviderFields form={form} onChange={setForm} keyRequired={false} />
      {update.error && <ErrorText>{update.error.message}</ErrorText>}
      <div>
        <Button type="submit" variant="primary" disabled={update.isPending}>
          Save
        </Button>
      </div>
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
    <FormPanel
      onSubmit={submit}
      aria-label="Add a provider"
      footer={
        <>
          <Button type="submit" variant="primary" disabled={create.isPending}>
            {create.isPending ? 'Adding…' : 'Add provider'}
          </Button>
          {create.error && <ErrorText>{create.error.message}</ErrorText>}
        </>
      }
    >
      <h3 className="m-0 text-sm font-semibold">Add a provider</h3>
      <ProviderFields form={form} onChange={setForm} keyRequired />
    </FormPanel>
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
      <div className="grid gap-4 sm:grid-cols-2">
        <Field label="Name">
          <Input
            required
            value={form.name}
            onChange={(e) => onChange({ ...form, name: e.target.value })}
          />
        </Field>
        <Field label="Model">
          <Input
            required
            className="font-mono text-mono"
            value={form.defaultModel}
            onChange={(e) => onChange({ ...form, defaultModel: e.target.value })}
          />
        </Field>
      </div>
      <Field
        label={
          keyRequired
            ? 'Anthropic API key (stored encrypted, never shown again)'
            : 'New API key (leave empty to keep the stored one)'
        }
      >
        <Input
          type="password"
          autoComplete="off"
          required={keyRequired}
          placeholder="sk-ant-…"
          value={form.apiKey}
          onChange={(e) => onChange({ ...form, apiKey: e.target.value })}
        />
      </Field>
      <details className="group">
        <summary className="flex w-fit cursor-pointer list-none items-center gap-1 text-sm font-medium text-fg-muted hover:text-fg [&::-webkit-details-marker]:hidden">
          <ChevronRightIcon size={14} className="transition-transform group-open:rotate-90" />
          Advanced
        </summary>
        <div className="mt-3">
          <Field label="Base URL (optional, for a gateway or proxy; it must accept the key as a bearer token)">
            <Input
              value={form.baseUrl}
              placeholder="https://api.anthropic.com"
              onChange={(e) => onChange({ ...form, baseUrl: e.target.value })}
            />
          </Field>
        </div>
      </details>
    </>
  )
}

import { useQuery } from '@tanstack/react-query'
import { useState, type FormEvent } from 'react'
import {
  projectSettingsQuery,
  useSaveProjectSettings,
  type DeliverySettings,
  type ProjectSettings,
} from '../projects/projects'

/** What Approve does in this project: merge in the hub (squash or merge commit) or push the run branch. */
export function DeliverySettingsForm({ slug }: { slug: string }) {
  const settings = useQuery(projectSettingsQuery(slug))
  if (settings.error) return <p className="error">{settings.error.message}</p>
  if (!settings.data) return <p className="muted">Loading…</p>
  return <Form slug={slug} settings={settings.data} />
}

function Form({ slug, settings }: { slug: string; settings: ProjectSettings }) {
  const save = useSaveProjectSettings(slug)
  const [delivery, setDelivery] = useState<DeliverySettings>(settings.delivery)
  const merge = delivery.mode !== 'push_branch'

  const submit = (e: FormEvent) => {
    e.preventDefault()
    save.mutate({ analysisModels: settings.analysisModels, delivery })
  }

  return (
    <form className="card stack" onSubmit={submit} aria-label="Approving a change">
      <fieldset className="stack">
        <legend>When a change is approved</legend>
        <label className="row">
          <input
            type="radio"
            name="mode"
            checked={merge}
            onChange={() => setDelivery({ ...delivery, mode: 'merge' })}
          />
          Merge it into the default branch, and push that when the repository has a remote
        </label>
        <label className="row">
          <input
            type="radio"
            name="mode"
            checked={!merge}
            onChange={() => setDelivery({ ...delivery, mode: 'push_branch' })}
          />
          Push the run branch to the remote, to merge it there (for example as a pull request)
        </label>
      </fieldset>
      {merge ? (
        <fieldset className="stack">
          <legend>Merge as</legend>
          <label className="row">
            <input
              type="radio"
              name="style"
              checked={delivery.mergeStyle !== 'merge_commit'}
              onChange={() => setDelivery({ ...delivery, mergeStyle: 'squash' })}
            />
            One squashed commit (the agent&apos;s commits stay in the review history)
          </label>
          <label className="row">
            <input
              type="radio"
              name="style"
              checked={delivery.mergeStyle === 'merge_commit'}
              onChange={() => setDelivery({ ...delivery, mergeStyle: 'merge_commit' })}
            />
            A merge commit that keeps the agent&apos;s commits
          </label>
        </fieldset>
      ) : (
        <p className="muted">
          A repository without a remote has nowhere to push to: its changes are merged in the hub.
        </p>
      )}
      <div className="row">
        <button type="submit" className="primary" disabled={save.isPending}>
          {save.isPending ? 'Saving…' : 'Save'}
        </button>
        {save.isSuccess && !save.isPending && <span className="success">Saved.</span>}
        {save.error && <span className="error">{save.error.message}</span>}
      </div>
    </form>
  )
}

import { useQuery } from '@tanstack/react-query'
import { useState, type FormEvent } from 'react'
import {
  projectSettingsQuery,
  useSaveProjectSettings,
  type ProjectSettings,
  type ReviewModels,
} from '../projects/projects'

const tiers = [
  { key: 'light', label: 'Light', hint: 'small changes to docs and tests' },
  { key: 'normal', label: 'Normal', hint: 'everything else' },
  {
    key: 'strict',
    label: 'Strict',
    hint: 'auth, security, migrations, deployment, or over 1 500 lines',
  },
] as const

/** The model per review analysis tier; empty fields use the defaults. */
export function AnalysisSettings({ slug }: { slug: string }) {
  const settings = useQuery(projectSettingsQuery(slug))
  if (settings.error) return <p className="error">{settings.error.message}</p>
  if (!settings.data) return <p className="muted">Loading…</p>
  return <AnalysisSettingsForm slug={slug} settings={settings.data} />
}

function AnalysisSettingsForm({ slug, settings }: { slug: string; settings: ProjectSettings }) {
  const save = useSaveProjectSettings(slug)
  const [models, setModels] = useState<ReviewModels>(settings.analysisModels)

  const submit = (e: FormEvent) => {
    e.preventDefault()
    save.mutate({
      light: models.light?.trim() || null,
      normal: models.normal?.trim() || null,
      strict: models.strict?.trim() || null,
    })
  }

  return (
    <form className="card stack" onSubmit={submit} aria-label="Review analysis models">
      <p className="muted">
        Every submitted change is analysed with the run&apos;s provider before you read it. The tier
        depends on the change; each tier uses its own model.
      </p>
      {tiers.map((tier) => (
        <label key={tier.key} className="stack">
          <span>
            {tier.label} <span className="muted">({tier.hint})</span>
          </span>
          <input
            value={models[tier.key] ?? ''}
            placeholder={settings.defaultAnalysisModels[tier.key] ?? ''}
            onChange={(e) => setModels({ ...models, [tier.key]: e.target.value })}
            spellCheck={false}
          />
        </label>
      ))}
      <div className="row">
        <button type="submit" className="primary" disabled={save.isPending}>
          {save.isPending ? 'Saving…' : 'Save models'}
        </button>
        {save.isSuccess && !save.isPending && <span className="success">Saved.</span>}
        {save.error && <span className="error">{save.error.message}</span>}
      </div>
    </form>
  )
}

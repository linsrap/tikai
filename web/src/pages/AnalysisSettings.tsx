import { useQuery } from '@tanstack/react-query'
import { useState, type FormEvent } from 'react'
import {
  projectSettingsQuery,
  useSaveProjectSettings,
  type ProjectSettings,
  type ReviewModels,
} from '../projects/projects'
import { Button } from '../ui/Button'
import { ErrorText, Field, Input } from '../ui/form'
import { FormPanel, Loading } from '../ui/layout'

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
  if (settings.error) return <ErrorText>{settings.error.message}</ErrorText>
  if (!settings.data) return <Loading />
  return <AnalysisSettingsForm slug={slug} settings={settings.data} />
}

function AnalysisSettingsForm({ slug, settings }: { slug: string; settings: ProjectSettings }) {
  const save = useSaveProjectSettings(slug)
  const [models, setModels] = useState<ReviewModels>(settings.analysisModels)

  const submit = (e: FormEvent) => {
    e.preventDefault()
    save.mutate({
      analysisModels: {
        light: models.light?.trim() || null,
        normal: models.normal?.trim() || null,
        strict: models.strict?.trim() || null,
      },
    })
  }

  return (
    <FormPanel
      onSubmit={submit}
      aria-label="Review analysis models"
      footer={
        <>
          <Button type="submit" variant="primary" disabled={save.isPending}>
            {save.isPending ? 'Saving…' : 'Save models'}
          </Button>
          {save.isSuccess && !save.isPending && <span className="text-sm text-ok">Saved.</span>}
          {save.error && <ErrorText>{save.error.message}</ErrorText>}
        </>
      }
    >
      {tiers.map((tier) => (
        <Field key={tier.key} label={tier.label} hint={`Used for ${tier.hint}.`}>
          <Input
            className="font-mono text-mono"
            value={models[tier.key] ?? ''}
            placeholder={settings.defaultAnalysisModels[tier.key] ?? ''}
            onChange={(e) => setModels({ ...models, [tier.key]: e.target.value })}
            spellCheck={false}
          />
        </Field>
      ))}
    </FormPanel>
  )
}

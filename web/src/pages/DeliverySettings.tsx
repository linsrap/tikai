import { useQuery } from '@tanstack/react-query'
import { useState, type FormEvent } from 'react'
import {
  projectSettingsQuery,
  useSaveProjectSettings,
  type DeliverySettings,
  type ProjectSettings,
} from '../projects/projects'
import { Button } from '../ui/Button'
import { Check, ErrorText, Hint } from '../ui/form'
import { FormPanel, Loading } from '../ui/layout'

/** What Approve does in this project: merge in the hub (squash or merge commit) or push the run branch. */
export function DeliverySettingsForm({ slug }: { slug: string }) {
  const settings = useQuery(projectSettingsQuery(slug))
  if (settings.error) return <ErrorText>{settings.error.message}</ErrorText>
  if (!settings.data) return <Loading />
  return <Form slug={slug} settings={settings.data} />
}

const legend = 'mb-2 p-0 text-sm font-medium text-fg-2'

function Form({ slug, settings }: { slug: string; settings: ProjectSettings }) {
  const save = useSaveProjectSettings(slug)
  const [delivery, setDelivery] = useState<DeliverySettings>(settings.delivery)
  const merge = delivery.mode !== 'push_branch'

  const submit = (e: FormEvent) => {
    e.preventDefault()
    save.mutate({ analysisModels: settings.analysisModels, delivery })
  }

  return (
    <FormPanel
      onSubmit={submit}
      aria-label="Approving a change"
      footer={
        <>
          <Button type="submit" variant="primary" disabled={save.isPending}>
            {save.isPending ? 'Saving…' : 'Save'}
          </Button>
          {save.isSuccess && !save.isPending && <span className="text-sm text-ok">Saved.</span>}
          {save.error && <ErrorText>{save.error.message}</ErrorText>}
        </>
      }
    >
      <fieldset className="flex flex-col gap-2">
        <legend className={legend}>When a change is approved</legend>
        <Check
          type="radio"
          name="mode"
          checked={merge}
          onChange={() => setDelivery({ ...delivery, mode: 'merge' })}
          label="Merge it into the default branch, and push that when the repository has a remote"
        />
        <Check
          type="radio"
          name="mode"
          checked={!merge}
          onChange={() => setDelivery({ ...delivery, mode: 'push_branch' })}
          label="Push the run branch to the remote, to merge it there (for example as a pull request)"
        />
      </fieldset>
      {merge ? (
        <div className="border-t border-line-soft pt-4">
          <fieldset className="flex flex-col gap-2">
            <legend className={legend}>Merge as</legend>
            <Check
              type="radio"
              name="style"
              checked={delivery.mergeStyle !== 'merge_commit'}
              onChange={() => setDelivery({ ...delivery, mergeStyle: 'squash' })}
              label="One squashed commit (the agent's commits stay in the review history)"
            />
            <Check
              type="radio"
              name="style"
              checked={delivery.mergeStyle === 'merge_commit'}
              onChange={() => setDelivery({ ...delivery, mergeStyle: 'merge_commit' })}
              label="A merge commit that keeps the agent's commits"
            />
          </fieldset>
        </div>
      ) : (
        <Hint>
          A repository without a remote has nowhere to push to: its changes are merged in the hub.
        </Hint>
      )}
    </FormPanel>
  )
}

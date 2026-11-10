import { useState } from 'react'
import type { components } from '../api/schema'
import { MarkdownView } from '../tickets/MarkdownView'
import { Button } from '../ui/Button'
import { ErrorText, Field, Input, Textarea } from '../ui/form'
import { CheckIcon } from '../ui/icons'
import { Disclosure } from '../ui/layout'
import { filesByFolder, orderedOptions, planDetails, planHeadline, useAnswer } from './inbox'

type PlanPayload = components['schemas']['PlanPayload']
type QuestionPayload = components['schemas']['QuestionPayload']

/** An agent's plan: its summary first, steps, risks and files on demand; approved or sent back with notes. */
export function PlanAnswer({ id, plan }: { id: string; plan: PlanPayload }) {
  const answer = useAnswer(id)
  const [revising, setRevising] = useState(false)
  const [notes, setNotes] = useState('')

  return (
    <div className="flex flex-col gap-3">
      <p className="m-0 font-medium text-fg">{planHeadline(plan)}</p>
      <Disclosure summary={`Show plan (${planDetails(plan)})`}>
        <PlanDetails plan={plan} />
      </Disclosure>
      {revising ? (
        <form
          className="flex flex-col gap-3"
          onSubmit={(e) => {
            e.preventDefault()
            answer.mutate({ decision: 'revise', text: notes })
          }}
        >
          <Field label="What should change?">
            <Textarea
              rows={3}
              value={notes}
              onChange={(e) => setNotes(e.target.value)}
              autoFocus
              required
            />
          </Field>
          <div className="flex flex-wrap items-center gap-2">
            <Button
              type="submit"
              variant="primary"
              disabled={answer.isPending || notes.trim() === ''}
            >
              Send back for revision
            </Button>
            <Button variant="ghost" onClick={() => setRevising(false)}>
              Back
            </Button>
          </div>
        </form>
      ) : (
        <div className="flex flex-wrap items-center gap-2">
          <Button
            variant="primary"
            disabled={answer.isPending}
            onClick={() => answer.mutate({ decision: 'approve' })}
          >
            <CheckIcon size={14} strokeWidth={1.8} />
            Approve plan
          </Button>
          <Button disabled={answer.isPending} onClick={() => setRevising(true)}>
            Revise…
          </Button>
        </div>
      )}
      {answer.error && <ErrorText>{answer.error.message}</ErrorText>}
    </div>
  )
}

function PlanDetails({ plan }: { plan: PlanPayload }) {
  return (
    <div className="flex flex-col gap-3 rounded-md border border-line-soft bg-bg px-4 py-3">
      <ol className="m-0 list-decimal space-y-1 pl-5 marker:text-fg-subtle">
        {plan.steps.map((step, i) => (
          <li key={i} className="pl-1">
            {step}
          </li>
        ))}
      </ol>
      {plan.risks.length > 0 && (
        <div className="border-t border-line-soft pt-3">
          <h3 className="m-0 mb-1 text-xs font-semibold text-fg-muted">Risks</h3>
          <ul className="m-0 list-disc space-y-1 pl-5 marker:text-fg-faint">
            {plan.risks.map((risk, i) => (
              <li key={i}>{risk}</li>
            ))}
          </ul>
        </div>
      )}
      {plan.filesExpected.length > 0 && (
        <div className="border-t border-line-soft pt-3">
          <h3 className="m-0 mb-1 text-xs font-semibold text-fg-muted">Files</h3>
          <ul className="m-0 list-none space-y-1 p-0 text-sm">
            {filesByFolder(plan.filesExpected).map(({ folder, names }) => (
              <li key={folder} className="[overflow-wrap:anywhere]">
                <span className="text-fg-muted">
                  {names.length} {names.length === 1 ? 'file' : 'files'} in{' '}
                </span>
                <span className="font-mono text-mono text-fg-2">{folder || 'the root'}</span>
                <span className="text-fg-subtle">: </span>
                <span className="font-mono text-mono text-fg-muted">{names.join(', ')}</span>
              </li>
            ))}
          </ul>
        </div>
      )}
    </div>
  )
}

/**
 * An agent's question: one button per option (the recommended one first), "Other…" for an answer in one's own
 * words, and the agent's reasons on demand. Picking an option answers with its text.
 */
export function QuestionAnswer({ id, question }: { id: string; question: QuestionPayload }) {
  const answer = useAnswer(id)
  const options = orderedOptions(question)
  const [writing, setWriting] = useState(options.length === 0)
  const [text, setText] = useState('')

  return (
    <div className="flex flex-col gap-3">
      <p className="m-0 font-medium text-fg">{question.question}</p>
      {question.context && (
        <Disclosure summary="Why it asks">
          <div className="rounded-md border border-line-soft bg-bg px-4 py-3">
            <MarkdownView text={question.context} />
          </div>
        </Disclosure>
      )}
      {options.length > 0 && (
        <div className="flex flex-wrap gap-2" role="group" aria-label="Options">
          {options.map((option) => (
            <Button
              key={option}
              disabled={answer.isPending}
              onClick={() => answer.mutate({ text: option })}
              className="h-auto min-h-8 py-1.5 text-left whitespace-normal"
            >
              {option}
              {option === question.recommended && (
                <>
                  {' '}
                  <span className="text-xs font-normal text-fg-subtle">recommended</span>
                </>
              )}
            </Button>
          ))}
          {!writing && (
            <Button variant="ghost" disabled={answer.isPending} onClick={() => setWriting(true)}>
              Other…
            </Button>
          )}
        </div>
      )}
      {writing && (
        <form
          className="flex flex-wrap gap-2"
          onSubmit={(e) => {
            e.preventDefault()
            answer.mutate({ text })
          }}
        >
          <Input
            className="min-w-48 flex-1"
            aria-label="Answer"
            placeholder={options.length > 0 ? 'Your own answer' : 'Answer'}
            value={text}
            onChange={(e) => setText(e.target.value)}
            autoFocus={options.length > 0}
          />
          <Button
            type="submit"
            variant={options.length > 0 ? 'secondary' : 'primary'}
            disabled={answer.isPending || text.trim() === ''}
          >
            Send answer
          </Button>
        </form>
      )}
      {answer.error && <ErrorText>{answer.error.message}</ErrorText>}
    </div>
  )
}

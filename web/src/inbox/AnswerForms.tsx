import { useState } from 'react'
import type { components } from '../api/schema'
import { MarkdownView } from '../tickets/MarkdownView'
import { Button } from '../ui/Button'
import { ErrorText, Field, Input, Textarea } from '../ui/form'
import { CheckIcon } from '../ui/icons'
import { useAnswer } from './inbox'

type PlanPayload = components['schemas']['PlanPayload']
type QuestionPayload = components['schemas']['QuestionPayload']

/** An agent's plan: steps, risks and files, approved or sent back with notes. */
export function PlanAnswer({ id, plan }: { id: string; plan: PlanPayload }) {
  const answer = useAnswer(id)
  const [revising, setRevising] = useState(false)
  const [notes, setNotes] = useState('')

  return (
    <div className="flex flex-col gap-3">
      <div className="rounded-md border border-line-soft bg-bg px-4 py-3">
        <ol className="m-0 list-decimal space-y-1 pl-5 marker:text-fg-subtle">
          {plan.steps.map((step, i) => (
            <li key={i} className="pl-1">
              {step}
            </li>
          ))}
        </ol>
        {plan.risks.length > 0 && (
          <div className="mt-3 border-t border-line-soft pt-3">
            <h3 className="m-0 mb-1 text-xs font-semibold text-fg-muted">Risks</h3>
            <ul className="m-0 list-disc space-y-1 pl-5 marker:text-fg-faint">
              {plan.risks.map((risk, i) => (
                <li key={i}>{risk}</li>
              ))}
            </ul>
          </div>
        )}
        {plan.filesExpected.length > 0 && (
          <p className="mt-3 mb-0 text-sm text-fg-muted">
            Files:{' '}
            <span className="font-mono text-mono text-fg-2">{plan.filesExpected.join(', ')}</span>
          </p>
        )}
      </div>
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

/** An agent's question with its context; picking an option answers with its text. */
export function QuestionAnswer({ id, question }: { id: string; question: QuestionPayload }) {
  const answer = useAnswer(id)
  const [text, setText] = useState('')

  return (
    <div className="flex flex-col gap-3">
      <p className="m-0 font-medium text-fg">{question.question}</p>
      {question.context && (
        <div className="rounded-md border border-line-soft bg-bg px-4 py-3">
          <MarkdownView text={question.context} />
        </div>
      )}
      {question.options.length > 0 && (
        <div className="flex flex-wrap gap-2" role="group" aria-label="Options">
          {question.options.map((option) => (
            <Button
              key={option}
              disabled={answer.isPending}
              onClick={() => answer.mutate({ text: option })}
              className="h-auto min-h-8 py-1.5 text-left whitespace-normal"
            >
              {option}
            </Button>
          ))}
        </div>
      )}
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
          placeholder={question.options.length > 0 ? 'Or answer in your own words' : 'Answer'}
          value={text}
          onChange={(e) => setText(e.target.value)}
        />
        <Button
          type="submit"
          variant={question.options.length > 0 ? 'secondary' : 'primary'}
          disabled={answer.isPending || text.trim() === ''}
        >
          Send answer
        </Button>
      </form>
      {answer.error && <ErrorText>{answer.error.message}</ErrorText>}
    </div>
  )
}

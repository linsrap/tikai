import { useState } from 'react'
import type { components } from '../api/schema'
import { MarkdownView } from '../tickets/MarkdownView'
import { useAnswer } from './inbox'

type PlanPayload = components['schemas']['PlanPayload']
type QuestionPayload = components['schemas']['QuestionPayload']

/** An agent's plan: steps, risks and files, approved or sent back with notes. */
export function PlanAnswer({ id, plan }: { id: string; plan: PlanPayload }) {
  const answer = useAnswer(id)
  const [revising, setRevising] = useState(false)
  const [notes, setNotes] = useState('')

  return (
    <div className="stack">
      <ol className="list">
        {plan.steps.map((step, i) => (
          <li key={i}>{step}</li>
        ))}
      </ol>
      {plan.risks.length > 0 && (
        <div>
          <strong>Risks</strong>
          <ul className="list">
            {plan.risks.map((risk, i) => (
              <li key={i}>{risk}</li>
            ))}
          </ul>
        </div>
      )}
      {plan.filesExpected.length > 0 && (
        <p className="muted">
          Files: <span className="mono">{plan.filesExpected.join(', ')}</span>
        </p>
      )}
      {revising ? (
        <form
          className="stack"
          onSubmit={(e) => {
            e.preventDefault()
            answer.mutate({ decision: 'revise', text: notes })
          }}
        >
          <label className="field">
            <span>What should change?</span>
            <textarea
              rows={3}
              value={notes}
              onChange={(e) => setNotes(e.target.value)}
              autoFocus
              required
            />
          </label>
          <div className="row">
            <button type="submit" disabled={answer.isPending || notes.trim() === ''}>
              Send back for revision
            </button>
            <button type="button" onClick={() => setRevising(false)}>
              Back
            </button>
          </div>
        </form>
      ) : (
        <div className="row">
          <button
            type="button"
            className="primary"
            disabled={answer.isPending}
            onClick={() => answer.mutate({ decision: 'approve' })}
          >
            Approve plan
          </button>
          <button type="button" disabled={answer.isPending} onClick={() => setRevising(true)}>
            Revise…
          </button>
        </div>
      )}
      {answer.error && <p className="error">{answer.error.message}</p>}
    </div>
  )
}

/** An agent's question with its context; picking an option answers with its text. */
export function QuestionAnswer({ id, question }: { id: string; question: QuestionPayload }) {
  const answer = useAnswer(id)
  const [text, setText] = useState('')

  return (
    <div className="stack">
      <p>
        <strong>{question.question}</strong>
      </p>
      {question.context && <MarkdownView text={question.context} />}
      {question.options.length > 0 && (
        <div className="row" role="group" aria-label="Options">
          {question.options.map((option) => (
            <button
              key={option}
              type="button"
              disabled={answer.isPending}
              onClick={() => answer.mutate({ text: option })}
            >
              {option}
            </button>
          ))}
        </div>
      )}
      <form
        className="row"
        onSubmit={(e) => {
          e.preventDefault()
          answer.mutate({ text })
        }}
      >
        <input
          className="grow"
          aria-label="Answer"
          placeholder={question.options.length > 0 ? 'Or answer in your own words' : 'Answer'}
          value={text}
          onChange={(e) => setText(e.target.value)}
        />
        <button type="submit" disabled={answer.isPending || text.trim() === ''}>
          Send answer
        </button>
      </form>
      {answer.error && <p className="error">{answer.error.message}</p>}
    </div>
  )
}

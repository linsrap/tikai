import { Link } from '@tanstack/react-router'
import { useState } from 'react'
import type { components } from '../api/schema'
import { RunLog } from '../live/RunLog'
import { SelfReport } from '../review/SelfReport'
import { useRebase } from '../review/review'
import { analysisLine, roundLine, type RoundBrief } from './inbox'
import { eventSummary, runStateLabel } from '../runs/runs'
import { PlanAnswer, QuestionAnswer } from './AnswerForms'
import { ago, kindLabel, useDismiss, useRetry, type AttentionItem } from './inbox'

type FailurePayload = components['schemas']['FailurePayload']
type ReviewPayload = components['schemas']['ReviewPayload']
type ConflictPayload = components['schemas']['ConflictPayload']

/** One inbox item with what is needed to act on it: ticket, agent, step, the request itself, and recent activity. */
export function AttentionCard({ item }: { item: AttentionItem }) {
  const [showLog, setShowLog] = useState(false)
  const run = item.run

  return (
    <article
      className={`card stack attention ${item.kind}${item.blocking ? ' blocking' : ''}`}
      aria-label={`${kindLabel[item.kind]}: ${item.ticketKey}`}
    >
      <header className="row spread">
        <span className="row">
          <span className={`badge ${item.blocking ? 'blocked' : item.kind}`}>
            {kindLabel[item.kind]}
          </span>
          <Link
            to="/projects/$slug/tickets/$number"
            params={{ slug: item.projectSlug, number: String(item.ticketNumber) }}
          >
            <strong>{item.ticketKey}</strong> {item.ticketTitle}
          </Link>
        </span>
        <span className="muted">{ago(item.createdAt)}</span>
      </header>
      {run && (
        <p className="muted">
          Claude Code · {run.model} · {runStateLabel[run.state]}
          {run.attempt > 1 && ` · attempt ${run.attempt}`}
          {run.progressStep && (
            <>
              {' · '}
              {run.progressStep}
              {run.progressNote && ` (${run.progressNote})`}
            </>
          )}
        </p>
      )}

      {item.plan && <PlanAnswer id={item.id} plan={item.plan} />}
      {item.question && <QuestionAnswer id={item.id} question={item.question} />}
      {item.failure && <FailureActions id={item.id} failure={item.failure} />}
      {item.conflict && item.changeSetId && (
        <ConflictActions id={item.id} changeSetId={item.changeSetId} conflict={item.conflict} />
      )}
      {item.review && (
        <ReviewReport
          id={item.id}
          review={item.review}
          coverage={item.reviewCoverage}
          changeSetId={item.changeSetId}
          analysis={item.analysis}
          round={item.round ?? null}
          deliveryError={item.deliveryError ?? null}
        />
      )}

      {item.excerpt.length > 0 && (
        <div className="excerpt">
          <p className="muted">{item.kind === 'failure' ? 'How it ended' : 'Right before'}</p>
          <ol className="list mono">
            {item.excerpt.map((e) => (
              <li key={e.seq} className={`event ${e.kind}`}>
                {eventSummary(e)}
              </li>
            ))}
          </ol>
        </div>
      )}
      {item.runId && (
        <div>
          <button type="button" aria-pressed={showLog} onClick={() => setShowLog(!showLog)}>
            {showLog ? 'Hide log' : 'Show log'}
          </button>
          {showLog && <RunLog runId={item.runId} />}
        </div>
      )}
    </article>
  )
}

function FailureActions({ id, failure }: { id: string; failure: FailurePayload }) {
  const retry = useRetry(id)
  const dismiss = useDismiss(id)
  const busy = retry.isPending || dismiss.isPending
  const error = retry.error ?? dismiss.error
  return (
    <div className="stack">
      <p className="error">
        {failure.reason}
        {failure.exitCode != null && ` (exit code ${failure.exitCode})`}
      </p>
      <div className="row">
        <button type="button" className="primary" disabled={busy} onClick={() => retry.mutate()}>
          Retry
        </button>
        <button type="button" disabled={busy} onClick={() => dismiss.mutate()}>
          Dismiss
        </button>
      </div>
      <p className="muted">Retry starts a new run with the same repository and provider.</p>
      {error && <p className="error">{error.message}</p>}
    </div>
  )
}

/** An approved round that does not merge: the files, and a rebase by the agent (or a look at the review). */
function ConflictActions({
  id,
  changeSetId,
  conflict,
}: {
  id: string
  changeSetId: string
  conflict: ConflictPayload
}) {
  const rebase = useRebase(changeSetId)
  const dismiss = useDismiss(id)
  const busy = rebase.isPending || dismiss.isPending
  const error = rebase.error ?? dismiss.error
  return (
    <div className="stack">
      <p>
        Round {conflict.roundNo} was approved, but {conflict.defaultBranch} moved since the agent
        started, and {conflict.files.length === 1 ? 'this file conflicts' : 'these files conflict'}:
      </p>
      <ul className="paths">
        {conflict.files.map((file) => (
          <li key={file}>
            <code>{file}</code>
          </li>
        ))}
      </ul>
      <div className="row">
        <button type="button" className="primary" disabled={busy} onClick={() => rebase.mutate()}>
          Ask the agent to rebase
        </button>
        <Link to="/reviews/$id" params={{ id: changeSetId }} className="button">
          Open review
        </Link>
        <button type="button" disabled={busy} onClick={() => dismiss.mutate()}>
          Dismiss
        </button>
      </div>
      <p className="muted">
        The agent gets the current {conflict.defaultBranch}, resolves the conflicts and submits a
        new round. Dismissed, the change waits on the review screen.
      </p>
      {error && <p className="error">{error.message}</p>}
    </div>
  )
}

/**
 * The agent's self-report, and the way to the review screen. A review from before change sets existed has no
 * review screen and can only be dismissed.
 */
function ReviewReport({
  id,
  review,
  coverage,
  changeSetId,
  analysis,
  round,
  deliveryError,
}: {
  id: string
  review: ReviewPayload
  coverage: AttentionItem['reviewCoverage']
  changeSetId: string | null
  analysis: AttentionItem['analysis']
  round: RoundBrief | null
  deliveryError: string | null
}) {
  const dismiss = useDismiss(id)
  const roundText = roundLine(round)
  return (
    <div className="stack">
      {deliveryError && (
        <p className="error">Approved, but it did not get through: {deliveryError}</p>
      )}
      <SelfReport summary={review.summary} coverage={coverage} openConcerns={review.openConcerns} />
      {roundText && <p className="muted round-line">{roundText}</p>}
      {analysis && (
        <p className="muted analysis-line" role="status">
          {analysisLine(analysis)}
        </p>
      )}
      {changeSetId ? (
        <div className="row">
          <Link to="/reviews/$id" params={{ id: changeSetId }} className="button primary">
            Open review
          </Link>
        </div>
      ) : (
        <div className="row">
          <button type="button" disabled={dismiss.isPending} onClick={() => dismiss.mutate()}>
            Dismiss
          </button>
          <span className="muted">
            Submitted before the review screen existed; the work is on the run&apos;s branch.
          </span>
        </div>
      )}
      {dismiss.error && <p className="error">{dismiss.error.message}</p>}
    </div>
  )
}

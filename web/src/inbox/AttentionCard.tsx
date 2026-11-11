import { Link } from '@tanstack/react-router'
import { useState } from 'react'
import type { components } from '../api/schema'
import { RunLog } from '../live/RunLog'
import { SelfReport } from '../review/SelfReport'
import { useRebase } from '../review/review'
import { eventSummary, runStateLabel } from '../runs/runs'
import { Button } from '../ui/Button'
import { ErrorText, Hint } from '../ui/form'
import { RetryIcon, TerminalIcon } from '../ui/icons'
import { Disclosure } from '../ui/layout'
import { Tag, type Tone } from '../ui/Tag'
import { PlanAnswer, QuestionAnswer } from './AnswerForms'
import {
  ago,
  analysisLine,
  kindLabel,
  roundLine,
  useDismiss,
  useRetry,
  type AttentionItem,
  type RoundBrief,
} from './inbox'

type FailurePayload = components['schemas']['FailurePayload']
type ReviewPayload = components['schemas']['ReviewPayload']
type ConflictPayload = components['schemas']['ConflictPayload']

function kindTone(item: AttentionItem): Tone {
  if (item.blocking) return 'wait'
  if (item.kind === 'failure') return 'bad'
  if (item.kind === 'conflict') return 'wait'
  if (item.kind === 'review') return 'run'
  return 'neutral'
}

/**
 * One inbox item with what is needed to act on it: ticket, agent, step, the request itself, and recent activity.
 * Items sit in a list on one panel, so the item itself has no border.
 */
export function AttentionCard({ item }: { item: AttentionItem }) {
  const [showLog, setShowLog] = useState(false)
  const run = item.run

  return (
    <article
      className="flex flex-col gap-3 border-t border-line-soft px-5 py-4 first:border-t-0"
      aria-label={`${kindLabel[item.kind]}: ${item.ticketKey}`}
    >
      <header className="flex flex-col gap-1">
        <div className="flex flex-wrap items-center gap-x-2.5 gap-y-1">
          <Tag tone={kindTone(item)}>{kindLabel[item.kind]}</Tag>
          <Link
            to="/projects/$slug/tickets/$number"
            params={{ slug: item.projectSlug, number: String(item.ticketNumber) }}
            className="flex min-w-0 items-baseline gap-2 text-fg hover:no-underline [&:hover_.title]:underline"
          >
            <span className="font-mono text-mono text-fg-muted">{item.ticketKey}</span>
            <span className="title font-semibold">{item.ticketTitle}</span>
          </Link>
          <span className="ml-auto text-sm text-fg-subtle tabular-nums">{ago(item.createdAt)}</span>
        </div>
        {run && (
          <p className="m-0 flex flex-wrap gap-x-1.5 text-sm text-fg-muted">
            <span>Claude Code</span>
            <Sep />
            <span>{run.model}</span>
            <Sep />
            <span>{runStateLabel[run.state]}</span>
            {run.attempt > 1 && (
              <>
                <Sep />
                <span>attempt {run.attempt}</span>
              </>
            )}
            {run.progressStep && (
              <>
                <Sep />
                <span>
                  {run.progressStep}
                  {run.progressNote && ` (${run.progressNote})`}
                </span>
              </>
            )}
          </p>
        )}
      </header>

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
        <div>
          <h3 className="m-0 mb-1 text-xs font-medium text-fg-subtle">
            {item.kind === 'failure' ? 'How it ended' : 'Right before'}
          </h3>
          <ol className="m-0 list-none border-l-2 border-line py-0.5 pl-3 font-mono text-xs leading-5 text-fg-muted">
            {item.excerpt.map((e) => (
              <li key={e.seq} className="truncate" title={eventSummary(e)}>
                {eventSummary(e)}
              </li>
            ))}
          </ol>
        </div>
      )}
      {item.runId && (
        <div className="flex flex-col gap-2">
          <div>
            <Button
              variant="ghost"
              size="sm"
              aria-pressed={showLog}
              onClick={() => setShowLog(!showLog)}
            >
              <TerminalIcon size={14} />
              {showLog ? 'Hide log' : 'Show log'}
            </Button>
          </div>
          {showLog && <RunLog runId={item.runId} />}
        </div>
      )}
    </article>
  )
}

function Sep() {
  return (
    <span aria-hidden="true" className="text-fg-faint">
      ·
    </span>
  )
}

function FailureActions({ id, failure }: { id: string; failure: FailurePayload }) {
  const retry = useRetry(id)
  const dismiss = useDismiss(id)
  const busy = retry.isPending || dismiss.isPending
  const error = retry.error ?? dismiss.error
  return (
    <div className="flex flex-col gap-2">
      <p className="m-0 text-bad">
        {failure.reason}
        {failure.exitCode != null && ` (exit code ${failure.exitCode})`}
      </p>
      <div className="flex flex-wrap items-center gap-2">
        <Button disabled={busy} onClick={() => retry.mutate()}>
          <RetryIcon size={14} />
          Retry
        </Button>
        <Button variant="ghost" disabled={busy} onClick={() => dismiss.mutate()}>
          Dismiss
        </Button>
        <Hint>Retry starts a new run with the same repository and provider.</Hint>
      </div>
      {error && <ErrorText>{error.message}</ErrorText>}
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
    <div className="flex flex-col gap-3">
      <p className="m-0">
        Round {conflict.roundNo} was approved, but{' '}
        <code className="font-mono text-mono">{conflict.defaultBranch}</code> moved since the agent
        started, and {conflict.files.length === 1 ? 'this file conflicts' : 'these files conflict'}:
      </p>
      <ul className="m-0 list-none space-y-0.5 rounded-md border border-line-soft bg-bg px-4 py-2 font-mono text-mono">
        {conflict.files.map((file) => (
          <li key={file}>{file}</li>
        ))}
      </ul>
      <div className="flex flex-wrap items-center gap-2">
        <Button variant="primary" disabled={busy} onClick={() => rebase.mutate()}>
          Ask the agent to rebase
        </Button>
        <Button asChild>
          <Link to="/reviews/$id" params={{ id: changeSetId }}>
            Open review
          </Link>
        </Button>
        <Button variant="ghost" disabled={busy} onClick={() => dismiss.mutate()}>
          Dismiss
        </Button>
      </div>
      <Hint>
        The agent gets the current {conflict.defaultBranch}, resolves the conflicts and submits a
        new round. Dismissed, the change waits on the review screen.
      </Hint>
      {error && <ErrorText>{error.message}</ErrorText>}
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
  // With a done analysis, its headline and places lead; the agent's report is one click away.
  const done = analysis?.status === 'done'
  return (
    <div className="flex flex-col gap-3">
      {deliveryError && (
        <p className="m-0 rounded-md bg-bad-bg px-3 py-2 text-sm text-bad">
          Approved, but it did not get through: {deliveryError}
        </p>
      )}
      {done ? (
        <div className="flex flex-col gap-2">
          {analysis.headline && (
            <p className="m-0 font-medium [overflow-wrap:anywhere]">{analysis.headline}</p>
          )}
          {(analysis.lookAt ?? []).length > 0 && (
            <ul className="m-0 flex list-none flex-col gap-0.5 p-0 text-sm" aria-label="Look at">
              {(analysis.lookAt ?? []).map((place) => (
                <li key={`${place.path}:${place.line}`}>
                  <span className="font-mono text-mono text-fg-muted">
                    {place.path}:{place.line}
                  </span>{' '}
                  <span className="text-fg-2">{place.reason}</span>
                </li>
              ))}
            </ul>
          )}
          <Disclosure summary="The agent’s report">
            <SelfReport
              summary={review.summary}
              coverage={coverage}
              openConcerns={review.openConcerns}
            />
          </Disclosure>
        </div>
      ) : (
        <SelfReport
          summary={review.summary}
          coverage={coverage}
          openConcerns={review.openConcerns}
          collapsed
        />
      )}
      {(roundText || analysis) && (
        <p className="m-0 flex flex-wrap gap-x-1.5 text-sm text-fg-muted">
          {roundText && <span className="round-line">{roundText}</span>}
          {roundText && analysis && <Sep />}
          {analysis && (
            <span className="analysis-line" role="status">
              {analysisLine(analysis)}
            </span>
          )}
        </p>
      )}
      {changeSetId ? (
        <div>
          <Button variant="primary" asChild>
            <Link to="/reviews/$id" params={{ id: changeSetId }}>
              Open review
            </Link>
          </Button>
        </div>
      ) : (
        <div className="flex flex-wrap items-center gap-2">
          <Button variant="ghost" disabled={dismiss.isPending} onClick={() => dismiss.mutate()}>
            Dismiss
          </Button>
          <Hint>
            Submitted before the review screen existed; the work is on the run&apos;s branch.
          </Hint>
        </div>
      )}
      {dismiss.error && <ErrorText>{dismiss.error.message}</ErrorText>}
    </div>
  )
}

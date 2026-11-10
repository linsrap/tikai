import { Button } from '../ui/Button'
import { cx } from '../ui/cx'
import { ErrorText } from '../ui/form'
import { CheckIcon } from '../ui/icons'
import { Disclosure } from '../ui/layout'
import { Tag, type Tone } from '../ui/Tag'
import { CoverageTag, SelfReport, Uncommitted } from './SelfReport'
import {
  coverageRows,
  refText,
  useRetryAnalysis,
  verdictCounts,
  verdictLine,
  type ChangeSet,
  type CodeRef,
  type CoverageRow,
  type ReviewRound,
} from './review'

const tierTone: Record<string, Tone> = { light: 'neutral', normal: 'run', strict: 'bad' }

const panel = 'rounded-lg border border-line bg-surface'

/**
 * The LLM's analysis of a round merged with the agent's report, shown before the diff: headline, verdict line, the
 * places to look at, the criteria that are not covered (covered ones in one line), and the agent's claim only where
 * the analysis disagrees; summary and the agent's report under "Details". While it runs it says so; when it failed
 * the review goes on without it (the agent's report is shown on its own), and it can be started again.
 */
export function AnalysisPanel({
  changeSet,
  round,
  onReveal,
}: {
  changeSet: ChangeSet
  round: ReviewRound
  onReveal: (ref: CodeRef) => void
}) {
  const retry = useRetryAnalysis(changeSet.id, round.roundNo)
  const analysis = round.analysis
  if (!analysis) return null

  const retryButton = (label: string) => (
    <Button size="sm" variant="ghost" disabled={retry.isPending} onClick={() => retry.mutate()}>
      {label}
    </Button>
  )

  if (analysis.status === 'pending') {
    return (
      <section className={cx(panel, 'px-4 py-3')} aria-label="Analysis" aria-busy="true">
        <p role="status" className="m-0 flex items-center gap-2 text-sm text-fg-muted">
          <span className="size-2 animate-pulse rounded-full bg-run-solid" aria-hidden="true" />
          Analysing this round… The coverage and the reading order appear here when it is done; you
          can start reading already.
        </p>
      </section>
    )
  }
  if (analysis.status === 'failed' || !analysis.result) {
    return (
      <section className={cx(panel, 'flex flex-col gap-2 px-4 py-3')} aria-label="Analysis">
        <p role="status" className="m-0">
          <strong className="font-semibold">Analysis unavailable.</strong>{' '}
          <span className="text-fg-muted">{analysis.error ?? 'The analysis did not finish.'}</span>
        </p>
        <div className="flex flex-wrap items-center gap-3">
          <span className="text-sm text-fg-subtle">
            The review works without it: files are in path order.
          </span>
          {retryButton(retry.isPending ? 'Starting…' : 'Retry analysis')}
        </div>
        {retry.error && <ErrorText>{retry.error.message}</ErrorText>}
      </section>
    )
  }

  const result = analysis.result
  const rows = coverageRows(changeSet.criteria, round.coverage, result)
  const attention = rows.filter((row) => row.found?.status !== 'covered')
  const covered = rows.filter((row) => row.found?.status === 'covered')
  const lookAt = result.lookAt ?? []
  // A v1 analysis has no headline: its summary takes the place, and the details do not repeat it.
  const headline = result.headline ?? result.summary
  const summary = result.headline ? result.summary : ''
  const tokens =
    result.inputTokens != null && result.outputTokens != null
      ? ` · ${result.inputTokens.toLocaleString()} in, ${result.outputTokens.toLocaleString()} out`
      : ''

  return (
    <section className={panel} aria-label="Analysis">
      <div className="flex flex-wrap items-center gap-2.5 border-b border-line-soft px-4 py-2.5">
        <h2 className="m-0 text-sm font-semibold">Analysis</h2>
        <Tag tone={tierTone[result.tier] ?? 'neutral'} title={result.tierReason}>
          {result.tier}
        </Tag>
        <span className="ml-auto text-xs text-fg-subtle">
          Written by an AI: check it against the code.
        </span>
      </div>
      <div className="flex flex-col gap-1 px-4 py-3">
        {headline && (
          <p className="m-0 font-medium [overflow-wrap:anywhere] whitespace-pre-wrap">{headline}</p>
        )}
        <p className="m-0 text-sm text-fg-muted" aria-label="Analysis result">
          {verdictLine(verdictCounts(changeSet.criteria, result))}
        </p>
      </div>
      {lookAt.length > 0 && (
        <div className="border-t border-line-soft px-4 py-3">
          <h3 className="m-0 mb-1.5 text-xs font-semibold text-fg-muted">Look at</h3>
          <ul className="m-0 flex list-none flex-col gap-1 p-0">
            {lookAt.map((place) => {
              const ref = { path: place.path, lineStart: place.line, lineEnd: place.line }
              return (
                <li key={refText(ref)} className="flex flex-wrap items-baseline gap-x-2">
                  <RefButton refTo={ref} onReveal={onReveal} />
                  <span className="text-fg-2">{place.reason}</span>
                </li>
              )
            })}
          </ul>
        </div>
      )}
      {rows.length > 0 && (
        <div className="flex flex-col gap-2.5 border-t border-line-soft px-4 py-3">
          {attention.length > 0 && (
            <ul className="m-0 flex list-none flex-col gap-2.5 p-0" aria-label="Criteria to check">
              {attention.map((row) => (
                <CriterionRow key={row.criterionId} row={row} onReveal={onReveal} />
              ))}
            </ul>
          )}
          {covered.length > 0 && (
            <Disclosure
              summary={
                <span className="inline-flex items-center gap-1.5">
                  <CheckIcon size={13} strokeWidth={2} className="text-ok" />
                  {covered.length} covered
                  {covered.some((row) => row.disagree) && (
                    <span className="font-semibold text-wait">· the agent says otherwise</span>
                  )}
                </span>
              }
            >
              <ul className="m-0 flex list-none flex-col gap-2.5 p-0" aria-label="Covered criteria">
                {covered.map((row) => (
                  <CriterionRow key={row.criterionId} row={row} onReveal={onReveal} />
                ))}
              </ul>
            </Disclosure>
          )}
        </div>
      )}
      {round.uncommitted.length > 0 && (
        <div className="border-t border-line-soft px-4 py-3">
          <Uncommitted files={round.uncommitted} />
        </div>
      )}
      <div className="border-t border-line-soft px-4 py-3">
        <Disclosure summary="Details">
          <div className="flex flex-col gap-4">
            {summary && (
              <p className="m-0 [overflow-wrap:anywhere] whitespace-pre-wrap text-fg-2">
                {summary}
              </p>
            )}
            {result.patchesOmitted.length > 0 && (
              <p className="m-0 text-sm text-fg-muted">
                Not read by the analysis ({result.patchesOmitted.length}):{' '}
                <span className="font-mono text-mono">
                  {result.patchesOmitted.slice(0, 8).join(', ')}
                </span>
                {result.patchesOmitted.length > 8 && ', …'}
              </p>
            )}
            <div>
              <h3 className="m-0 mb-1.5 text-xs font-semibold text-fg-muted">The agent’s report</h3>
              {/* The agent's claims per criterion are shown above where they differ. */}
              <SelfReport
                summary={round.selfReport.summary}
                coverage={[]}
                openConcerns={round.selfReport.openConcerns}
              />
            </div>
          </div>
        </Disclosure>
      </div>
      <div className="flex flex-wrap items-center gap-3 rounded-b-lg border-t border-line-soft bg-bg px-4 py-2 text-xs text-fg-subtle">
        <span className="min-w-0 flex-1">
          {result.tierReason} · {result.model}
          {tokens} · {result.promptVersion}
        </span>
        {retryButton(retry.isPending ? 'Starting…' : 'Analyse again')}
      </div>
      {retry.error && <ErrorText className="px-4 pb-3">{retry.error.message}</ErrorText>}
    </section>
  )
}

/** One criterion: what the analysis found, its refs, and the agent's claim only where it differs. */
function CriterionRow({ row, onReveal }: { row: CoverageRow; onReveal: (ref: CodeRef) => void }) {
  return (
    <li className="flex flex-col gap-1" data-criterion={row.criterionId}>
      <span className="flex flex-wrap items-baseline gap-x-2 gap-y-1">
        {row.found ? (
          <CoverageTag status={row.found.status} />
        ) : (
          <Tag tone="neutral">not assessed</Tag>
        )}
        <span>{row.text}</span>
      </span>
      {(row.found?.note || row.disagree) && (
        <span className="flex flex-wrap items-baseline gap-x-2 text-sm text-fg-muted">
          {row.found?.note}
          {row.disagree && row.claimed && (
            <strong
              className="text-xs font-semibold text-wait"
              title={row.claimedNote ?? undefined}
            >
              the agent says {row.claimed}
            </strong>
          )}
        </span>
      )}
      {row.found && row.found.refs.length > 0 && (
        <span className="flex flex-wrap gap-x-3 gap-y-0.5">
          {row.found.refs.map((ref) => (
            <RefButton key={refText(ref)} refTo={ref} onReveal={onReveal} />
          ))}
        </span>
      )}
    </li>
  )
}

function RefButton({ refTo, onReveal }: { refTo: CodeRef; onReveal: (ref: CodeRef) => void }) {
  return (
    <button
      type="button"
      className="cursor-pointer font-mono text-xs text-link hover:underline"
      onClick={() => onReveal(refTo)}
    >
      {refText(refTo)}
    </button>
  )
}

import { Button } from '../ui/Button'
import { cx } from '../ui/cx'
import { ErrorText } from '../ui/form'
import { Tag, type Tone } from '../ui/Tag'
import { CoverageTag } from './SelfReport'
import {
  coverageRows,
  refText,
  useRetryAnalysis,
  type ChangeSet,
  type CodeRef,
  type ReviewRound,
} from './review'

const tierTone: Record<string, Tone> = { light: 'neutral', normal: 'run', strict: 'bad' }

const panel = 'rounded-lg border border-line bg-surface'

/**
 * The LLM's analysis of a round, shown before the diff: summary and coverage per criterion next to what the agent
 * claims. While it runs it says so; when it failed the review goes on without it, and it can be started again.
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
      {result.summary && (
        <p className="m-0 px-4 py-3 [overflow-wrap:anywhere] whitespace-pre-wrap text-fg-2">
          {result.summary}
        </p>
      )}
      {rows.length > 0 && (
        <div className="overflow-x-auto border-t border-line-soft">
          <table className="w-full min-w-[36rem] border-collapse text-left text-sm">
            <thead>
              <tr>
                <th scope="col" className="px-4 py-2 text-xs font-medium text-fg-subtle">
                  Criterion
                </th>
                <th scope="col" className="w-32 px-4 py-2 text-xs font-medium text-fg-subtle">
                  Agent
                </th>
                <th scope="col" className="px-4 py-2 text-xs font-medium text-fg-subtle">
                  Analysis
                </th>
              </tr>
            </thead>
            <tbody>
              {rows.map((row) => (
                <tr
                  key={row.criterionId}
                  className={cx(
                    'border-t border-line-soft align-top',
                    row.disagree && 'bg-wait-bg/50',
                  )}
                  data-criterion={row.criterionId}
                >
                  <td className="px-4 py-2.5">{row.text}</td>
                  <td className="px-4 py-2.5">
                    {row.claimed ? (
                      <span title={row.claimedNote ?? undefined}>
                        <CoverageTag status={row.claimed} />
                      </span>
                    ) : (
                      <span className="text-fg-subtle">not said</span>
                    )}
                  </td>
                  <td className="px-4 py-2.5">
                    {row.found ? (
                      <div className="flex flex-col gap-1.5">
                        <span className="flex flex-wrap items-baseline gap-x-2 gap-y-1">
                          <CoverageTag status={row.found.status} />
                          {row.disagree && (
                            <strong className="text-xs font-semibold text-wait">
                              differs from the agent
                            </strong>
                          )}
                          {row.found.note && (
                            <span className="text-fg-muted">– {row.found.note}</span>
                          )}
                        </span>
                        {row.found.refs.length > 0 && (
                          <span className="flex flex-wrap gap-x-3 gap-y-0.5">
                            {row.found.refs.map((ref) => (
                              <button
                                key={refText(ref)}
                                type="button"
                                className="cursor-pointer font-mono text-xs text-link hover:underline"
                                onClick={() => onReveal(ref)}
                              >
                                {refText(ref)}
                              </button>
                            ))}
                          </span>
                        )}
                      </div>
                    ) : (
                      <span className="text-fg-subtle">not assessed</span>
                    )}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
      {result.patchesOmitted.length > 0 && (
        <p className="m-0 border-t border-line-soft px-4 py-2.5 text-sm text-fg-muted">
          Not read by the analysis ({result.patchesOmitted.length}):{' '}
          <span className="font-mono text-mono">
            {result.patchesOmitted.slice(0, 8).join(', ')}
          </span>
          {result.patchesOmitted.length > 8 && ', …'}
        </p>
      )}
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

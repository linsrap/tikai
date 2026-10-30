import {
  coverageRows,
  refText,
  useRetryAnalysis,
  type ChangeSet,
  type CodeRef,
  type ReviewRound,
} from './review'

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
    <button type="button" disabled={retry.isPending} onClick={() => retry.mutate()}>
      {label}
    </button>
  )

  if (analysis.status === 'pending') {
    return (
      <section className="card analysis" aria-label="Analysis" aria-busy="true">
        <p role="status" className="muted">
          Analysing this round… The coverage and the reading order appear here when it is done; you
          can start reading already.
        </p>
      </section>
    )
  }
  if (analysis.status === 'failed' || !analysis.result) {
    return (
      <section className="card analysis stack" aria-label="Analysis">
        <p role="status">
          <strong>Analysis unavailable.</strong>{' '}
          <span className="muted">{analysis.error ?? 'The analysis did not finish.'}</span>
        </p>
        <p className="muted">The review works without it: files are in path order.</p>
        <div className="row">
          {retryButton(retry.isPending ? 'Starting…' : 'Retry analysis')}
          {retry.error && <span className="error">{retry.error.message}</span>}
        </div>
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
    <section className="card analysis stack" aria-label="Analysis">
      <h2 className="row spread">
        <span>Analysis</span>
        <span className={`badge tier ${result.tier}`} title={result.tierReason}>
          {result.tier}
        </span>
      </h2>
      {result.summary && <p className="pre-wrap">{result.summary}</p>}
      {rows.length > 0 && (
        <table className="coverage">
          <thead>
            <tr>
              <th scope="col">Criterion</th>
              <th scope="col">Agent</th>
              <th scope="col">Analysis</th>
            </tr>
          </thead>
          <tbody>
            {rows.map((row) => (
              <tr
                key={row.criterionId}
                className={row.disagree ? 'disagree' : undefined}
                data-criterion={row.criterionId}
              >
                <td>{row.text}</td>
                <td>
                  {row.claimed ? (
                    <span className={`badge ${row.claimed}`} title={row.claimedNote ?? undefined}>
                      {row.claimed}
                    </span>
                  ) : (
                    <span className="muted">not said</span>
                  )}
                </td>
                <td className="stack">
                  {row.found ? (
                    <>
                      <span>
                        <span className={`badge ${row.found.status}`}>{row.found.status}</span>
                        {row.disagree && (
                          <strong className="differs"> differs from the agent</strong>
                        )}
                        {row.found.note && <span className="muted"> – {row.found.note}</span>}
                      </span>
                      {row.found.refs.length > 0 && (
                        <span className="refs">
                          {row.found.refs.map((ref) => (
                            <button
                              key={refText(ref)}
                              type="button"
                              className="link mono"
                              onClick={() => onReveal(ref)}
                            >
                              {refText(ref)}
                            </button>
                          ))}
                        </span>
                      )}
                    </>
                  ) : (
                    <span className="muted">not assessed</span>
                  )}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
      {result.patchesOmitted.length > 0 && (
        <p className="muted">
          Not read by the analysis ({result.patchesOmitted.length}):{' '}
          <span className="mono">{result.patchesOmitted.slice(0, 8).join(', ')}</span>
          {result.patchesOmitted.length > 8 && ', …'}
        </p>
      )}
      <p className="muted small row spread">
        <span>
          {result.tierReason} · {result.model}
          {tokens} · {result.promptVersion}. Written by an AI: check it against the code.
        </span>
        {retryButton(retry.isPending ? 'Starting…' : 'Analyse again')}
      </p>
      {retry.error && <p className="error">{retry.error.message}</p>}
    </section>
  )
}

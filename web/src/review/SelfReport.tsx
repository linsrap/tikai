import type { ReviewCoverage } from '../inbox/inbox'
import { coverageSummary } from '../inbox/inbox'

/** The agent's self-report: summary, coverage per criterion (with the criterion's text now), open concerns. */
export function SelfReport({
  summary,
  coverage,
  openConcerns,
  uncommitted = [],
}: {
  summary: string
  coverage: readonly ReviewCoverage[]
  openConcerns: readonly string[]
  uncommitted?: readonly string[]
}) {
  return (
    <div className="stack self-report">
      <p className="pre-wrap">{summary}</p>
      {coverage.length > 0 && (
        <div>
          <strong>Criteria: {coverageSummary(coverage)}</strong>
          <ul className="list">
            {coverage.map((c) => (
              <li key={c.criterionId}>
                <span className={`badge ${c.status}`}>{c.status}</span>{' '}
                {c.text ?? <span className="muted">(criterion deleted)</span>}
                {c.note && <span className="muted"> – {c.note}</span>}
              </li>
            ))}
          </ul>
        </div>
      )}
      {openConcerns.length > 0 && (
        <div>
          <strong>Open concerns</strong>
          <ul className="list">
            {openConcerns.map((c, i) => (
              <li key={i}>{c}</li>
            ))}
          </ul>
        </div>
      )}
      {uncommitted.length > 0 && (
        <div className="banner" role="note">
          <strong>
            Left out: {uncommitted.length} uncommitted file{uncommitted.length === 1 ? '' : 's'}
          </strong>
          <p className="muted">
            The agent did not commit these, so they are not part of this review.
          </p>
          <ul className="list mono">
            {uncommitted.map((line) => (
              <li key={line}>{line}</li>
            ))}
          </ul>
        </div>
      )}
    </div>
  )
}

import type { ReviewCoverage } from '../inbox/inbox'
import { coverageSummary } from '../inbox/inbox'
import { Tag } from '../ui/Tag'
import { coverageTone } from './coverage'

type CoverageStatus = ReviewCoverage['status']

/** A coverage status as a tag; the text stays the status word ("covered"). */
export function CoverageTag({ status }: { status: CoverageStatus }) {
  return <Tag tone={coverageTone[status]}>{status}</Tag>
}

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
    <div className="flex flex-col gap-3">
      <p className="m-0 [overflow-wrap:anywhere] whitespace-pre-wrap text-fg-2">{summary}</p>
      {coverage.length > 0 && (
        <div>
          <h3 className="m-0 mb-1.5 text-xs font-semibold text-fg-muted">
            Criteria: {coverageSummary(coverage)}
          </h3>
          <ul className="m-0 flex list-none flex-col gap-1.5 p-0">
            {coverage.map((c) => (
              <li key={c.criterionId} className="flex items-baseline gap-2">
                <CoverageTag status={c.status} />
                <span>
                  {c.text ?? <span className="text-fg-subtle">(criterion deleted)</span>}
                  {c.note && <span className="text-fg-subtle"> – {c.note}</span>}
                </span>
              </li>
            ))}
          </ul>
        </div>
      )}
      {openConcerns.length > 0 && (
        <div>
          <h3 className="m-0 mb-1.5 text-xs font-semibold text-fg-muted">Open concerns</h3>
          <ul className="m-0 list-disc space-y-1 pl-5 marker:text-fg-faint">
            {openConcerns.map((c, i) => (
              <li key={i}>{c}</li>
            ))}
          </ul>
        </div>
      )}
      {uncommitted.length > 0 && (
        <div className="rounded-md border border-wait-bg bg-wait-bg px-4 py-3" role="note">
          <h3 className="m-0 text-sm font-semibold text-wait">
            Left out: {uncommitted.length} uncommitted file{uncommitted.length === 1 ? '' : 's'}
          </h3>
          <p className="mt-0.5 mb-2 text-sm text-fg-2">
            The agent did not commit these, so they are not part of this review.
          </p>
          <ul className="m-0 list-none space-y-0.5 p-0 font-mono text-mono text-fg-2">
            {uncommitted.map((line) => (
              <li key={line}>{line}</li>
            ))}
          </ul>
        </div>
      )}
    </div>
  )
}

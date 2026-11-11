import type { ReviewCoverage } from '../inbox/inbox'
import { coverageSummary, selfReportLine } from '../inbox/inbox'
import { Disclosure } from '../ui/layout'
import { Tag } from '../ui/Tag'
import { coverageTone } from './coverage'

type CoverageStatus = ReviewCoverage['status']

/** A coverage status as a tag; the text stays the status word ("covered"). */
export function CoverageTag({ status }: { status: CoverageStatus }) {
  return <Tag tone={coverageTone[status]}>{status}</Tag>
}

/**
 * The agent's self-report: summary, coverage per criterion (with the criterion's text now), open concerns.
 * `collapsed` (the inbox card) shows the summary and one line that opens the rest.
 */
export function SelfReport({
  summary,
  coverage,
  openConcerns,
  uncommitted = [],
  collapsed = false,
}: {
  summary: string
  coverage: readonly ReviewCoverage[]
  openConcerns: readonly string[]
  uncommitted?: readonly string[]
  collapsed?: boolean
}) {
  const line = selfReportLine(coverage, openConcerns)
  const details = (
    <ReportDetails coverage={coverage} openConcerns={openConcerns} collapsed={collapsed} />
  )
  return (
    <div className="flex flex-col gap-3">
      <p className="m-0 [overflow-wrap:anywhere] whitespace-pre-wrap text-fg-2">{summary}</p>
      {collapsed ? line && <Disclosure summary={line}>{details}</Disclosure> : details}
      <Uncommitted files={uncommitted} />
    </div>
  )
}

/** Files the agent left uncommitted: not part of the review, so always in sight. */
export function Uncommitted({ files }: { files: readonly string[] }) {
  if (files.length === 0) return null
  return (
    <div className="rounded-md border border-wait-bg bg-wait-bg px-4 py-3" role="note">
      <h3 className="m-0 text-sm font-semibold text-wait">
        Left out: {files.length} uncommitted file{files.length === 1 ? '' : 's'}
      </h3>
      <p className="mt-0.5 mb-2 text-sm text-fg-2">
        The agent did not commit these, so they are not part of this review.
      </p>
      <ul className="m-0 list-none space-y-0.5 p-0 font-mono text-mono text-fg-2">
        {files.map((line) => (
          <li key={line}>{line}</li>
        ))}
      </ul>
    </div>
  )
}

function ReportDetails({
  coverage,
  openConcerns,
  collapsed,
}: {
  coverage: readonly ReviewCoverage[]
  openConcerns: readonly string[]
  collapsed: boolean
}) {
  return (
    <div className="flex flex-col gap-3">
      {coverage.length > 0 && (
        <div>
          {!collapsed && (
            <h3 className="m-0 mb-1.5 text-xs font-semibold text-fg-muted">
              Criteria: {coverageSummary(coverage)}
            </h3>
          )}
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
    </div>
  )
}

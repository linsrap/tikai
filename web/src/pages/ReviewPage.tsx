import { useQuery } from '@tanstack/react-query'
import { Link } from '@tanstack/react-router'
import { lazy, Suspense, useEffect, useState } from 'react'
import { useWatch } from '../live/live'
import {
  changeSetQuery,
  fileOrder,
  groupFiles,
  kindLabel,
  kindLetter,
  lineCounts,
  loadDiffMode,
  nextUnviewed,
  reviewFileQuery,
  reviewFilesQuery,
  saveDiffMode,
  shortSha,
  sizeText,
  startsCollapsed,
  stepFile,
  useVerdict,
  useViewed,
  type ChangeSet,
  type DiffMode,
  type FileSide,
  type ReviewFile,
  type ReviewRound,
} from '../review/review'
import { SelfReport } from '../review/SelfReport'
import { runStateLabel } from '../runs/runs'
import { AppShell } from './AppShell'

// Monaco is large: it loads with the first diff, not with the app.
const DiffView = lazy(() => import('../review/DiffView'))

export function ReviewPage({ id }: { id: string }) {
  useWatch('WatchAll')
  const changeSet = useQuery(changeSetQuery(id))
  const [roundNo, setRoundNo] = useState<number | null>(null)
  const data = changeSet.data
  const round = data?.rounds.find((r) => r.roundNo === roundNo) ?? data?.rounds.at(-1)

  return (
    <AppShell wide>
      {changeSet.error && (
        <p role="alert" className="error">
          {changeSet.error.message}
        </p>
      )}
      {!data && !changeSet.error && <p>Loading…</p>}
      {data && round && (
        <article className="stack review">
          <Header changeSet={data} round={round} onRound={setRoundNo} />
          <details open className="card">
            <summary>
              <strong>The agent&apos;s report</strong>
            </summary>
            <SelfReport
              summary={round.selfReport.summary}
              coverage={round.coverage}
              openConcerns={round.selfReport.openConcerns}
              uncommitted={round.uncommitted}
            />
          </details>
          <VerdictBar changeSet={data} round={round} />
          <Files key={round.roundNo} id={id} round={round} />
        </article>
      )}
    </AppShell>
  )
}

function Header({
  changeSet,
  round,
  onRound,
}: {
  changeSet: ChangeSet
  round: ReviewRound
  onRound: (no: number) => void
}) {
  return (
    <header className="stack">
      <h1>
        <Link
          to="/projects/$slug/tickets/$number"
          params={{ slug: changeSet.projectSlug, number: String(changeSet.ticketNumber) }}
        >
          <span className="muted mono">{changeSet.ticketKey}</span>
        </Link>{' '}
        {changeSet.ticketTitle}
      </h1>
      <p className="row">
        <span className={`badge ${changeSet.runState}`}>{runStateLabel[changeSet.runState]}</span>
        <span className="mono">{changeSet.branch}</span>
        <span className="muted">
          Claude Code · {changeSet.model} · {changeSet.repositoryName} ·{' '}
          <span className="mono">
            {changeSet.defaultBranch} {shortSha(round.baseSha)}..{shortSha(round.headSha)}
          </span>
        </span>
        {changeSet.rounds.length > 1 && (
          <label className="row">
            <span className="muted">Round</span>
            <select value={round.roundNo} onChange={(e) => onRound(Number(e.target.value))}>
              {changeSet.rounds.map((r) => (
                <option key={r.roundNo} value={r.roundNo}>
                  {r.roundNo} ({new Date(r.createdAt).toLocaleString()})
                </option>
              ))}
            </select>
          </label>
        )}
      </p>
    </header>
  )
}

function VerdictBar({ changeSet, round }: { changeSet: ChangeSet; round: ReviewRound }) {
  const verdict = useVerdict(changeSet.id)
  const latest = round.roundNo === changeSet.rounds.at(-1)?.roundNo
  if (round.verdict) {
    return (
      <p className="card">
        Round {round.roundNo}:{' '}
        <strong>{round.verdict === 'reject' ? 'rejected' : round.verdict.replace('_', ' ')}</strong>
        {round.verdictAt && (
          <span className="muted"> on {new Date(round.verdictAt).toLocaleString()}</span>
        )}
      </p>
    )
  }
  if (changeSet.status !== 'open' || changeSet.runState !== 'in_review' || !latest) {
    return (
      <p className="card muted">
        {changeSet.status === 'closed'
          ? 'This change was closed: its run ended.'
          : 'No verdict on this round.'}
      </p>
    )
  }
  return (
    <div className="card row verdicts" aria-label="Verdict">
      <button type="button" disabled title="Merging comes in a later version">
        Approve
      </button>
      <button
        type="button"
        disabled
        title="Sending comments back to the agent comes in a later version"
      >
        Request changes
      </button>
      <button
        type="button"
        className="danger"
        disabled={verdict.isPending}
        onClick={() => {
          if (
            window.confirm(
              'Reject this change? The run is closed and the ticket goes back to open. The branch stays on the hub.',
            )
          ) {
            verdict.mutate('reject')
          }
        }}
      >
        Reject
      </button>
      <span className="muted">Approve and request changes come in later versions.</span>
      {verdict.error && <span className="error">{verdict.error.message}</span>}
    </div>
  )
}

/** Keys that are typing, not commands: inside fields and the editor. */
const isTyping = (target: EventTarget | null) =>
  target instanceof HTMLElement &&
  (target.isContentEditable ||
    ['INPUT', 'TEXTAREA', 'SELECT'].includes(target.tagName) ||
    !!target.closest('.monaco-editor'))

function Files({ id, round }: { id: string; round: ReviewRound }) {
  const files = useQuery(reviewFilesQuery(id, round.roundNo))
  const viewed = useViewed(id, round.roundNo)
  const [selected, setSelected] = useState<string | null>(null)
  const [mode, setMode] = useState<DiffMode>(() => loadDiffMode())
  const list = files.data?.files
  const current =
    list?.find((f) => f.path === selected) ?? list?.find((f) => f.path === fileOrder(list)[0])

  const toggleViewed = (file: ReviewFile) => {
    const marking = !file.viewed
    viewed.mutate({ path: file.path, viewed: marking })
    if (marking && list) {
      // On to the next file that still needs a look.
      setSelected(nextUnviewed(list, file.path, new Set([file.path])) ?? file.path)
    }
  }

  useEffect(() => {
    if (!list || !current) return
    const onKey = (e: KeyboardEvent) => {
      if (e.ctrlKey || e.metaKey || e.altKey || isTyping(e.target)) return
      if (e.key === 'j' || e.key === 'k') {
        setSelected(stepFile(fileOrder(list), current.path, e.key === 'j' ? 1 : -1))
      } else if (e.key === 'v') {
        toggleViewed(current)
      } else {
        return
      }
      e.preventDefault()
    }
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  })

  if (files.error) return <p className="error">{files.error.message}</p>
  if (!files.data || !list) return <p>Loading the files…</p>
  const viewedCount = list.filter((f) => f.viewed).length

  return (
    <div className="review-layout">
      <nav className="file-tree card" aria-label="Changed files">
        <p className="row spread">
          <strong>
            {viewedCount} of {list.length} files viewed
          </strong>
          <span className="muted" title="j / k: next or previous file, v: viewed">
            j k v
          </span>
        </p>
        {files.data.truncated && (
          <p className="muted">Only the first {list.length} files are listed.</p>
        )}
        <FileTree
          files={list}
          current={current?.path ?? null}
          onSelect={setSelected}
          onToggle={toggleViewed}
        />
        {viewed.error && <p className="error">{viewed.error.message}</p>}
      </nav>
      <section className="diff-pane" aria-label="Diff">
        {current ? (
          <FilePane
            key={current.path}
            id={id}
            round={round.roundNo}
            file={current}
            mode={mode}
            onMode={(m) => {
              setMode(m)
              saveDiffMode(m)
            }}
            onToggleViewed={() => toggleViewed(current)}
          />
        ) : (
          <p className="muted">No files changed.</p>
        )}
      </section>
    </div>
  )
}

function FileTree({
  files,
  current,
  onSelect,
  onToggle,
}: {
  files: readonly ReviewFile[]
  current: string | null
  onSelect: (path: string) => void
  onToggle: (file: ReviewFile) => void
}) {
  const { groups, skimmed } = groupFiles(files)
  const row = (file: ReviewFile, name: string) => (
    <li
      key={file.path}
      className={`file-row${file.viewed ? ' viewed' : ''}`}
      data-path={file.path}
      data-viewed={file.viewed}
    >
      <input
        type="checkbox"
        checked={file.viewed}
        aria-label={`Viewed ${file.path}`}
        onChange={() => onToggle(file)}
      />
      <button
        type="button"
        className="file-name"
        aria-current={file.path === current ? 'true' : undefined}
        title={`${kindLabel[file.kind]}: ${file.oldPath ? `${file.oldPath} → ` : ''}${file.path}`}
        onClick={() => onSelect(file.path)}
      >
        <span className={`kind ${file.kind}`}>{kindLetter[file.kind]}</span>{' '}
        <span className="mono">{name}</span>
      </button>
      <span className="muted counts">{lineCounts(file)}</span>
    </li>
  )
  return (
    <div className="stack tree">
      {groups.map((group) => (
        <div key={group.directory}>
          {group.directory && <p className="mono muted directory">{group.directory}/</p>}
          <ul className="files">
            {group.files.map((f) =>
              row(f, f.path.slice(group.directory ? group.directory.length + 1 : 0)),
            )}
          </ul>
        </div>
      ))}
      {skimmed.length > 0 && (
        <details>
          <summary className="muted">Generated and lock files ({skimmed.length})</summary>
          <ul className="files">{skimmed.map((f) => row(f, f.path))}</ul>
        </details>
      )}
    </div>
  )
}

function FilePane({
  id,
  round,
  file,
  mode,
  onMode,
  onToggleViewed,
}: {
  id: string
  round: number
  file: ReviewFile
  mode: DiffMode
  onMode: (mode: DiffMode) => void
  onToggleViewed: () => void
}) {
  const [expanded, setExpanded] = useState(!startsCollapsed(file))
  const contents = useQuery({ ...reviewFileQuery(id, round, file.path), enabled: expanded })
  const flags = [
    file.lock && 'lock file',
    file.generated && 'generated',
    file.large && 'large',
    file.binary && 'binary',
  ].filter(Boolean)

  return (
    <div className="stack">
      <header className="row spread file-header">
        <span className="row">
          <strong className="mono">
            {file.oldPath ? `${file.oldPath} → ${file.path}` : file.path}
          </strong>
          <span className="muted">
            {kindLabel[file.kind]} · {lineCounts(file)}
          </span>
          {flags.map((flag) => (
            <span key={String(flag)} className="badge">
              {flag}
            </span>
          ))}
        </span>
        <span className="row">
          <label className="row">
            <input type="checkbox" checked={file.viewed} onChange={onToggleViewed} /> Viewed
          </label>
          <button
            type="button"
            aria-pressed={mode === 'side-by-side'}
            onClick={() => onMode('side-by-side')}
          >
            Side by side
          </button>
          <button type="button" aria-pressed={mode === 'inline'} onClick={() => onMode('inline')}>
            Inline
          </button>
        </span>
      </header>
      {!expanded ? (
        <div className="card stack">
          <p className="muted">
            {file.binary
              ? 'A binary file.'
              : file.large
                ? 'A large change; it is not shown by default.'
                : 'A generated or lock file; it is not shown by default.'}
          </p>
          <div>
            <button type="button" onClick={() => setExpanded(true)}>
              Show diff
            </button>
          </div>
        </div>
      ) : contents.error ? (
        <p className="error">{contents.error.message}</p>
      ) : !contents.data ? (
        <p className="muted">Loading the file…</p>
      ) : (
        <Contents
          old={contents.data.old}
          next={contents.data.new}
          language={contents.data.language}
          mode={mode}
        />
      )}
    </div>
  )
}

function Contents({
  old,
  next,
  language,
  mode,
}: {
  old: FileSide | null
  next: FileSide | null
  language: string
  mode: DiffMode
}) {
  const sides = [old, next].filter((s): s is FileSide => s !== null)
  const sizes = `${old ? sizeText(old.size) : 'none'} → ${next ? sizeText(next.size) : 'deleted'}`
  if (sides.some((s) => s.binary)) {
    return <p className="card muted">Binary file ({sizes}); it cannot be shown as text.</p>
  }
  if (sides.some((s) => s.tooLarge)) {
    return (
      <p className="card muted">
        Too large to show here ({sizes}). Fetch the run branch from the hub to read it.
      </p>
    )
  }
  return (
    <Suspense fallback={<p className="muted">Loading the diff…</p>}>
      <DiffView
        original={old?.text ?? ''}
        modified={next?.text ?? ''}
        language={language}
        mode={mode}
      />
    </Suspense>
  )
}

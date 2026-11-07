import { useQuery } from '@tanstack/react-query'
import { Link } from '@tanstack/react-router'
import { lazy, Suspense, useEffect, useState } from 'react'
import { useWatch } from '../live/live'
import {
  anchorFor,
  approveAction,
  changeSetQuery,
  deliveryLine,
  fileOrder,
  fileThreads,
  groupFiles,
  kindLabel,
  kindLetter,
  lineCounts,
  loadDiffMode,
  loadOrderMode,
  nextUnviewed,
  openThreadCounts,
  reviewFileQuery,
  reviewFilesQuery,
  saveDiffMode,
  saveOrderMode,
  riskOf,
  shortSha,
  sizeText,
  startsCollapsed,
  stepFile,
  storyFiles,
  threadsQuery,
  unsentForAgent,
  useRebase,
  useThreads,
  useVerdict,
  useViewed,
  viewLabel,
  type ChangeSet,
  type CodeRef,
  type DiffMode,
  type FileOrderMode,
  type FileSide,
  type Pane,
  type ReviewAnalysis,
  type ReviewFile,
  type ReviewFiles,
  type ReviewRound,
  type ReviewThread,
} from '../review/review'
import { AnalysisPanel } from '../review/AnalysisPanel'
import { SelfReport } from '../review/SelfReport'
import { GeneralComments, NewThreadForm, ThreadView } from '../review/Threads'
import type { DiffZone, LineSelection } from '../review/DiffView'
import { runStateLabel } from '../runs/runs'
import { AppShell } from './AppShell'

// Monaco is large: it loads with the first diff, not with the app.
const DiffView = lazy(() => import('../review/DiffView'))

/** A ref from the analysis to show in the diff; `n` makes the same ref show again when clicked again. */
type Reveal = CodeRef & { n: number }

export function ReviewPage({ id }: { id: string }) {
  useWatch('WatchAll')
  const changeSet = useQuery(changeSetQuery(id))
  const threads = useQuery(threadsQuery(id))
  const mutations = useThreads(id)
  const [roundNo, setRoundNo] = useState<number | null>(null)
  // A later round shows the interdiff since the round before unless the whole change is asked for.
  const [full, setFull] = useState(false)
  const [reveal, setReveal] = useState<Reveal | null>(null)
  const data = changeSet.data
  const round = data?.rounds.find((r) => r.roundNo === roundNo) ?? data?.rounds.at(-1)
  const analysis = round?.analysis?.result ?? null
  const threadList = threads.data ?? []
  const locked = data?.status !== 'open'

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
          <Header
            changeSet={data}
            round={round}
            full={full}
            onRound={(n) => {
              setRoundNo(n)
              setFull(false)
            }}
            onFull={setFull}
          />
          <AnalysisPanel
            changeSet={data}
            round={round}
            onReveal={(ref) => setReveal((r) => ({ ...ref, n: (r?.n ?? 0) + 1 }))}
          />
          <details open className="card">
            <summary>
              <strong>The agent&apos;s report</strong>
            </summary>
            <SelfReport
              summary={round.selfReport.summary}
              // With an analysis, the agent's claims are in its coverage table.
              coverage={analysis ? [] : round.coverage}
              openConcerns={round.selfReport.openConcerns}
              uncommitted={round.uncommitted}
            />
          </details>
          <details open className="card">
            <summary>
              <strong>Comments on the whole change</strong>{' '}
              <span className="muted">({threadList.filter((t) => t.path === null).length})</span>
            </summary>
            <GeneralComments
              roundNo={round.roundNo}
              list={threadList.filter((t) => t.path === null)}
              threads={mutations}
              locked={locked}
            />
            {threads.error && <p className="error">{threads.error.message}</p>}
          </details>
          <VerdictBar changeSet={data} round={round} threads={threadList} />
          <Files
            key={`${round.roundNo}-${full}`}
            id={id}
            round={round}
            full={full}
            analysis={analysis}
            reveal={reveal}
            threads={threadList}
            locked={locked}
          />
        </article>
      )}
    </AppShell>
  )
}

function Header({
  changeSet,
  round,
  full,
  onRound,
  onFull,
}: {
  changeSet: ChangeSet
  round: ReviewRound
  full: boolean
  onRound: (no: number) => void
  onFull: (full: boolean) => void
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
        {round.roundNo > 1 && (
          <span className="row" role="group" aria-label="What to show">
            <button type="button" aria-pressed={!full} onClick={() => onFull(false)}>
              Changes since round {round.roundNo - 1}
            </button>
            <button type="button" aria-pressed={full} onClick={() => onFull(true)}>
              Full change
            </button>
          </span>
        )}
      </p>
    </header>
  )
}

function VerdictBar({
  changeSet,
  round,
  threads,
}: {
  changeSet: ChangeSet
  round: ReviewRound
  threads: readonly ReviewThread[]
}) {
  const verdict = useVerdict(changeSet.id)
  const rebase = useRebase(changeSet.id)
  const [requesting, setRequesting] = useState(false)
  const [message, setMessage] = useState('')
  const latest = round.roundNo === changeSet.rounds.at(-1)?.roundNo
  const unsent = unsentForAgent(threads)
  const delivered = deliveryLine(changeSet)
  if (delivered && latest) {
    return (
      <div className="card" data-testid="verdict" role="status">
        <p>
          Round {round.roundNo}: <strong>approved</strong>
          {round.verdictAt && (
            <span className="muted"> on {new Date(round.verdictAt).toLocaleString()}</span>
          )}
          . {delivered}
        </p>
        {changeSet.status === 'delivering' && changeSet.delivery.error && (
          <p className="error">{changeSet.delivery.error}</p>
        )}
      </div>
    )
  }
  if (round.verdict) {
    const working = latest && round.verdict === 'request_changes' && changeSet.status === 'open'
    return (
      <p className="card" data-testid="verdict">
        Round {round.roundNo}:{' '}
        <strong>{round.verdict === 'reject' ? 'rejected' : 'changes requested'}</strong>
        {round.verdictAt && (
          <span className="muted"> on {new Date(round.verdictAt).toLocaleString()}</span>
        )}
        {working && (
          <span className="muted">
            . The agent is working on them; its next round shows up here.
          </span>
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
  const { conflict, error } = changeSet.delivery
  return (
    <div className="card row verdicts" aria-label="Verdict">
      {conflict ? (
        <div className="conflict" role="alert">
          <p>
            <strong>Not merged:</strong> {changeSet.defaultBranch} moved since the agent started,
            and {conflict.files.length === 1 ? 'this file conflicts' : 'these files conflict'}:
          </p>
          <ul className="paths">
            {conflict.files.map((file) => (
              <li key={file}>
                <code>{file}</code>
              </li>
            ))}
          </ul>
          <div className="row">
            <button
              type="button"
              className="primary"
              disabled={rebase.isPending}
              onClick={() => rebase.mutate()}
            >
              Ask the agent to rebase
            </button>
            <span className="muted">
              It gets the current {changeSet.defaultBranch}, resolves the conflicts and submits a
              new round.
            </span>
          </div>
          {rebase.error && <span className="error">{rebase.error.message}</span>}
        </div>
      ) : (
        error && (
          <p className="error" role="alert">
            The last approval did not get through: {error} Approve again to retry.
          </p>
        )
      )}
      <button
        type="button"
        className="primary"
        disabled={verdict.isPending || !!conflict}
        title={conflict ? 'Resolve the conflict first' : undefined}
        onClick={() => verdict.mutate({ verdict: 'approve' })}
      >
        {approveAction(changeSet)}
      </button>
      <button type="button" disabled={verdict.isPending} onClick={() => setRequesting((r) => !r)}>
        Request changes…
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
            verdict.mutate({ verdict: 'reject' })
          }
        }}
      >
        Reject
      </button>
      <span className="muted">
        {unsent === 0
          ? 'No comments for the agent yet.'
          : `${unsent} comment${unsent === 1 ? '' : 's'} will be sent to the agent.`}
      </span>
      {requesting && (
        <form
          className="stack request-changes"
          onSubmit={(e) => {
            e.preventDefault()
            verdict.mutate({ verdict: 'request_changes', message })
          }}
        >
          <label className="field">
            <span>
              A message for the agent (optional
              {unsent === 0 ? ', but without line comments it is all the agent gets' : ''})
            </span>
            <textarea
              rows={3}
              value={message}
              maxLength={10000}
              onChange={(e) => setMessage(e.target.value)}
            />
          </label>
          <div className="row">
            <button
              type="submit"
              className="primary"
              disabled={verdict.isPending || (unsent === 0 && message.trim() === '')}
            >
              Send to the agent
            </button>
            <span className="muted">
              The agent continues with your comments and submits a new round.
            </span>
          </div>
        </form>
      )}
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

function Files({
  id,
  round,
  full,
  analysis,
  reveal,
  threads,
  locked,
}: {
  id: string
  round: ReviewRound
  full: boolean
  analysis: ReviewAnalysis | null
  reveal: Reveal | null
  threads: readonly ReviewThread[]
  locked: boolean
}) {
  const files = useQuery(reviewFilesQuery(id, round.roundNo, full))
  const viewed = useViewed(id, round.roundNo, full)
  const [selected, setSelected] = useState<string | null>(null)
  const [mode, setMode] = useState<DiffMode>(() => loadDiffMode())
  const [orderMode, setOrderMode] = useState<FileOrderMode>(() => loadOrderMode())
  const story = orderMode === 'story' ? analysis : null
  const list = files.data?.files
  const order = list ? fileOrder(list, story) : []
  const current = list?.find((f) => f.path === selected) ?? list?.find((f) => f.path === order[0])

  // A ref clicked in the analysis selects its file.
  const [shownReveal, setShownReveal] = useState(reveal)
  if (reveal !== shownReveal) {
    setShownReveal(reveal)
    if (reveal) setSelected(reveal.path)
  }

  const toggleViewed = (file: ReviewFile) => {
    const marking = !file.viewed
    viewed.mutate({ path: file.path, viewed: marking })
    if (marking && list) {
      // On to the next file that still needs a look.
      setSelected(nextUnviewed(list, file.path, new Set([file.path]), order) ?? file.path)
    }
  }

  useEffect(() => {
    if (!list || !current) return
    const onKey = (e: KeyboardEvent) => {
      if (e.ctrlKey || e.metaKey || e.altKey || isTyping(e.target)) return
      if (e.key === 'j' || e.key === 'k') {
        setSelected(stepFile(order, current.path, e.key === 'j' ? 1 : -1))
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
  const shown = files.data

  return (
    <div className="stack">
      {shown.interdiff === 'per_file' && (
        <p className="card notice" role="note">
          The base changed since round {shown.previousRound} (
          {shortSha(shown.previousBaseSha ?? '')} → {shortSha(shown.baseSha)}): the agent rebased.
          Showing the files whose change differs from round {shown.previousRound}, each with its
          whole change.
          {shown.dropped.length > 0 && (
            <>
              {' '}
              No longer changed: <span className="mono">{shown.dropped.join(', ')}</span>.
            </>
          )}
        </p>
      )}
      <div className="review-layout">
        <nav className="file-tree card" aria-label="Changed files">
          <p className="row spread">
            <strong>
              {viewedCount} of {list.length} files viewed
            </strong>
            {shown.previousRound !== null && <span className="muted">{viewLabel(shown)}</span>}
            <span className="muted" title="j / k: next or previous file, v: viewed">
              j k v
            </span>
          </p>
          {analysis && (
            <p className="row" role="group" aria-label="File order">
              {(['story', 'path'] as const).map((m) => (
                <button
                  key={m}
                  type="button"
                  aria-pressed={orderMode === m}
                  onClick={() => {
                    setOrderMode(m)
                    saveOrderMode(m)
                  }}
                >
                  {m === 'story' ? 'Story order' : 'Path order'}
                </button>
              ))}
            </p>
          )}
          {files.data.truncated && (
            <p className="muted">Only the first {list.length} files are listed.</p>
          )}
          <FileTree
            files={list}
            story={story}
            risks={analysis}
            comments={openThreadCounts(threads)}
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
              shown={shown}
              full={full}
              threads={threads}
              locked={locked}
              file={current}
              reveal={reveal?.path === current.path ? reveal : null}
              mode={mode}
              onMode={(m) => {
                setMode(m)
                saveDiffMode(m)
              }}
              onToggleViewed={() => toggleViewed(current)}
            />
          ) : (
            <p className="muted">
              {shown.previousRound !== null && !full
                ? `No files changed since round ${shown.previousRound}.`
                : 'No files changed.'}
            </p>
          )}
        </section>
      </div>
    </div>
  )
}

function FileTree({
  files,
  story,
  risks,
  comments,
  current,
  onSelect,
  onToggle,
}: {
  files: readonly ReviewFile[]
  story: ReviewAnalysis | null
  risks: ReviewAnalysis | null
  /** Open threads per path. */
  comments: ReadonlyMap<string, number>
  current: string | null
  onSelect: (path: string) => void
  onToggle: (file: ReviewFile) => void
}) {
  const { groups, skimmed } = groupFiles(files)
  const row = (file: ReviewFile, name: string) => {
    const risk = riskOf(risks, file.path)
    return (
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
        {risk && (
          <span className={`badge risk ${risk.risk}`} title={risk.reason}>
            {risk.risk}
          </span>
        )}
        {(comments.get(file.path) ?? 0) > 0 && (
          <span className="badge comments" title="Open comment threads">
            {comments.get(file.path)} 💬
          </span>
        )}
        <span className="muted counts">{lineCounts(file)}</span>
      </li>
    )
  }
  const skimmedGroup = skimmed.length > 0 && (
    <details>
      <summary className="muted">Generated and lock files ({skimmed.length})</summary>
      <ul className="files">{skimmed.map((f) => row(f, f.path))}</ul>
    </details>
  )
  if (story) {
    return (
      <div className="stack tree">
        {storyFiles(files, story).groups.map((group) => (
          <div key={group.title} className="story-group">
            <p className="group-title">
              <strong>{group.title}</strong> <span className="muted">{group.layer}</span>
            </p>
            {group.note && <p className="muted small">{group.note}</p>}
            <ul className="files">{group.files.map((f) => row(f, f.path))}</ul>
          </div>
        ))}
        {skimmedGroup}
      </div>
    )
  }
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
      {skimmedGroup}
    </div>
  )
}

function FilePane({
  id,
  shown,
  full,
  threads,
  locked,
  file,
  reveal,
  mode,
  onMode,
  onToggleViewed,
}: {
  id: string
  shown: ReviewFiles
  full: boolean
  threads: readonly ReviewThread[]
  locked: boolean
  file: ReviewFile
  reveal: Reveal | null
  mode: DiffMode
  onMode: (mode: DiffMode) => void
  onToggleViewed: () => void
}) {
  const [opened, setExpanded] = useState(!startsCollapsed(file))
  // A ref from the analysis opens a collapsed file too.
  const expanded = opened || reveal !== null
  const contents = useQuery({
    ...reviewFileQuery(id, shown.roundNo, file.path, full),
    enabled: expanded,
  })
  const mutations = useThreads(id)
  const [selection, setSelection] = useState<LineSelection | null>(null)
  const [draft, setDraft] = useState<LineSelection | null>(null)
  const { inline, listed } = fileThreads(threads, file, shown, mode === 'side-by-side')
  const pathIn = (pane: Pane) => (pane === 'original' ? (file.oldPath ?? file.path) : file.path)
  const lines = (s: LineSelection) =>
    s.lineEnd > s.lineStart ? `lines ${s.lineStart}–${s.lineEnd}` : `line ${s.lineStart}`
  const zones: DiffZone[] = [
    ...inline.map(({ thread, pane }) => ({
      key: thread.id,
      pane,
      afterLine: thread.lineEnd ?? thread.lineStart ?? 1,
      content: <ThreadView thread={thread} threads={mutations} locked={locked} />,
    })),
    ...(draft
      ? [
          {
            key: 'draft',
            pane: draft.pane,
            afterLine: draft.lineEnd,
            content: (
              <NewThreadForm
                threads={mutations}
                anchor={{
                  ...anchorFor(shown, draft.pane),
                  path: pathIn(draft.pane),
                  lineStart: draft.lineStart,
                  lineEnd: draft.lineEnd,
                }}
                label={`Comment on ${lines(draft)} of the ${draft.pane === 'original' ? 'old' : 'new'} version`}
                onDone={() => setDraft(null)}
              />
            ),
          },
        ]
      : []),
  ]
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
          {!locked && expanded && (
            <button
              type="button"
              disabled={!selection || draft !== null}
              title="Click a line (or select lines) in the diff first"
              onClick={() => setDraft(selection)}
            >
              {selection ? `Comment on ${lines(selection)}` : 'Comment on a line'}
            </button>
          )}
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
      {listed.length > 0 && (
        <details className="card">
          <summary className="small">
            {listed.length} thread{listed.length === 1 ? '' : 's'} on other versions of this file
          </summary>
          <div className="stack">
            {listed.map((t) => (
              <ThreadView key={t.id} thread={t} threads={mutations} locked={locked} showPlace />
            ))}
          </div>
        </details>
      )}
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
          reveal={reveal}
          zones={zones}
          onSelection={setSelection}
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
  reveal,
  zones,
  onSelection,
}: {
  old: FileSide | null
  next: FileSide | null
  language: string
  mode: DiffMode
  reveal: Reveal | null
  zones: readonly DiffZone[]
  onSelection: (selection: LineSelection | null) => void
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
        reveal={reveal}
        zones={zones}
        onSelection={onSelection}
      />
    </Suspense>
  )
}

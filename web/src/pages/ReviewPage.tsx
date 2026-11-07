import { useQuery } from '@tanstack/react-query'
import { Link } from '@tanstack/react-router'
import { lazy, Suspense, useEffect, useState, type ReactNode } from 'react'
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
  type ChangeKind,
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
import { runStateTone } from '../runs/tones'
import { Button } from '../ui/Button'
import { cx } from '../ui/cx'
import { ErrorText, Field, Hint, Textarea } from '../ui/form'
import { ChevronRightIcon, CheckIcon, CommentIcon } from '../ui/icons'
import { CrumbSep, Kbd, Loading } from '../ui/layout'
import { ConfirmDialog } from '../ui/overlay'
import { Segmented } from '../ui/Segmented'
import { Tag } from '../ui/Tag'
import { usePageTitle } from '../ui/title'
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
  usePageTitle(data ? `Review ${data.ticketKey}` : 'Review')
  const round = data?.rounds.find((r) => r.roundNo === roundNo) ?? data?.rounds.at(-1)
  const analysis = round?.analysis?.result ?? null
  const threadList = threads.data ?? []
  const general = threadList.filter((t) => t.path === null)
  const locked = data?.status !== 'open'

  return (
    <AppShell rail>
      {changeSet.error && (
        <div className="p-6">
          <ErrorText>{changeSet.error.message}</ErrorText>
        </div>
      )}
      {!data && !changeSet.error && (
        <div className="p-6">
          <Loading />
        </div>
      )}
      {data && round && (
        <article className="flex flex-col">
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
          <VerdictBar changeSet={data} round={round} threads={threadList} />
          <div className="flex flex-col gap-5 px-4 pt-5 pb-12 sm:px-6">
            <div className="grid items-start gap-5 xl:grid-cols-[minmax(0,1.7fr)_minmax(0,1fr)]">
              <div className="flex min-w-0 flex-col gap-5">
                <AnalysisPanel
                  changeSet={data}
                  round={round}
                  onReveal={(ref) => setReveal((r) => ({ ...ref, n: (r?.n ?? 0) + 1 }))}
                />
                {!round.analysis && <AgentReport round={round} analysis={analysis} />}
              </div>
              <div className="flex min-w-0 flex-col gap-5">
                {round.analysis && <AgentReport round={round} analysis={analysis} />}
                <Collapsible title="Comments on the whole change" count={general.length}>
                  <GeneralComments
                    roundNo={round.roundNo}
                    list={general}
                    threads={mutations}
                    locked={locked}
                  />
                  {threads.error && <ErrorText>{threads.error.message}</ErrorText>}
                </Collapsible>
              </div>
            </div>
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
          </div>
        </article>
      )}
    </AppShell>
  )
}

/** A panel that opens and closes by its title row. */
function Collapsible({
  title,
  count,
  children,
  defaultOpen = true,
}: {
  title: string
  count?: number
  children: ReactNode
  defaultOpen?: boolean
}) {
  return (
    <details open={defaultOpen} className="group rounded-lg border border-line bg-surface">
      <summary className="flex cursor-pointer list-none items-center gap-2 px-4 py-2.5 select-none [&::-webkit-details-marker]:hidden">
        <ChevronRightIcon
          size={14}
          className="shrink-0 text-fg-subtle transition-transform group-open:rotate-90"
        />
        <span className="text-sm font-semibold">{title}</span>
        {count != null && <span className="text-sm text-fg-subtle tabular-nums">{count}</span>}
      </summary>
      <div className="border-t border-line-soft px-4 py-3">{children}</div>
    </details>
  )
}

function AgentReport({ round, analysis }: { round: ReviewRound; analysis: ReviewAnalysis | null }) {
  return (
    <Collapsible title="The agent’s report">
      <SelfReport
        summary={round.selfReport.summary}
        // With an analysis, the agent's claims are in its coverage table.
        coverage={analysis ? [] : round.coverage}
        openConcerns={round.selfReport.openConcerns}
        uncommitted={round.uncommitted}
      />
    </Collapsible>
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
    <header className="border-b border-line bg-surface px-4 pt-3.5 pb-3 sm:px-6">
      <div className="flex flex-wrap items-start gap-x-4 gap-y-3">
        <div className="min-w-0">
          <nav
            aria-label="Breadcrumb"
            className="mb-0.5 flex flex-wrap items-center gap-1.5 text-sm text-fg-subtle [&_a]:text-fg-muted"
          >
            <Link to="/projects/$slug/tickets" params={{ slug: changeSet.projectSlug }}>
              {changeSet.projectSlug}
            </Link>
            <CrumbSep />
            <Link
              to="/projects/$slug/tickets/$number"
              params={{ slug: changeSet.projectSlug, number: String(changeSet.ticketNumber) }}
              className="font-mono text-mono"
            >
              {changeSet.ticketKey}
            </Link>
          </nav>
          <h1 className="m-0 text-lg font-semibold tracking-[-0.01em]">{changeSet.ticketTitle}</h1>
        </div>
        <div className="ml-auto flex flex-wrap items-center gap-2">
          {changeSet.rounds.length > 1 && (
            <Segmented
              label="Round"
              size="sm"
              value={String(round.roundNo)}
              onChange={(v) => onRound(Number(v))}
              options={changeSet.rounds.map((r) => ({
                value: String(r.roundNo),
                label: `Round ${r.roundNo}`,
              }))}
            />
          )}
          {round.roundNo > 1 && (
            <Segmented
              label="What to show"
              size="sm"
              value={full ? 'full' : 'since'}
              onChange={(v) => onFull(v === 'full')}
              options={[
                { value: 'since', label: `Changes since round ${round.roundNo - 1}` },
                { value: 'full', label: 'Full change' },
              ]}
            />
          )}
        </div>
      </div>
      <p className="m-0 mt-2 flex flex-wrap items-center gap-x-2 gap-y-1 text-sm text-fg-muted">
        <Tag tone={runStateTone[changeSet.runState]}>{runStateLabel[changeSet.runState]}</Tag>
        <span className="font-mono text-mono">{changeSet.branch}</span>
        <span aria-hidden="true" className="text-fg-faint">
          →
        </span>
        <span className="font-mono text-mono">{changeSet.defaultBranch}</span>
        <span aria-hidden="true" className="text-fg-faint">
          ·
        </span>
        <span>
          Claude Code · {changeSet.model} · {changeSet.repositoryName}
        </span>
        <span aria-hidden="true" className="text-fg-faint">
          ·
        </span>
        <span className="font-mono text-mono" title="Base and head of this round">
          {shortSha(round.baseSha)}..{shortSha(round.headSha)}
        </span>
      </p>
    </header>
  )
}

const verdictStrip =
  'flex flex-wrap items-center gap-x-3 gap-y-2 border-b border-line bg-bg px-4 py-2.5 sm:px-6'

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
    const done = changeSet.status === 'merged' || changeSet.status === 'pushed'
    return (
      <div className={verdictStrip} data-testid="verdict" role="status">
        <p className="m-0 flex flex-wrap items-center gap-x-2">
          {done ? (
            <CheckIcon size={15} strokeWidth={2} className="text-ok" />
          ) : (
            <span className="size-2 animate-pulse rounded-full bg-run-solid" aria-hidden="true" />
          )}
          <span>
            Round {round.roundNo}: <strong className="font-semibold">approved</strong>
            {round.verdictAt && (
              <span className="text-fg-subtle">
                {' '}
                on {new Date(round.verdictAt).toLocaleString()}
              </span>
            )}
            . {delivered}
          </span>
        </p>
        {changeSet.status === 'delivering' && changeSet.delivery.error && (
          <p className="m-0 basis-full text-sm text-bad">{changeSet.delivery.error}</p>
        )}
      </div>
    )
  }
  if (round.verdict) {
    const working = latest && round.verdict === 'request_changes' && changeSet.status === 'open'
    return (
      <p className={cx(verdictStrip, 'm-0')} data-testid="verdict">
        <span>
          Round {round.roundNo}:{' '}
          <strong className={cx('font-semibold', round.verdict === 'reject' && 'text-bad')}>
            {round.verdict === 'reject' ? 'rejected' : 'changes requested'}
          </strong>
          {round.verdictAt && (
            <span className="text-fg-subtle"> on {new Date(round.verdictAt).toLocaleString()}</span>
          )}
          {working && (
            <span className="text-fg-muted">
              . The agent is working on them; its next round shows up here.
            </span>
          )}
        </span>
      </p>
    )
  }
  if (changeSet.status !== 'open' || changeSet.runState !== 'in_review' || !latest) {
    return (
      <p className={cx(verdictStrip, 'm-0 text-fg-muted')}>
        {changeSet.status === 'closed'
          ? 'This change was closed: its run ended.'
          : 'No verdict on this round.'}
      </p>
    )
  }
  const { conflict, error } = changeSet.delivery
  return (
    <div className={cx(verdictStrip, 'sticky top-0 z-10')} aria-label="Verdict" role="group">
      {conflict ? (
        <div
          className="flex basis-full flex-col gap-2 rounded-md border border-wait-bg bg-wait-bg px-3 py-2.5"
          role="alert"
        >
          <p className="m-0">
            <strong className="font-semibold text-wait">Not merged:</strong>{' '}
            <code className="font-mono text-mono">{changeSet.defaultBranch}</code> moved since the
            agent started, and{' '}
            {conflict.files.length === 1 ? 'this file conflicts' : 'these files conflict'}:
          </p>
          <ul className="m-0 list-none space-y-0.5 p-0 font-mono text-mono">
            {conflict.files.map((file) => (
              <li key={file}>{file}</li>
            ))}
          </ul>
          <div className="flex flex-wrap items-center gap-3">
            <Button
              variant="primary"
              size="sm"
              disabled={rebase.isPending}
              onClick={() => rebase.mutate()}
            >
              Ask the agent to rebase
            </Button>
            <Hint>
              It gets the current {changeSet.defaultBranch}, resolves the conflicts and submits a
              new round.
            </Hint>
          </div>
          {rebase.error && <ErrorText>{rebase.error.message}</ErrorText>}
        </div>
      ) : (
        error && (
          <p
            className="m-0 basis-full rounded-md bg-bad-bg px-3 py-2 text-sm text-bad"
            role="alert"
          >
            The last approval did not get through: {error} Approve again to retry.
          </p>
        )
      )}
      <span className="flex items-center gap-1.5 text-sm text-fg-muted">
        <CommentIcon size={14} className="text-fg-subtle" />
        {unsent === 0
          ? 'No comments for the agent yet.'
          : `${unsent} comment${unsent === 1 ? '' : 's'} will be sent to the agent.`}
      </span>
      <div className="ml-auto flex flex-wrap items-center gap-2">
        <ConfirmDialog
          trigger={
            <Button variant="ghost" disabled={verdict.isPending}>
              Reject
            </Button>
          }
          title="Reject this change?"
          description="The run is closed and the ticket goes back to open. The branch stays on the hub."
          confirmLabel="Reject change"
          danger
          onConfirm={() => verdict.mutate({ verdict: 'reject' })}
        />
        <Button
          disabled={verdict.isPending}
          aria-expanded={requesting}
          onClick={() => setRequesting((r) => !r)}
        >
          Request changes…
        </Button>
        <Button
          variant="primary"
          disabled={verdict.isPending || !!conflict}
          title={conflict ? 'Resolve the conflict first' : undefined}
          onClick={() => verdict.mutate({ verdict: 'approve' })}
        >
          <CheckIcon size={14} strokeWidth={1.8} />
          {approveAction(changeSet)}
        </Button>
      </div>
      {requesting && (
        <form
          className="flex basis-full flex-col gap-3 rounded-md border border-line bg-surface p-3"
          onSubmit={(e) => {
            e.preventDefault()
            verdict.mutate({ verdict: 'request_changes', message })
          }}
        >
          <Field
            label={`A message for the agent (optional${unsent === 0 ? ', but without line comments it is all the agent gets' : ''})`}
          >
            <Textarea
              rows={3}
              value={message}
              maxLength={10000}
              onChange={(e) => setMessage(e.target.value)}
            />
          </Field>
          <div className="flex flex-wrap items-center gap-3">
            <Button
              type="submit"
              variant="primary"
              disabled={verdict.isPending || (unsent === 0 && message.trim() === '')}
            >
              Send to the agent
            </Button>
            <Hint>The agent continues with your comments and submits a new round.</Hint>
          </div>
        </form>
      )}
      {verdict.error && <ErrorText className="basis-full">{verdict.error.message}</ErrorText>}
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

  if (files.error) return <ErrorText>{files.error.message}</ErrorText>
  if (!files.data || !list) return <Loading label="Loading the files…" />
  const viewedCount = list.filter((f) => f.viewed).length
  const shown = files.data

  return (
    <div className="flex flex-col gap-4">
      {shown.interdiff === 'per_file' && (
        <p
          className="m-0 rounded-lg border border-wait-bg bg-wait-bg px-4 py-2.5 text-sm text-fg-2"
          role="note"
        >
          The base changed since round {shown.previousRound} (
          <span className="font-mono text-mono">
            {shortSha(shown.previousBaseSha ?? '')} → {shortSha(shown.baseSha)}
          </span>
          ): the agent rebased. Showing the files whose change differs from round{' '}
          {shown.previousRound}, each with its whole change.
          {shown.dropped.length > 0 && (
            <>
              {' '}
              No longer changed:{' '}
              <span className="font-mono text-mono">{shown.dropped.join(', ')}</span>.
            </>
          )}
        </p>
      )}
      <div className="grid items-start gap-5 lg:grid-cols-[minmax(14rem,17rem)_minmax(0,1fr)]">
        <nav
          className="flex max-h-[calc(100vh-5rem)] flex-col overflow-hidden rounded-lg border border-line bg-surface lg:sticky lg:top-16"
          aria-label="Changed files"
        >
          <div className="flex flex-col gap-2 border-b border-line-soft px-3 pt-2.5 pb-3">
            <div className="flex items-center gap-2 text-xs">
              <strong className="font-semibold text-fg-2">
                {viewedCount} of {list.length} files viewed
              </strong>
              <span
                className="ml-auto flex items-center gap-1 text-fg-subtle"
                title="j / k: next or previous file, v: viewed"
              >
                <Kbd>j</Kbd>
                <Kbd>k</Kbd>
                <Kbd>v</Kbd>
              </span>
            </div>
            <div className="h-1 overflow-hidden rounded-full bg-line-soft" aria-hidden="true">
              <div
                className="h-full rounded-full bg-ok-solid transition-[width]"
                style={{ width: `${list.length ? (viewedCount / list.length) * 100 : 0}%` }}
              />
            </div>
            {shown.previousRound !== null && (
              <span className="text-xs text-fg-muted">{viewLabel(shown)}</span>
            )}
            {analysis && (
              <Segmented
                label="File order"
                size="sm"
                className="self-start"
                value={orderMode}
                onChange={(m) => {
                  setOrderMode(m)
                  saveOrderMode(m)
                }}
                options={[
                  { value: 'story', label: 'Story order' },
                  { value: 'path', label: 'Path order' },
                ]}
              />
            )}
            {files.data.truncated && <Hint>Only the first {list.length} files are listed.</Hint>}
          </div>
          <div className="min-h-0 overflow-y-auto px-1.5 py-2">
            <FileTree
              files={list}
              story={story}
              risks={analysis}
              comments={openThreadCounts(threads)}
              current={current?.path ?? null}
              onSelect={setSelected}
              onToggle={toggleViewed}
            />
          </div>
          {viewed.error && <ErrorText className="px-3 pb-2">{viewed.error.message}</ErrorText>}
        </nav>
        <section className="min-w-0" aria-label="Diff">
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
            <p className="m-0 rounded-lg border border-dashed border-line-strong px-6 py-10 text-center text-fg-muted">
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

const kindColor: Record<ChangeKind, string> = {
  added: 'text-ok',
  modified: 'text-wait',
  deleted: 'text-bad',
  renamed: 'text-run',
  type_changed: 'text-wait',
}

function KindLetter({ kind }: { kind: ChangeKind }) {
  return (
    <span
      className={cx(
        'w-3.5 shrink-0 text-center font-mono text-[11px] font-semibold',
        kindColor[kind],
      )}
    >
      {kindLetter[kind]}
    </span>
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
    const open = comments.get(file.path) ?? 0
    const isCurrent = file.path === current
    return (
      <li
        key={file.path}
        className={cx(
          'flex items-center gap-1.5 rounded-md pl-1.5 text-sm',
          isCurrent ? 'bg-selected shadow-[inset_2px_0_0_var(--c-run-solid)]' : 'hover:bg-hover',
        )}
        data-path={file.path}
        data-viewed={file.viewed}
      >
        <input
          type="checkbox"
          className="size-3.5 shrink-0 cursor-pointer accent-(--c-ok-solid)"
          checked={file.viewed}
          aria-label={`Viewed ${file.path}`}
          onChange={() => onToggle(file)}
        />
        <button
          type="button"
          className="flex min-w-0 flex-1 cursor-pointer items-center gap-1.5 py-1 text-left"
          aria-current={isCurrent ? 'true' : undefined}
          title={`${kindLabel[file.kind]}: ${file.oldPath ? `${file.oldPath} → ` : ''}${file.path}`}
          onClick={() => onSelect(file.path)}
        >
          <KindLetter kind={file.kind} />
          <span
            className={cx(
              'min-w-0 flex-1 truncate font-mono text-mono',
              file.viewed ? 'text-fg-faint' : 'text-fg',
            )}
            style={{ direction: 'rtl', textAlign: 'left' }}
          >
            {/* rtl keeps the end of a long path (the file name) visible when it is cut. */}
            <bdi>{name}</bdi>
          </span>
        </button>
        {risk && (
          <Tag
            tone={risk.risk === 'high' ? 'bad' : risk.risk === 'medium' ? 'wait' : 'neutral'}
            className="h-[18px] px-1.5 text-[11px]"
            title={risk.reason}
          >
            {risk.risk}
          </Tag>
        )}
        {open > 0 && (
          <span
            className="flex shrink-0 items-center gap-0.5 text-xs text-fg-muted"
            title="Open comment threads"
            aria-label={`${open} open comment thread${open === 1 ? '' : 's'}`}
            role="img"
          >
            <CommentIcon size={12} />
            {open}
          </span>
        )}
        <span className="shrink-0 pr-2 font-mono text-[11px] text-fg-subtle">
          {lineCounts(file)}
        </span>
      </li>
    )
  }
  const list = (items: readonly ReviewFile[], name: (f: ReviewFile) => string) => (
    <ul className="m-0 flex list-none flex-col gap-px p-0">{items.map((f) => row(f, name(f)))}</ul>
  )
  const skimmedGroup = skimmed.length > 0 && (
    <details className="group mt-3">
      <summary className="flex cursor-pointer list-none items-center gap-1 px-1.5 py-1 text-xs font-medium text-fg-muted hover:text-fg [&::-webkit-details-marker]:hidden">
        <ChevronRightIcon size={12} className="transition-transform group-open:rotate-90" />
        Generated and lock files ({skimmed.length})
      </summary>
      {list(skimmed, (f) => f.path)}
    </details>
  )
  if (story) {
    return (
      <div className="flex flex-col gap-3">
        {storyFiles(files, story).groups.map((group) => (
          <div key={group.title}>
            <p className="m-0 flex items-baseline gap-2 px-1.5 pt-1">
              <span className="text-xs font-semibold text-fg-2">{group.title}</span>
              <span className="text-[11px] text-fg-subtle">{group.layer}</span>
            </p>
            {group.note && <p className="m-0 mb-1 px-1.5 text-xs text-fg-subtle">{group.note}</p>}
            {list(group.files, (f) => f.path)}
          </div>
        ))}
        {skimmedGroup}
      </div>
    )
  }
  return (
    <div className="flex flex-col gap-2.5">
      {groups.map((group) => (
        <div key={group.directory}>
          {group.directory && (
            <p className="m-0 truncate px-1.5 pt-1 pb-0.5 font-mono text-[11px] text-fg-subtle">
              {group.directory}/
            </p>
          )}
          {list(group.files, (f) => f.path.slice(group.directory ? group.directory.length + 1 : 0))}
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
    <div className="flex flex-col">
      <header className="flex flex-wrap items-center gap-x-3 gap-y-2 rounded-t-lg border border-line bg-surface px-3 py-2">
        <span className="flex min-w-0 items-center gap-2">
          <KindLetter kind={file.kind} />
          <strong className="min-w-0 font-mono text-mono font-medium [overflow-wrap:anywhere]">
            {file.oldPath ? `${file.oldPath} → ${file.path}` : file.path}
          </strong>
          <span className="shrink-0 text-xs whitespace-nowrap text-fg-subtle">
            {kindLabel[file.kind]} · {lineCounts(file)}
          </span>
          {flags.map((flag) => (
            <Tag key={String(flag)} className="h-[18px] px-1.5 text-[11px]">
              {flag}
            </Tag>
          ))}
        </span>
        <span className="ml-auto flex flex-wrap items-center gap-2">
          {!locked && expanded && (
            <Button
              size="sm"
              variant="ghost"
              disabled={!selection || draft !== null}
              title="Click a line (or select lines) in the diff first"
              onClick={() => setDraft(selection)}
            >
              <CommentIcon size={13} />
              {selection ? `Comment on ${lines(selection)}` : 'Comment on a line'}
            </Button>
          )}
          <label className="flex cursor-pointer items-center gap-1.5 text-sm text-fg-2">
            <input
              type="checkbox"
              className="size-3.5 cursor-pointer accent-(--c-ok-solid)"
              checked={file.viewed}
              onChange={onToggleViewed}
            />
            Viewed
          </label>
          <Segmented
            label="Diff layout"
            size="sm"
            value={mode}
            onChange={onMode}
            options={[
              { value: 'side-by-side', label: 'Side by side' },
              { value: 'inline', label: 'Inline' },
            ]}
          />
        </span>
      </header>
      {listed.length > 0 && (
        <details className="group border-x border-line bg-bg">
          <summary className="flex cursor-pointer list-none items-center gap-1.5 px-3 py-2 text-xs font-medium text-fg-muted hover:text-fg [&::-webkit-details-marker]:hidden">
            <ChevronRightIcon size={12} className="transition-transform group-open:rotate-90" />
            {listed.length} thread{listed.length === 1 ? '' : 's'} on other versions of this file
          </summary>
          <div className="flex flex-col gap-2 px-3 pb-3">
            {listed.map((t) => (
              <ThreadView key={t.id} thread={t} threads={mutations} locked={locked} showPlace />
            ))}
          </div>
        </details>
      )}
      {!expanded ? (
        <PaneNote>
          <p className="m-0">
            {file.binary
              ? 'A binary file.'
              : file.large
                ? 'A large change; it is not shown by default.'
                : 'A generated or lock file; it is not shown by default.'}
          </p>
          <div>
            <Button size="sm" onClick={() => setExpanded(true)}>
              Show diff
            </Button>
          </div>
        </PaneNote>
      ) : contents.error ? (
        <PaneNote>
          <ErrorText>{contents.error.message}</ErrorText>
        </PaneNote>
      ) : !contents.data ? (
        <PaneNote>
          <Loading label="Loading the file…" />
        </PaneNote>
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

/** Text in place of the diff, under the file header. */
function PaneNote({ children }: { children: ReactNode }) {
  return (
    <div className="flex flex-col gap-3 rounded-b-lg border border-t-0 border-line bg-surface px-4 py-6 text-sm text-fg-muted">
      {children}
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
    return (
      <PaneNote>
        <p className="m-0">Binary file ({sizes}); it cannot be shown as text.</p>
      </PaneNote>
    )
  }
  if (sides.some((s) => s.tooLarge)) {
    return (
      <PaneNote>
        <p className="m-0">
          Too large to show here ({sizes}). Fetch the run branch from the hub to read it.
        </p>
      </PaneNote>
    )
  }
  return (
    <Suspense
      fallback={
        <PaneNote>
          <Loading label="Loading the diff…" />
        </PaneNote>
      }
    >
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

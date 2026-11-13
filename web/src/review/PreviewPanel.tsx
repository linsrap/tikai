import { useQuery } from '@tanstack/react-query'
import { useEffect, useRef, useState } from 'react'
import { Button } from '../ui/Button'
import { ErrorText, Hint } from '../ui/form'
import { StopIcon, TerminalIcon } from '../ui/icons'
import { Segmented } from '../ui/Segmented'
import { Tag } from '../ui/Tag'
import {
  appsLine,
  canStart,
  changeSetPreviewQuery,
  exampleApps,
  exampleDefinition,
  filterLog,
  isActive,
  logFilterOptions,
  previewLine,
  previewLogQuery,
  previewStateLabel,
  previewStateTone,
  useOpenPreview,
  useStartPreview,
  useStopPreview,
  type Preview,
} from './preview'
import type { ChangeSet } from './review'

/**
 * Start the latest round in a preview environment and open it at its own address. The repository's
 * tikai-preview.yml says how; without one the panel shows an example and points to trying it locally.
 */
export function PreviewPanel({
  changeSet,
  onTryLocally,
}: {
  changeSet: ChangeSet
  onTryLocally: () => void
}) {
  const query = useQuery(changeSetPreviewQuery(changeSet.id))
  const start = useStartPreview(changeSet.id)
  const info = query.data
  const preview = info?.preview ?? null
  const error = start.error ?? query.error

  return (
    <section
      aria-label="Preview"
      className="mt-3 flex flex-col gap-2.5 rounded-md border border-line-soft bg-bg px-4 py-3"
    >
      {!info && !query.error && <Hint>Loading…</Hint>}
      {info && !info.available && <Hint>{info.unavailableReason}</Hint>}
      {info?.available && !info.definition.found && (
        <div className="flex flex-col gap-2">
          <Hint>
            Round {info.definition.roundNo} has no{' '}
            <code className="font-mono text-mono">tikai-preview.yml</code> at the repository root,
            so it cannot be previewed here. The agent can add one; it says how to start the app in
            stock images, for example:
          </Hint>
          <pre className="m-0 overflow-x-auto rounded bg-surface px-3 py-2 font-mono text-mono">
            {exampleDefinition}
          </pre>
          <Hint>
            Several processes, such as a frontend dev server and an API, go under{' '}
            <code className="font-mono text-mono">apps</code>, each on its own path prefix of the
            preview’s address (the longest prefix wins, the path reaches the app unchanged):
          </Hint>
          <pre className="m-0 overflow-x-auto rounded bg-surface px-3 py-2 font-mono text-mono">
            {exampleApps}
          </pre>
          <div>
            <Button variant="ghost" size="sm" onClick={onTryLocally}>
              Try it locally instead
            </Button>
          </div>
        </div>
      )}
      {info?.available && info.definition.errors.length > 0 && (
        <div className="flex flex-col gap-1">
          <ErrorText>Round {info.definition.roundNo}’s tikai-preview.yml cannot be used:</ErrorText>
          <ul className="m-0 flex list-disc flex-col gap-0.5 pl-5 font-mono text-mono text-bad">
            {info.definition.errors.map((e) => (
              <li key={e}>{e}</li>
            ))}
          </ul>
        </div>
      )}
      {info && (preview || canStart(info, changeSet.status === 'open')) && (
        <PreviewControls
          preview={preview}
          stopKey={['change-set-preview', changeSet.id]}
          start={
            canStart(info, changeSet.status === 'open')
              ? {
                  label:
                    preview && isActive(preview.state)
                      ? `Preview round ${info.definition.roundNo} instead`
                      : 'Start preview',
                  pending: start.isPending,
                  onStart: () => start.mutate(),
                }
              : null
          }
        />
      )}
      {error && <ErrorText>{error.message}</ErrorText>}
    </section>
  )
}

/**
 * A preview's state with its buttons (open, stop, start), the apps it serves and its log; shared by the review screen
 * and the project page. start is null when nothing can be started now.
 */
export function PreviewControls({
  preview,
  stopKey,
  start,
}: {
  preview: Preview | null
  stopKey: unknown[]
  start: { label: string; pending: boolean; onStart: () => void } | null
}) {
  const stop = useStopPreview(stopKey)
  const open = useOpenPreview()
  const error = stop.error ?? open.error
  return (
    <>
      <div className="flex flex-wrap items-center gap-2">
        {preview && (
          <Tag tone={previewStateTone[preview.state]}>{previewStateLabel[preview.state]}</Tag>
        )}
        {preview && (
          <span className="text-sm text-fg-muted" role="status">
            {previewLine(preview)}
          </span>
        )}
        <span className="ml-auto flex flex-wrap items-center gap-2">
          {preview?.state === 'running' && (
            <Button
              variant="primary"
              size="sm"
              disabled={open.isPending}
              onClick={() => open.mutate(preview.id)}
            >
              Open preview
            </Button>
          )}
          {preview && isActive(preview.state) && (
            <Button
              variant="danger"
              size="sm"
              disabled={stop.isPending}
              onClick={() => stop.mutate(preview.id)}
            >
              <StopIcon size={13} />
              Stop
            </Button>
          )}
          {start && (
            <Button
              variant={preview?.state === 'running' ? 'secondary' : 'primary'}
              size="sm"
              disabled={start.pending}
              onClick={start.onStart}
            >
              <TerminalIcon size={13} />
              {start.label}
            </Button>
          )}
        </span>
      </div>
      {preview && isActive(preview.state) && appsLine(preview.apps) && (
        <span className="text-sm text-fg-muted">{appsLine(preview.apps)}</span>
      )}
      {preview && preview.lastLogSeq > 0 && <PreviewLog preview={preview} />}
      {error && <ErrorText>{error.message}</ErrorText>}
    </>
  )
}

/**
 * The latest lines of the preview's log (clone, setup output, the apps), following new lines while it is shown. With
 * several apps, a filter shows one app's lines with the hub's notes.
 */
function PreviewLog({ preview }: { preview: Preview }) {
  const log = useQuery(previewLogQuery(preview))
  const box = useRef<HTMLDivElement>(null)
  const [filter, setFilter] = useState('all')
  const options = logFilterOptions(preview.apps)
  const lines = filterLog(log.data ?? [], options.some((o) => o.value === filter) ? filter : 'all')
  const last = lines.at(-1)?.seq
  // Follows the end of the log unless the reader scrolled up.
  useEffect(() => {
    const element = box.current
    if (element && element.scrollHeight - element.scrollTop - element.clientHeight < 80) {
      element.scrollTop = element.scrollHeight
    }
  }, [last])
  return (
    <details open={isActive(preview.state) || preview.state === 'failed'}>
      <summary className="cursor-pointer text-sm text-fg-muted select-none">Preview log</summary>
      {options.length > 0 && (
        <Segmented
          label="Log of"
          size="sm"
          className="mt-1.5"
          value={filter}
          options={options}
          onChange={setFilter}
        />
      )}
      <div
        ref={box}
        role="log"
        aria-label="Preview log"
        className="mt-1.5 max-h-72 overflow-auto rounded bg-surface px-3 py-2 font-mono text-mono"
      >
        {lines.map((line) => (
          <div key={line.seq} className="flex gap-2 whitespace-pre-wrap">
            <span className="min-w-[8ch] shrink-0 text-fg-subtle">{line.source}</span>
            <span className="min-w-0 break-words">{line.text}</span>
          </div>
        ))}
      </div>
      {log.error && <ErrorText>{log.error.message}</ErrorText>}
    </details>
  )
}

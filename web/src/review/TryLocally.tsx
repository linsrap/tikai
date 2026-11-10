import { useState } from 'react'
import { buttonClass } from '../ui/buttonClass'
import { Button } from '../ui/Button'
import { Hint } from '../ui/form'
import { CheckIcon, DownloadIcon } from '../ui/icons'
import { tryLocally, type ChangeSet } from './review'

/** The round as a bundle download, and the commands that check it out next to the person's own clone. */
export function TryLocally({ changeSet, roundNo }: { changeSet: ChangeSet; roundNo: number }) {
  const tried = tryLocally(changeSet, roundNo)
  return (
    <section
      aria-label="Try it locally"
      className="mt-3 flex flex-col gap-2 rounded-md border border-line-soft bg-bg px-4 py-3"
    >
      <div className="flex flex-wrap items-center gap-3">
        <a className={buttonClass('secondary', 'sm')} href={tried.url} download={tried.fileName}>
          <DownloadIcon size={13} />
          Download round {roundNo}
        </a>
        <Hint>Then, in your clone of {changeSet.repositoryName}:</Hint>
      </div>
      <ol className="m-0 flex list-none flex-col gap-1.5 p-0">
        {tried.commands.map((command) => (
          <li key={command} className="flex items-center gap-2">
            <code className="min-w-0 flex-1 overflow-x-auto rounded bg-surface px-2 py-1 font-mono text-mono whitespace-nowrap">
              {command}
            </code>
            <CopyButton text={command} />
          </li>
        ))}
      </ol>
      <Hint>
        The worktree is a second folder on its own branch; your checkout stays as it is. Remove it
        with <code className="font-mono text-mono">git worktree remove</code>.
      </Hint>
    </section>
  )
}

function CopyButton({ text }: { text: string }) {
  const [copied, setCopied] = useState(false)
  return (
    <Button
      variant="ghost"
      size="sm"
      onClick={() => {
        void navigator.clipboard.writeText(text).then(() => {
          setCopied(true)
          setTimeout(() => setCopied(false), 1500)
        })
      }}
    >
      {copied ? <CheckIcon size={13} className="text-ok" /> : null}
      {copied ? 'Copied' : 'Copy'}
    </Button>
  )
}

import { useQuery } from '@tanstack/react-query'
import { PreviewControls } from '../review/PreviewPanel'
import {
  canStartBranch,
  isActive,
  repositoryPreviewQuery,
  shortSha,
  useStartBranchPreview,
} from '../review/preview'
import { ErrorText, Hint } from '../ui/form'
import { repositoriesQuery, type RepositoryInfo } from './projects'

/**
 * Previews of the project's current state: each ready repository's default branch at its head, e.g. once every ticket
 * is done. Repositories without a tikai-preview.yml (and no earlier branch preview) are left out, so projects that do
 * not use previews see nothing here.
 */
export function BranchPreviews({ slug }: { slug: string }) {
  const repositories = useQuery(repositoriesQuery(slug))
  const ready = repositories.data?.filter((r) => r.status === 'ready') ?? []
  return (
    <>
      {ready.map((r) => (
        <BranchPreview key={r.id} slug={slug} repository={r} named={ready.length > 1} />
      ))}
    </>
  )
}

function BranchPreview({
  slug,
  repository,
  named,
}: {
  slug: string
  repository: RepositoryInfo
  named: boolean
}) {
  const query = useQuery(repositoryPreviewQuery(slug, repository.id))
  const start = useStartBranchPreview(slug, repository.id)
  const info = query.data
  if (query.error) {
    return <ErrorText>{query.error.message}</ErrorText>
  }
  if (!info || (!info.found && !info.preview)) {
    return null
  }
  const preview = info.preview
  const where = named ? `${repository.name}: ${info.branch}` : info.branch
  return (
    <section
      aria-label={`Preview of ${where}`}
      className="mb-4 flex flex-col gap-2.5 rounded-md border border-line-soft bg-bg px-4 py-3"
    >
      {!preview && (
        <span className="text-sm text-fg-muted">
          Preview {where}
          {info.headSha && ` at ${shortSha(info.headSha)}`}: the app as it is now, with every merged
          change. It stops when {info.branch} moves.
        </span>
      )}
      {!info.available && <Hint>{info.unavailableReason}</Hint>}
      {info.available && info.errors.length > 0 && (
        <div className="flex flex-col gap-1">
          <ErrorText>The tikai-preview.yml on {info.branch} cannot be used:</ErrorText>
          <ul className="m-0 flex list-disc flex-col gap-0.5 pl-5 font-mono text-mono text-bad">
            {info.errors.map((e) => (
              <li key={e}>{e}</li>
            ))}
          </ul>
        </div>
      )}
      {info.available && !info.found && (
        <Hint>
          {info.branch} has no <code className="font-mono text-mono">tikai-preview.yml</code> at the
          repository root any more, so it cannot be previewed.
        </Hint>
      )}
      <PreviewControls
        preview={preview}
        stopKey={['repository-preview', repository.id]}
        start={
          canStartBranch(info)
            ? {
                label:
                  preview && isActive(preview.state)
                    ? `Preview ${info.branch} at ${shortSha(info.headSha ?? '')} instead`
                    : `Preview ${info.branch}`,
                pending: start.isPending,
                onStart: () => start.mutate(),
              }
            : null
        }
      />
      {start.error && <ErrorText>{start.error.message}</ErrorText>}
    </section>
  )
}

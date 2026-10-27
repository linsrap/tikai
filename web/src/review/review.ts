import { queryOptions, useMutation, useQueryClient } from '@tanstack/react-query'
import { api, ensureAntiforgery } from '../api/client'
import type { components } from '../api/schema'
import { unwrap } from '../api/result'

export type ChangeSet = components['schemas']['ChangeSetInfo']
export type ReviewRound = components['schemas']['ReviewRoundInfo']
export type ReviewFiles = components['schemas']['ReviewFilesInfo']
export type ReviewFile = components['schemas']['ReviewFileInfo']
export type FileContents = components['schemas']['FileContentsInfo']
export type FileSide = components['schemas']['FileSide']
export type ChangeKind = components['schemas']['GitChangeKind']
export type Verdict = NonNullable<components['schemas']['ReviewVerdict']>

export const kindLetter: Record<ChangeKind, string> = {
  added: 'A',
  modified: 'M',
  deleted: 'D',
  renamed: 'R',
  type_changed: 'T',
}

export const kindLabel: Record<ChangeKind, string> = {
  added: 'added',
  modified: 'modified',
  deleted: 'deleted',
  renamed: 'renamed',
  type_changed: 'type changed',
}

/** Files a reviewer skims: lock files and generated output. They come last, collapsed. */
export const isSkimmed = (file: Pick<ReviewFile, 'lock' | 'generated'>) =>
  file.lock || file.generated

/** The diff is not shown until asked for: skimmed, large or binary files. */
export const startsCollapsed = (
  file: Pick<ReviewFile, 'lock' | 'generated' | 'large' | 'binary'>,
) => isSkimmed(file) || file.large || file.binary

export type DirectoryGroup = { directory: string; files: ReviewFile[] }

/** The file tree: ordinary files by directory (root first, then by path), and the skimmed ones apart. */
export function groupFiles(files: readonly ReviewFile[]): {
  groups: DirectoryGroup[]
  skimmed: ReviewFile[]
} {
  const byPath = (a: ReviewFile, b: ReviewFile) => (a.path < b.path ? -1 : a.path > b.path ? 1 : 0)
  const groups = new Map<string, ReviewFile[]>()
  for (const file of [...files].filter((f) => !isSkimmed(f)).sort(byPath)) {
    const slash = file.path.lastIndexOf('/')
    const directory = slash < 0 ? '' : file.path.slice(0, slash)
    groups.set(directory, [...(groups.get(directory) ?? []), file])
  }
  return {
    groups: [...groups.entries()]
      .sort(([a], [b]) => (a === '' ? -1 : b === '' ? 1 : a < b ? -1 : a > b ? 1 : 0))
      .map(([directory, list]) => ({ directory, files: list })),
    skimmed: [...files].filter(isSkimmed).sort(byPath),
  }
}

/** The order j/k move through: the tree from top to bottom. */
export function fileOrder(files: readonly ReviewFile[]): string[] {
  const { groups, skimmed } = groupFiles(files)
  return [...groups.flatMap((g) => g.files), ...skimmed].map((f) => f.path)
}

/** The next file in the order (wrapping), or the previous one with step -1. */
export function stepFile(
  order: readonly string[],
  current: string | null,
  step: 1 | -1,
): string | null {
  if (order.length === 0) return null
  const index = current === null ? -1 : order.indexOf(current)
  if (index < 0) return order[step === 1 ? 0 : order.length - 1]
  return order[(index + step + order.length) % order.length]
}

/** The first unviewed file after the current one (wrapping); null once every file is viewed. */
export function nextUnviewed(
  files: readonly ReviewFile[],
  current: string | null,
  viewedNow: ReadonlySet<string> = new Set(),
): string | null {
  const order = fileOrder(files)
  const viewed = new Set([...files.filter((f) => f.viewed).map((f) => f.path), ...viewedNow])
  const start = current === null ? 0 : order.indexOf(current) + 1
  for (let i = 0; i < order.length; i++) {
    const path = order[(start + i) % order.length]
    if (!viewed.has(path) && path !== current) return path
  }
  return null
}

/** "+12 −3", or "binary". */
export const lineCounts = (file: Pick<ReviewFile, 'additions' | 'deletions'>) =>
  file.additions == null ? 'binary' : `+${file.additions} −${file.deletions ?? 0}`

/** "1.2 MB", "340 KB", "12 bytes". */
export function sizeText(bytes: number): string {
  if (bytes >= 1024 * 1024) return `${(bytes / (1024 * 1024)).toFixed(1)} MB`
  if (bytes >= 1024) return `${Math.round(bytes / 1024)} KB`
  return `${bytes} bytes`
}

export const shortSha = (sha: string) => sha.slice(0, 8)

export type DiffMode = 'side-by-side' | 'inline'

const diffModeKey = 'tikai.review.diffMode'

/** The reviewer's last choice, per browser; side by side when nothing (or no storage) is there. */
export function loadDiffMode(
  storage: Pick<Storage, 'getItem'> | undefined = globalThis.localStorage,
): DiffMode {
  try {
    return storage?.getItem(diffModeKey) === 'inline' ? 'inline' : 'side-by-side'
  } catch {
    return 'side-by-side'
  }
}

export function saveDiffMode(
  mode: DiffMode,
  storage: Pick<Storage, 'setItem'> | undefined = globalThis.localStorage,
) {
  try {
    storage?.setItem(diffModeKey, mode)
  } catch {
    // Private windows and blocked storage: the choice lasts for this page only.
  }
}

export const changeSetQuery = (id: string) =>
  queryOptions({
    queryKey: ['change-sets', id],
    queryFn: () =>
      unwrap(api.GET('/api/change-sets/{id}', { params: { path: { id } } }), 'load the review'),
  })

// Files and contents have keys of their own: live updates re-read ['change-sets', …] often (run changes), and a
// round's files never change; only the viewer's own clicks change their viewed state.
export const reviewFilesQuery = (id: string, round: number) =>
  queryOptions({
    queryKey: ['change-set-files', id, round],
    queryFn: () =>
      unwrap(
        api.GET('/api/change-sets/{id}/files', { params: { path: { id }, query: { round } } }),
        'load the changed files',
      ),
  })

/** A round's file never changes: its content is read once. */
export const reviewFileQuery = (id: string, round: number, path: string) =>
  queryOptions({
    queryKey: ['change-set-file', id, round, path],
    queryFn: () =>
      unwrap(
        api.GET('/api/change-sets/{id}/file', { params: { path: { id }, query: { round, path } } }),
        'load the file',
      ),
    staleTime: Infinity,
  })

/** Marks a file viewed (or not) for the current user; the list shows it at once. */
export function useViewed(id: string, round: number) {
  const queryClient = useQueryClient()
  const key = reviewFilesQuery(id, round).queryKey
  return useMutation({
    mutationFn: async ({ path, viewed }: { path: string; viewed: boolean }) => {
      await ensureAntiforgery()
      return unwrap(
        api.PUT('/api/change-sets/{id}/viewed', {
          params: { path: { id } },
          body: { path, viewed, round },
        }),
        'save the viewed state',
      )
    },
    onMutate: ({ path, viewed }) => {
      queryClient.setQueryData(key, (old) =>
        old
          ? { ...old, files: old.files.map((f) => (f.path === path ? { ...f, viewed } : f)) }
          : old,
      )
    },
    onSettled: () => void queryClient.invalidateQueries({ queryKey: key }),
  })
}

export function useVerdict(id: string) {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: async (verdict: Verdict) => {
      await ensureAntiforgery()
      return unwrap(
        api.POST('/api/change-sets/{id}/verdict', { params: { path: { id } }, body: { verdict } }),
        'give the verdict',
      )
    },
    onSettled: () => {
      void queryClient.invalidateQueries({ queryKey: ['change-sets', id] })
      void queryClient.invalidateQueries({ queryKey: ['runs'] })
      void queryClient.invalidateQueries({ queryKey: ['attention-items'] })
      void queryClient.invalidateQueries({ queryKey: ['projects'] })
    },
  })
}

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
export type Analysis = components['schemas']['AnalysisInfo']
export type ReviewAnalysis = components['schemas']['ReviewAnalysis']
export type AnalysisCoverage = components['schemas']['AnalysisCoverage']
export type CodeRef = components['schemas']['CodeRef']
export type StoryLayer = components['schemas']['StoryLayer']
export type Coverage = components['schemas']['CriterionCoverage']

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

export type StoryFileGroup = {
  title: string
  layer: StoryLayer
  note: string
  files: ReviewFile[]
}

/**
 * The file list in the analysis's reading order: its groups with their files, then any file it did not place, and
 * the skimmed files apart as in the path order.
 */
export function storyFiles(
  files: readonly ReviewFile[],
  analysis: Pick<ReviewAnalysis, 'groups'>,
): { groups: StoryFileGroup[]; skimmed: ReviewFile[] } {
  const byPath = new Map(files.filter((f) => !isSkimmed(f)).map((f) => [f.path, f]))
  const groups: StoryFileGroup[] = []
  for (const group of analysis.groups) {
    const list = group.files.flatMap((path) => {
      const file = byPath.get(path)
      byPath.delete(path)
      return file ? [file] : []
    })
    if (list.length > 0) groups.push({ ...group, files: list })
  }
  const rest = [...byPath.values()].sort((a, b) => (a.path < b.path ? -1 : 1))
  if (rest.length > 0)
    groups.push({ title: 'Other changes', layer: 'other', note: '', files: rest })
  return { groups, skimmed: groupFiles(files).skimmed }
}

export type FileOrderMode = 'story' | 'path'

/** The order j/k move through: the list from top to bottom, by story when an analysis gives one. */
export function fileOrder(
  files: readonly ReviewFile[],
  story: Pick<ReviewAnalysis, 'groups'> | null = null,
): string[] {
  const { groups, skimmed } = story ? storyFiles(files, story) : groupFiles(files)
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
  order: readonly string[] = fileOrder(files),
): string | null {
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

const orderKey = 'tikai.review.order'

/** Story order unless the reviewer chose path order in this browser. */
export function loadOrderMode(
  storage: Pick<Storage, 'getItem'> | undefined = globalThis.localStorage,
): FileOrderMode {
  try {
    return storage?.getItem(orderKey) === 'path' ? 'path' : 'story'
  } catch {
    return 'story'
  }
}

export function saveOrderMode(
  mode: FileOrderMode,
  storage: Pick<Storage, 'setItem'> | undefined = globalThis.localStorage,
) {
  try {
    storage?.setItem(orderKey, mode)
  } catch {
    // The choice lasts for this page only.
  }
}

/** A file's risk from the analysis; only medium and high get a badge. */
export function riskOf(analysis: Pick<ReviewAnalysis, 'risks'> | null | undefined, path: string) {
  const risk = analysis?.risks.find((r) => r.path === path)
  return risk && risk.risk !== 'low' ? risk : null
}

export type CoverageRow = {
  criterionId: string
  text: string
  /** What the agent claims; null when it did not say. */
  claimed: Coverage | null
  claimedNote: string | null
  /** What the analysis found; null when it did not assess the criterion. */
  found: AnalysisCoverage | null
  /** Agent and analysis both rated it, differently. */
  disagree: boolean
}

/** One row per ticket criterion, with the agent's claim next to the analysis. */
export function coverageRows(
  criteria: readonly { id: string; text: string }[],
  claims: readonly { criterionId: string; status: Coverage; note: string | null }[],
  analysis: Pick<ReviewAnalysis, 'coverage'> | null,
): CoverageRow[] {
  return criteria.map((criterion) => {
    const claim = claims.find((c) => c.criterionId === criterion.id) ?? null
    const found = analysis?.coverage.find((c) => c.criterionId === criterion.id) ?? null
    return {
      criterionId: criterion.id,
      text: criterion.text,
      claimed: claim?.status ?? null,
      claimedNote: claim?.note ?? null,
      found,
      disagree: !!claim && !!found && claim.status !== found.status,
    }
  })
}

/** "src/a.cs:3–9", or "src/a.cs:3" for one line. */
export const refText = (ref: CodeRef) =>
  `${ref.path}:${ref.lineStart}${ref.lineEnd > ref.lineStart ? `–${ref.lineEnd}` : ''}`

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

/** Analyses the round again; the review shows "Analysing…" until the job is done. */
export function useRetryAnalysis(id: string, roundNo: number) {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: async () => {
      await ensureAntiforgery()
      return unwrap(
        api.POST('/api/change-sets/{id}/rounds/{roundNo}/analysis', {
          params: { path: { id, roundNo } },
        }),
        'start the analysis',
      )
    },
    onSuccess: (changeSet) => queryClient.setQueryData(changeSetQuery(id).queryKey, changeSet),
  })
}

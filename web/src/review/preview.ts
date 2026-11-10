import { queryOptions, useMutation, useQueryClient } from '@tanstack/react-query'
import { api, ensureAntiforgery } from '../api/client'
import { unwrap } from '../api/result'
import type { components } from '../api/schema'
import type { Tone } from '../ui/Tag'

export type ChangeSetPreview = components['schemas']['ChangeSetPreviewInfo']
export type Preview = components['schemas']['PreviewInfo']
export type PreviewState = Preview['state']
export type PreviewLogLine = components['schemas']['PreviewLogLineInfo']

/** The log lines shown at most (the latest). */
export const shownLogLines = 500

export const changeSetPreviewQuery = (changeSetId: string) =>
  queryOptions({
    queryKey: ['change-set-preview', changeSetId],
    queryFn: () =>
      unwrap(
        api.GET('/api/change-sets/{id}/preview', { params: { path: { id: changeSetId } } }),
        'load the preview',
      ),
  })

/** The latest lines of the log; the key changes with every new line, so live updates read only what is shown. */
export const previewLogQuery = (preview: Pick<Preview, 'id' | 'lastLogSeq'>) =>
  queryOptions({
    queryKey: ['preview-log', preview.id, preview.lastLogSeq],
    queryFn: () =>
      unwrap(
        api.GET('/api/previews/{id}/log', {
          params: {
            path: { id: preview.id },
            query: { afterSeq: logStart(preview.lastLogSeq), limit: shownLogLines },
          },
        }),
        'load the preview log',
      ),
    placeholderData: (previous) => previous,
  })

/** The seq after which the shown lines start. */
export const logStart = (lastLogSeq: number) => Math.max(0, lastLogSeq - shownLogLines)

export const isActive = (state: PreviewState) =>
  state === 'preparing' || state === 'starting' || state === 'running'

export const previewStateLabel: Record<PreviewState, string> = {
  preparing: 'Preparing',
  starting: 'Starting',
  running: 'Running',
  stopped: 'Stopped',
  failed: 'Failed',
}

/** Blue while it gets ready, green once it can be opened, red when it failed. */
export const previewStateTone: Record<PreviewState, Tone> = {
  preparing: 'run',
  starting: 'run',
  running: 'ok',
  stopped: 'neutral',
  failed: 'bad',
}

/** One line about the latest preview, for people. */
export function previewLine(preview: Preview): string {
  switch (preview.state) {
    case 'preparing':
      return `Cloning round ${preview.roundNo} and running its setup…`
    case 'starting':
      return `Starting round ${preview.roundNo}…`
    case 'running':
      return `Round ${preview.roundNo} runs. It stops after ${preview.idleMinutes} minutes without use.`
    case 'stopped':
      return `The preview of round ${preview.roundNo} stopped. ${preview.stopReason ?? ''}`.trim()
    case 'failed':
      return `The preview of round ${preview.roundNo} failed: ${preview.error ?? 'no reason given'}`
  }
}

/** A start for the latest round, unless one runs for it already. */
export function canStart(info: ChangeSetPreview, open: boolean): boolean {
  return (
    open &&
    info.available &&
    info.definition.found &&
    info.definition.errors.length === 0 &&
    !(
      info.preview &&
      isActive(info.preview.state) &&
      info.preview.roundNo === info.definition.roundNo
    )
  )
}

/** What a tikai-preview.yml looks like, for rounds without one. */
export const exampleDefinition = `setup:
  - image: node:24-trixie-slim
    run: npm ci && npm run build
app:
  image: node:24-trixie-slim
  command: npm start
  port: 3000
  healthcheck: { path: /health }
services:
  db:
    image: postgres:17
    environment: { POSTGRES_PASSWORD: preview }
    healthcheck: { command: pg_isready -U postgres }`

export function useStartPreview(changeSetId: string) {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: async () => {
      await ensureAntiforgery()
      return unwrap(
        api.POST('/api/change-sets/{id}/previews', { params: { path: { id: changeSetId } } }),
        'start the preview',
      )
    },
    onSettled: () =>
      void queryClient.invalidateQueries({ queryKey: ['change-set-preview', changeSetId] }),
  })
}

export function useStopPreview(changeSetId: string) {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: async (previewId: string) => {
      await ensureAntiforgery()
      return unwrap(
        api.POST('/api/previews/{id}/stop', { params: { path: { id: previewId } } }),
        'stop the preview',
      )
    },
    onSettled: () =>
      void queryClient.invalidateQueries({ queryKey: ['change-set-preview', changeSetId] }),
  })
}

/**
 * Opens the preview in a new tab through a one-time entry link (the hub's login does not reach the preview's own
 * host). The tab is opened at once, inside the click, so no popup blocker stops it; the link follows.
 */
export function useOpenPreview() {
  return useMutation({
    mutationFn: async (previewId: string) => {
      const tab = window.open('about:blank', '_blank')
      try {
        await ensureAntiforgery()
        const entry = await unwrap(
          api.POST('/api/previews/{id}/entry', { params: { path: { id: previewId } } }),
          'open the preview',
        )
        if (tab) {
          tab.opener = null
          tab.location.href = entry.url
        } else {
          window.location.href = entry.url
        }
      } catch (error) {
        tab?.close()
        throw error
      }
    },
  })
}

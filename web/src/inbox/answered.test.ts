import { afterEach, describe, expect, it } from 'vitest'
import { isWorking } from '../runs/runs'
import { continuedFor, continuedLine, noteAnswered, resetAnswered, useAnswered } from './answered'
import { inboxSearch } from './inbox'
import { renderHook, act } from '@testing-library/react'

const at = 1_000_000
const run = (
  state: Parameters<typeof continuedLine>[0]['state'],
  agentStopped = false,
  waiting = 0,
) => ({
  state,
  agentStopped,
  waiting: Array.from({ length: waiting }, (_, i) => ({
    id: String(i),
    kind: 'question' as const,
    question: null,
    plan: null,
    createdAt: '2026-11-25T10:00:00Z',
  })),
})

describe('agents working on the inbox page', () => {
  afterEach(resetAnswered)

  it('lists runs that work or are about to, not those in review or ended', () => {
    expect(
      ['queued', 'preparing', 'running', 'blocked', 'changes_requested'].every((s) =>
        isWorking(s as never),
      ),
    ).toBe(true)
    expect(
      ['in_review', 'merged', 'pushed', 'failed', 'cancelled'].some((s) => isWorking(s as never)),
    ).toBe(false)
  })

  it('says a run continued after an answer from here', () => {
    expect(continuedLine(run('running'), at, at + 1000)).toBe(
      'Your answer was delivered · running again',
    )
    expect(continuedLine(run('preparing'), at, at)).toBe('Resuming in a new container…')
    // A stopped agent with everything answered is resumed in a new container.
    expect(continuedLine(run('blocked', true, 0), at, at)).toBe('Resuming in a new container…')
  })

  it('says nothing without an answer, while it still waits, or after two minutes', () => {
    expect(continuedLine(run('running'), undefined, at)).toBeNull()
    expect(continuedLine(run('blocked', false, 1), at, at)).toBeNull()
    expect(continuedLine(run('blocked', true, 1), at, at)).toBeNull()
    expect(continuedLine(run('running'), at, at + continuedFor + 1)).toBeNull()
    expect(continuedLine(run('in_review'), at, at)).toBeNull()
  })

  it('counts each answer, so the page reacts to every one', () => {
    const { result } = renderHook(() => useAnswered())
    expect(result.current.last).toBeNull()
    act(() => noteAnswered('run-1', at))
    act(() => noteAnswered('run-1', at + 5))
    expect(result.current.last).toEqual({ runId: 'run-1', n: 2 })
    expect(result.current.runs.get('run-1')).toBe(at + 5)
  })

  it('keeps only a run id in the search', () => {
    const id = '0193a5b2-7c4d-7e8f-9a0b-1c2d3e4f5a6b'
    expect(inboxSearch({ log: id })).toEqual({ log: id })
    expect(inboxSearch({ log: 'nope' })).toEqual({})
    expect(inboxSearch({ log: 5, other: 'x' })).toEqual({})
  })
})

import { describe, expect, it } from 'vitest'
import {
  blockedNote,
  eventSummary,
  isActive,
  isMoving,
  pollInterval,
  resumeLabel,
  toStartRunRequest,
  waitingText,
  type RunEventInfo,
  type WaitingItem,
} from './runs'

const event = (kind: RunEventInfo['kind'], payload: Record<string, unknown>): RunEventInfo => ({
  seq: 1,
  kind,
  payload: payload as RunEventInfo['payload'],
  createdAt: '2026-10-26T12:00:00Z',
})

describe('run states', () => {
  it('polls only while the run moves by itself', () => {
    expect(isMoving('running')).toBe(true)
    expect(isMoving('queued')).toBe(true)
    expect(isMoving('blocked')).toBe(false)
    expect(isMoving('in_review')).toBe(false)
  })

  it('can cancel everything that has not ended', () => {
    expect(isActive('blocked')).toBe(true)
    expect(isActive('in_review')).toBe(true)
    expect(isActive('failed')).toBe(false)
    expect(isActive('cancelled')).toBe(false)
    expect(isActive('merged')).toBe(false)
  })
})

describe('toStartRunRequest', () => {
  it('sends only what was chosen', () => {
    expect(toStartRunRequest(null, null)).toEqual({ repositoryId: null, providerId: null })
    expect(toStartRunRequest('r1', 'p1')).toEqual({ repositoryId: 'r1', providerId: 'p1' })
  })
})

describe('eventSummary', () => {
  it('describes the agent start with its MCP status', () => {
    expect(
      eventSummary(
        event('system', {
          model: 'claude-opus-5-5',
          claude_code_version: '2.1.310',
          mcp_servers: [{ name: 'tikai', status: 'connected' }],
        }),
      ),
    ).toBe('Started (claude-opus-5-5, Claude Code 2.1.310); MCP: tikai connected')
  })

  it('shows tool calls, results, progress and the hub notes in one line each', () => {
    expect(eventSummary(event('tool_call', { name: 'Bash', input: { command: 'ls' } }))).toBe(
      'Bash {"command":"ls"}',
    )
    expect(eventSummary(event('tool_result', { content: 'a.txt', is_error: false }))).toBe(
      '→ a.txt',
    )
    expect(eventSummary(event('tool_result', { content: 'nope', is_error: true }))).toBe(
      'Error: nope',
    )
    expect(eventSummary(event('progress', { step: 'Tests', note: 'all green' }))).toBe(
      'Progress: Tests (all green)',
    )
    expect(eventSummary(event('message', { role: 'assistant', thinking: 'hmm' }))).toBe(
      '(thinking) hmm',
    )
    expect(
      eventSummary(event('result', { subtype: 'success', is_error: false, num_turns: 4 })),
    ).toBe('Result: success, 4 turns')
    expect(
      eventSummary(event('log', { source: 'hub', text: 'Run failed', reason: 'no image' })),
    ).toBe('Hub: Run failed (no image)')
    expect(eventSummary(event('log', { source: 'agent', stream: 'stderr', text: 'warn' }))).toBe(
      'stderr: warn',
    )
  })
})

describe('blocked runs', () => {
  const item = (fields: Partial<WaitingItem>): WaitingItem => ({
    id: 'i1',
    kind: 'question',
    question: null,
    plan: null,
    createdAt: '2026-10-27T10:00:00Z',
    ...fields,
  })
  const time = (iso: string) => `at ${iso}`

  it('polls a waiting run too, but less often', () => {
    expect(pollInterval('running')).toBe(3000)
    expect(pollInterval('blocked')).toBe(10000)
    expect(pollInterval('in_review')).toBe(false)
  })

  it('shows the question with its options, or the plan steps', () => {
    expect(
      waitingText(item({ question: { question: 'Which word?', context: null, options: [] } })),
    ).toBe('Question: Which word?')
    expect(
      waitingText(
        item({ question: { question: 'Colour?', context: null, options: ['red', 'blue'] } }),
      ),
    ).toBe('Question: Colour? (red / blue)')
    expect(
      waitingText(
        item({
          kind: 'plan_approval',
          plan: { steps: ['Read', 'Write'], risks: [], filesExpected: [] },
        }),
      ),
    ).toBe('Plan with 2 steps: Read; Write')
  })

  it('says when the waiting agent is stopped, and what happens once it was', () => {
    const waiting = [item({})]
    expect(
      blockedNote({ state: 'blocked', agentStopped: false, blockTimeoutAt: 'T', waiting }, time),
    ).toContain('The agent waits until at T')
    expect(
      blockedNote({ state: 'blocked', agentStopped: true, blockTimeoutAt: null, waiting }, time),
    ).toContain('stopped while it waited')
    expect(
      blockedNote(
        { state: 'blocked', agentStopped: true, blockTimeoutAt: null, waiting: [] },
        time,
      ),
    ).toContain('being resumed')
    expect(
      blockedNote(
        { state: 'running', agentStopped: false, blockTimeoutAt: null, waiting: [] },
        time,
      ),
    ).toBeNull()
  })

  it('names how the run was resumed', () => {
    expect(resumeLabel({ attempt: 1, attemptKind: 'start' })).toBeNull()
    expect(resumeLabel({ attempt: 2, attemptKind: 'resume' })).toBe(
      'Resumed 1×, last in its own session',
    )
    expect(resumeLabel({ attempt: 3, attemptKind: 'summary' })).toBe(
      'Resumed 2×, last in a fresh session with a summary',
    )
  })
})

import { describe, expect, it } from 'vitest'
import { eventSummary, isActive, isMoving, toStartRunRequest, type RunEventInfo } from './runs'

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

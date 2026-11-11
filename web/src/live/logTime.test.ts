import { logTime } from './logTime'

describe('run log', () => {
  it('writes times with 24 hours and 8 characters in any locale', () => {
    // Local times, so the test holds in every time zone.
    expect(logTime(new Date(2026, 10, 10, 17, 30, 16).toISOString())).toBe('17:30:16')
    expect(logTime(new Date(2026, 10, 10, 0, 5, 9).toISOString())).toBe('00:05:09')
  })
})

/**
 * A log time as "17:30:16": always 24 hours and 8 characters, whatever the locale (a 12-hour locale's "05:30:16 PM"
 * wrapped in the fixed column).
 */
export const logTime = (iso: string) =>
  new Date(iso).toLocaleTimeString('en-GB', {
    hour: '2-digit',
    minute: '2-digit',
    second: '2-digit',
    hourCycle: 'h23',
  })

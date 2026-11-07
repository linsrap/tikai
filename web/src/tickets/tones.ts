import type { Tone } from '../ui/Tag'
import type { TicketStatus } from './tickets'

/** Colour of a ticket status. */
export const ticketStatusTone: Record<TicketStatus, Tone> = {
  open: 'neutral',
  in_progress: 'run',
  in_review: 'wait',
  done: 'ok',
  closed: 'neutral',
}

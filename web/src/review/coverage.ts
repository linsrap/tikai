import type { ReviewCoverage } from '../inbox/inbox'
import type { Tone } from '../ui/Tag'

/** Colour of a coverage status: covered is fine, partial needs a look, missing is a problem. */
export const coverageTone: Record<ReviewCoverage['status'], Tone> = {
  covered: 'ok',
  partial: 'wait',
  missing: 'bad',
}

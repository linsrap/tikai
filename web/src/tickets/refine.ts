import {
  appendAssumptions,
  draftKey,
  type CriterionDraft,
  type SpecProposal,
  type TicketForm,
} from './tickets'

/**
 * A refine proposal written into the form as suggestions. `before` is the form as it was sent, for "Undo refine".
 * The markers stay only while the form still holds the suggested text: a field the person edits is theirs again.
 */
export type Refined = {
  before: TicketForm
  proposal: SpecProposal
  /** The suggested title, null when there is none or it was dropped. */
  title: string | null
  /** True while the description is the proposal's (plus the assumption lines). */
  description: boolean
  /** Suggested criteria by row key; `was` is the text a rewrite replaced, null for a new one. */
  criteria: readonly { key: string; was: string | null; text: string }[]
  /** Lines added under "## Assumptions": "Assumed: …" and "Decided: … → …". */
  lines: readonly string[]
}

/**
 * Writes the whole proposal into the form: title, description, rewrites (a rewritten criterion keeps its row and id),
 * new criteria, and the assumptions under "## Assumptions". `keys` are the rows the request sent, in order.
 */
export function refineForm(
  form: TicketForm,
  proposal: SpecProposal,
  keys: readonly string[],
): { form: TicketForm; refined: Refined } {
  let criteria: CriterionDraft[] = form.criteria
  const marked: Refined['criteria'][number][] = []
  for (const change of proposal.criteria) {
    const key = change.replaces != null ? keys[change.replaces] : undefined
    const row = key ? criteria.find((c) => c.key === key) : undefined
    if (row) {
      criteria = criteria.map((c) => (c.key === row.key ? { ...c, text: change.text } : c))
      marked.push({ key: row.key, was: row.text, text: change.text })
    } else {
      const added = { key: draftKey(), id: null, text: change.text }
      criteria = [...criteria, added]
      marked.push({ key: added.key, was: null, text: change.text })
    }
  }
  const lines = proposal.assumptions.map((a) => `Assumed: ${a}`)
  return {
    form: {
      ...form,
      title: proposal.title ?? form.title,
      description: appendAssumptions(proposal.description ?? form.description, lines),
      criteria,
    },
    refined: {
      before: form,
      proposal,
      title: proposal.title,
      description: proposal.description != null,
      criteria: marked,
      lines,
    },
  }
}

/** The suggested title, while the form still holds it. */
export const titleSuggestion = (form: TicketForm, refined: Refined | null) =>
  refined?.title != null && form.title === refined.title ? refined.title : null

/** The suggestion for a criterion row, while the row still holds the suggested text. */
export const criterionSuggestion = (
  refined: Refined | null,
  row: Pick<CriterionDraft, 'key' | 'text'>,
) => refined?.criteria.find((c) => c.key === row.key && c.text === row.text) ?? null

/** Assumption and decision lines that are still in the description. */
export const linesInDescription = (form: TicketForm, refined: Refined | null) =>
  refined?.lines.filter((line) => hasLine(form.description, line)) ?? []

/** Puts the title back. */
export function dropTitle(form: TicketForm, refined: Refined) {
  return { form: { ...form, title: refined.before.title }, refined: { ...refined, title: null } }
}

/** Puts the description back, keeping the assumption lines that are still there. */
export function dropDescription(form: TicketForm, refined: Refined) {
  const kept = linesInDescription(form, refined)
  return {
    form: { ...form, description: appendAssumptions(refined.before.description, kept) },
    refined: { ...refined, description: false },
  }
}

/** A rewrite gets its old text back; a new criterion goes. */
export function dropCriterion(form: TicketForm, refined: Refined, key: string) {
  const was = refined.criteria.find((c) => c.key === key)?.was
  const criteria =
    was != null
      ? form.criteria.map((c) => (c.key === key ? { ...c, text: was } : c))
      : form.criteria.filter((c) => c.key !== key)
  return {
    form: { ...form, criteria },
    refined: { ...refined, criteria: refined.criteria.filter((c) => c.key !== key) },
  }
}

/** Takes one assumption or decision line out of the description again. */
export function dropLine(form: TicketForm, refined: Refined, line: string) {
  return {
    form: { ...form, description: removeAssumption(form.description, line) },
    refined: { ...refined, lines: refined.lines.filter((l) => l !== line) },
  }
}

/** Answers a question by clicking an option: "Decided: question → option" under "## Assumptions" (or replaces it). */
export function decide(form: TicketForm, refined: Refined, question: string, option: string) {
  const prefix = `Decided: ${question} → `
  const earlier = refined.lines.find((l) => l.startsWith(prefix))
  const dropped = earlier ? dropLine(form, refined, earlier) : { form, refined }
  const line = prefix + option
  return {
    form: {
      ...dropped.form,
      description: appendAssumptions(dropped.form.description, [line]),
    },
    refined: { ...dropped.refined, lines: [...dropped.refined.lines, line] },
  }
}

/** The option a question was answered with, while that line is in the description. */
export function decision(form: TicketForm, refined: Refined, question: string): string | null {
  const prefix = `Decided: ${question} → `
  const line = linesInDescription(form, refined).find((l) => l.startsWith(prefix))
  return line ? line.slice(prefix.length) : null
}

const hasLine = (description: string, line: string) =>
  description.split('\n').some((l) => l.trimEnd() === `- ${line}`)

/** Removes "- line" from a description, and the "## Assumptions" heading when its section is empty then. */
export function removeAssumption(description: string, line: string): string {
  const lines = description.split('\n')
  const at = lines.findIndex((l) => l.trimEnd() === `- ${line}`)
  if (at < 0) return description
  lines.splice(at, 1)
  const heading = lines.findIndex((l) => /^## Assumptions[ \t]*$/.test(l))
  if (heading >= 0) {
    let end = heading + 1
    while (end < lines.length && !/^#{1,2} /.test(lines[end])) end++
    if (lines.slice(heading + 1, end).every((l) => l.trim() === '')) {
      lines.splice(heading, end - heading)
    }
  }
  const text = lines.join('\n').trimEnd()
  return text ? `${text}\n` : ''
}

import {
  criterionSuggestion,
  decide,
  decision,
  dropCriterion,
  dropDescription,
  dropLine,
  dropTitle,
  linesInDescription,
  refineForm,
  removeAssumption,
  titleSuggestion,
} from './refine'
import { refinedCriterionKeys, type SpecProposal, type TicketForm } from './tickets'

const form: TicketForm = {
  title: 'reset pw',
  description: 'users forget passwords',
  status: 'open',
  priority: 'normal',
  criteria: [
    { key: 'k1', id: 'a', text: 'no info leak' },
    { key: 'k2', id: 'b', text: 'works' },
  ],
}

const proposal: SpecProposal = {
  size: 'medium',
  title: 'Add password reset by email',
  description: '## Goal\n\nPeople reset their password.',
  criteria: [
    { replaces: 0, was: 'no info leak', text: 'An unknown email shows the same message' },
    { replaces: null, was: null, text: 'The link expires after 1 hour' },
  ],
  assumptions: ['Links are single-use'],
  questions: [{ question: 'Which sender?', options: ['SMTP', 'SendGrid'], recommended: 'SMTP' }],
  promptVersion: 'refine-spec.v2',
  providerId: 'p1',
  providerName: 'Team key',
  model: 'claude-opus-5-5',
  durationMs: 1000,
}

const refine = () => refineForm(form, proposal, refinedCriterionKeys(form))

describe('refine into the form', () => {
  it('writes the whole proposal into the form, a rewrite keeping its row and id', () => {
    const { form: next, refined } = refine()
    expect(next.title).toBe('Add password reset by email')
    expect(next.description).toBe(
      '## Goal\n\nPeople reset their password.\n\n## Assumptions\n\n- Assumed: Links are single-use\n',
    )
    expect(next.criteria.map((c) => [c.key, c.id, c.text])).toEqual([
      ['k1', 'a', 'An unknown email shows the same message'],
      ['k2', 'b', 'works'],
      [next.criteria[2].key, null, 'The link expires after 1 hour'],
    ])
    expect(titleSuggestion(next, refined)).toBe('Add password reset by email')
    expect(criterionSuggestion(refined, next.criteria[0])?.was).toBe('no info leak')
    expect(criterionSuggestion(refined, next.criteria[1])).toBeNull()
    expect(criterionSuggestion(refined, next.criteria[2])?.was).toBeNull()
    expect(refined.before).toBe(form)
  })

  it('forgets a suggestion once the person edits the field', () => {
    const { form: next, refined } = refine()
    expect(titleSuggestion({ ...next, title: 'Mine' }, refined)).toBeNull()
    expect(criterionSuggestion(refined, { ...next.criteria[0], text: 'Mine' })).toBeNull()
  })

  it('drops one suggestion at a time', () => {
    const start = refine()
    const title = dropTitle(start.form, start.refined)
    expect(title.form.title).toBe('reset pw')
    expect(titleSuggestion(title.form, title.refined)).toBeNull()

    const rewrite = dropCriterion(start.form, start.refined, 'k1')
    expect(rewrite.form.criteria[0]).toEqual({ key: 'k1', id: 'a', text: 'no info leak' })
    const added = dropCriterion(start.form, start.refined, start.form.criteria[2].key)
    expect(added.form.criteria.map((c) => c.key)).toEqual(['k1', 'k2'])

    const description = dropDescription(start.form, start.refined)
    expect(description.form.description).toBe(
      'users forget passwords\n\n## Assumptions\n\n- Assumed: Links are single-use\n',
    )
    const line = dropLine(start.form, start.refined, 'Assumed: Links are single-use')
    expect(line.form.description).toBe('## Goal\n\nPeople reset their password.\n')
    expect(linesInDescription(line.form, line.refined)).toEqual([])
  })

  it('answers a question with one line, replaced by another choice', () => {
    const start = refine()
    const first = decide(start.form, start.refined, 'Which sender?', 'SMTP')
    expect(decision(first.form, first.refined, 'Which sender?')).toBe('SMTP')
    const second = decide(first.form, first.refined, 'Which sender?', 'SendGrid')
    expect(second.form.description).toBe(
      '## Goal\n\nPeople reset their password.\n\n## Assumptions\n\n- Assumed: Links are single-use\n- Decided: Which sender? → SendGrid\n',
    )
    expect(decision(second.form, second.refined, 'Which sender?')).toBe('SendGrid')
  })

  it('removes an assumption line and an empty section, but keeps a section with other lines', () => {
    expect(removeAssumption('Text\n\n## Assumptions\n\n- A\n', 'A')).toBe('Text\n')
    expect(removeAssumption('## Assumptions\n\n- A\n- B\n\n## Out of scope\n\nX', 'A')).toBe(
      '## Assumptions\n\n- B\n\n## Out of scope\n\nX\n',
    )
    expect(removeAssumption('## Assumptions\n\n- A\n\n## Out of scope\n\nX', 'A')).toBe(
      '## Out of scope\n\nX\n',
    )
    expect(removeAssumption('Text', 'A')).toBe('Text')
  })
})

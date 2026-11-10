import {
  addCriterion,
  appendAssumptions,
  applyProposal,
  changeCriterion,
  emptyTicketForm,
  largerSize,
  moveCriterion,
  refinedCriterionKeys,
  removeCriterion,
  selectAll,
  toCreateTicketRequest,
  toRefineSpecRequest,
  toTicketForm,
  toUpdateTicketRequest,
  type CriterionDraft,
  type SpecProposal,
  type TicketInfo,
} from './tickets'

const ticket: TicketInfo = {
  id: 't1',
  number: 7,
  key: 'HUB-7',
  title: 'Login page',
  description: '# Goal',
  status: 'in_progress',
  priority: 'high',
  criteria: [
    { id: 'a', text: 'first' },
    { id: 'b', text: 'second' },
    { id: 'c', text: 'third' },
  ],
  createdAt: '2026-10-26T12:00:00Z',
  updatedAt: '2026-10-26T12:00:00Z',
}

const texts = (criteria: CriterionDraft[]) => criteria.map((c) => c.text)

describe('criteria editing', () => {
  const criteria = toTicketForm(ticket).criteria

  it('moves criteria up and down and stops at the ends', () => {
    expect(texts(moveCriterion(criteria, 1, -1))).toEqual(['second', 'first', 'third'])
    expect(texts(moveCriterion(criteria, 1, 1))).toEqual(['first', 'third', 'second'])
    expect(moveCriterion(criteria, 0, -1)).toBe(criteria)
    expect(moveCriterion(criteria, 2, 1)).toBe(criteria)
  })

  it('adds empty rows with distinct keys and no id', () => {
    const added = addCriterion(addCriterion(criteria))
    expect(added).toHaveLength(5)
    expect(added[3]).toMatchObject({ id: null, text: '' })
    expect(added[3].key).not.toBe(added[4].key)
  })

  it('changes and removes one criterion without touching the others', () => {
    expect(texts(changeCriterion(criteria, 2, 'changed'))).toEqual(['first', 'second', 'changed'])
    expect(removeCriterion(criteria, 0).map((c) => c.id)).toEqual(['b', 'c'])
    expect(texts(criteria)).toEqual(['first', 'second', 'third'])
  })
})

describe('ticket requests', () => {
  it('creates with trimmed title and criteria texts, dropping empty rows', () => {
    const form = {
      ...emptyTicketForm(),
      title: '  New  ',
      description: '  kept as written  ',
      criteria: changeCriterion(addCriterion(addCriterion([])), 1, ' only this '),
    }
    expect(toCreateTicketRequest(form)).toEqual({
      title: 'New',
      description: '  kept as written  ',
      priority: 'normal',
      criteria: ['only this'],
    })
  })

  it('updates with ids for existing criteria, null for new ones, in the new order', () => {
    let form = toTicketForm(ticket)
    form = { ...form, criteria: moveCriterion(removeCriterion(form.criteria, 1), 1, -1) }
    form = { ...form, criteria: changeCriterion(addCriterion(form.criteria), 2, 'fourth') }

    expect(toUpdateTicketRequest(form)).toEqual({
      title: 'Login page',
      description: '# Goal',
      status: 'in_progress',
      priority: 'high',
      criteria: [
        { id: 'c', text: 'third' },
        { id: 'a', text: 'first' },
        { id: null, text: 'fourth' },
      ],
    })
  })

  it('removes a criterion whose text was emptied', () => {
    const form = toTicketForm(ticket)
    const request = toUpdateTicketRequest({
      ...form,
      criteria: changeCriterion(form.criteria, 0, '   '),
    })
    expect(request.criteria.map((c) => c.id)).toEqual(['b', 'c'])
  })
})

describe('refine spec', () => {
  const form = toTicketForm(ticket)
  const keys = refinedCriterionKeys(form)
  const proposal: SpecProposal = {
    size: 'medium',
    title: 'Add a login page',
    description: '## Goal\n\nPeople log in.',
    criteria: [
      { replaces: 1, was: 'second', text: 'second, sharper' },
      { replaces: null, was: null, text: 'fourth' },
    ],
    assumptions: ['Sessions last a day', 'Cookies only'],
    questions: [{ question: 'Which IdP?', options: ['GitHub', 'Google'], recommended: 'GitHub' }],
    promptVersion: 'refine-spec.v2',
    providerId: 'p1',
    providerName: 'Team key',
    model: 'claude-opus-5-5',
    durationMs: 1000,
  }

  it('sends the form as it is on screen, without empty rows', () => {
    const draft = { ...form, criteria: addCriterion(form.criteria) }
    expect(toRefineSpecRequest(draft, null)).toEqual({
      title: 'Login page',
      description: '# Goal',
      criteria: ['first', 'second', 'third'],
      providerId: null,
    })
    expect(toRefineSpecRequest(draft, 'p1').providerId).toBe('p1')
    expect(toRefineSpecRequest(draft, null, 'large').minSize).toBe('large')
    expect(refinedCriterionKeys(draft)).toEqual(keys)
  })

  it('asks one size larger for more detail', () => {
    expect([largerSize('small'), largerSize('medium'), largerSize('large')]).toEqual([
      'medium',
      'large',
      null,
    ])
  })

  it('takes over everything ticked: a rewrite keeps its id, assumptions go into the description', () => {
    const next = applyProposal(
      form,
      proposal,
      { ...selectAll(proposal), decisions: [{ question: 'Which IdP?', option: 'GitHub' }] },
      keys,
    )
    expect(next.title).toBe('Add a login page')
    expect(next.criteria.map((c) => [c.id, c.text])).toEqual([
      ['a', 'first'],
      ['b', 'second, sharper'],
      ['c', 'third'],
      [null, 'fourth'],
    ])
    expect(next.description).toBe(
      '## Goal\n\nPeople log in.\n\n## Assumptions\n\n- Assumed: Sessions last a day\n- Assumed: Cookies only\n- Decided: Which IdP? → GitHub\n',
    )
    expect(form.title).toBe('Login page')
  })

  it('leaves out what is unticked, and adds a rewrite of a removed criterion as new', () => {
    const edited = { ...form, criteria: form.criteria.filter((c) => c.id !== 'b') }
    const next = applyProposal(
      edited,
      proposal,
      { title: false, description: false, criteria: [0], assumptions: [1], decisions: [] },
      keys,
    )
    expect(next.title).toBe('Login page')
    expect(next.criteria.map((c) => [c.id, c.text])).toEqual([
      ['a', 'first'],
      ['c', 'third'],
      [null, 'second, sharper'],
    ])
    expect(next.description).toBe('# Goal\n\n## Assumptions\n\n- Assumed: Cookies only\n')
  })

  it('adds to an existing assumptions section, before the next section', () => {
    expect(appendAssumptions('## Goal\n\nX\n\n## Assumptions\n\n- Assumed: A\n', ['B'])).toBe(
      '## Goal\n\nX\n\n## Assumptions\n\n- Assumed: A\n- B\n',
    )
    expect(appendAssumptions('## Assumptions\n\n- A\n\n## Out of scope\n\nY', ['B'])).toBe(
      '## Assumptions\n\n- A\n- B\n\n## Out of scope\n\nY\n',
    )
    expect(appendAssumptions('', ['B'])).toBe('## Assumptions\n\n- B\n')
    expect(appendAssumptions('Text', [])).toBe('Text')
  })
})

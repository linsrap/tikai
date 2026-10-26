import {
  addCriterion,
  addProposedCriterion,
  appendQuestions,
  replaceCriteria,
  takeProposedDescription,
  takeProposedTitle,
  toRefineSpecRequest,
  changeCriterion,
  emptyTicketForm,
  moveCriterion,
  removeCriterion,
  toCreateTicketRequest,
  toTicketForm,
  toUpdateTicketRequest,
  type CriterionDraft,
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

  it('sends the form as it is on screen, without empty rows', () => {
    const draft = { ...form, criteria: addCriterion(form.criteria) }
    expect(toRefineSpecRequest(draft, null)).toEqual({
      title: 'Login page',
      description: '# Goal',
      criteria: ['first', 'second', 'third'],
      providerId: null,
    })
    expect(toRefineSpecRequest(draft, 'p1').providerId).toBe('p1')
  })

  it('takes over title and description one at a time', () => {
    const titled = takeProposedTitle(form, 'Add a login page')
    expect(titled).toEqual({ ...form, title: 'Add a login page' })
    expect(takeProposedDescription(titled, '## Goal')).toEqual({
      ...titled,
      description: '## Goal',
    })
    expect(form.title).toBe('Login page')
  })

  it('adds one proposed criterion as new and keeps the saved ones', () => {
    const added = addProposedCriterion(form, 'fourth')
    expect(added.criteria.map((c) => c.id)).toEqual(['a', 'b', 'c', null])
    expect(texts(added.criteria)).toEqual(['first', 'second', 'third', 'fourth'])
  })

  it('replaces all criteria with new ones', () => {
    const replaced = replaceCriteria(form, ['x', 'y'])
    expect(replaced.criteria.map((c) => [c.id, c.text])).toEqual([
      [null, 'x'],
      [null, 'y'],
    ])
    expect(new Set(replaced.criteria.map((c) => c.key)).size).toBe(2)
  })

  it('appends open questions to the description as a list', () => {
    expect(appendQuestions(form, ['Which IdP?', 'Session length?']).description).toBe(
      '# Goal\n\n## Open questions\n\n- Which IdP?\n- Session length?\n',
    )
    expect(appendQuestions({ ...form, description: '  ' }, ['Q?']).description).toBe(
      '## Open questions\n\n- Q?\n',
    )
  })
})

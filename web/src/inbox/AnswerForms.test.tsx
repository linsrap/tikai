import { fireEvent, render, screen } from '@testing-library/react'
import { PlanAnswer, QuestionAnswer } from './AnswerForms'

const mutate = vi.fn()

vi.mock('./inbox', async (importOriginal) => ({
  ...(await importOriginal<typeof import('./inbox')>()),
  useAnswer: () => ({ mutate, isPending: false, error: null }),
}))

beforeEach(() => mutate.mockReset())

const question = {
  question: 'Which stack?',
  options: ['React', 'Plain HTML'],
  recommended: 'React',
  context: null,
}

function writeAnswer() {
  render(<QuestionAnswer id="item-1" question={question} />)
  fireEvent.click(screen.getByRole('button', { name: 'Other…' }))
  return screen.getByLabelText('Answer')
}

describe('a free-text answer', () => {
  it('is sent with Enter and keeps the lines added with Shift+Enter', () => {
    const answer = writeAnswer()
    fireEvent.change(answer, { target: { value: 'React and Vite:' } })
    fireEvent.keyDown(answer, { key: 'Enter', shiftKey: true })
    expect(mutate).not.toHaveBeenCalled()

    fireEvent.change(answer, { target: { value: 'React and Vite:\nlocal storage only' } })
    fireEvent.keyDown(answer, { key: 'Enter' })
    expect(mutate).toHaveBeenCalledWith({ text: 'React and Vite:\nlocal storage only' })
  })

  it('is not sent while empty or while an input method composes', () => {
    const answer = writeAnswer()
    fireEvent.keyDown(answer, { key: 'Enter' })
    fireEvent.change(answer, { target: { value: 'にほん' } })
    fireEvent.keyDown(answer, { key: 'Enter', isComposing: true })
    expect(mutate).not.toHaveBeenCalled()
  })
})

describe('a plan sent back', () => {
  it('sends its notes with Enter, Shift+Enter adds a line', () => {
    const plan = { summary: 'Add a toggle', steps: ['One step'], risks: [], filesExpected: [] }
    render(<PlanAnswer id="item-2" plan={plan} />)
    fireEvent.click(screen.getByRole('button', { name: 'Revise…' }))
    const notes = screen.getByLabelText('What should change?')

    fireEvent.change(notes, { target: { value: 'Use React' } })
    fireEvent.keyDown(notes, { key: 'Enter', shiftKey: true })
    expect(mutate).not.toHaveBeenCalled()
    fireEvent.keyDown(notes, { key: 'Enter' })
    expect(mutate).toHaveBeenCalledWith({ decision: 'revise', text: 'Use React' })
  })
})

import { applyTheme, setTheme } from './theme'

describe('theme', () => {
  afterEach(() => setTheme('system'))

  it('sets data-theme on <html> and remembers the choice', () => {
    setTheme('dark')
    expect(document.documentElement.dataset.theme).toBe('dark')
    expect(localStorage.getItem('tikai.theme')).toBe('dark')

    setTheme('light')
    expect(document.documentElement.dataset.theme).toBe('light')
  })

  it('follows the system again: no attribute, nothing stored', () => {
    setTheme('dark')
    setTheme('system')
    expect(document.documentElement.dataset.theme).toBeUndefined()
    expect(localStorage.getItem('tikai.theme')).toBeNull()
    applyTheme()
    expect(document.documentElement.dataset.theme).toBeUndefined()
  })
})

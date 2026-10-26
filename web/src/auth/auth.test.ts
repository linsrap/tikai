import { entryRouteFor, problemMessage } from './auth'

const user = {
  id: '0192f0c4-0000-7000-8000-000000000001',
  email: 'ada@example.org',
  displayName: 'Ada',
  isAdmin: true,
}

describe('entryRouteFor', () => {
  it('sends people to sign-up while no account exists', () => {
    expect(entryRouteFor({ signUpOpen: true, user: null })).toBe('/signup')
  })

  it('sends people without a session to login once sign-up is closed', () => {
    expect(entryRouteFor({ signUpOpen: false, user: null })).toBe('/login')
  })

  it('sends logged-in people home', () => {
    expect(entryRouteFor({ signUpOpen: false, user })).toBe('/')
  })
})

describe('problemMessage', () => {
  it('prefers field errors from a validation problem', () => {
    const problem = {
      title: 'One or more validation errors occurred.',
      errors: { password: ['Too short.'] },
    }
    expect(problemMessage(problem, 'fallback')).toBe('Too short.')
  })

  it('uses detail, then title', () => {
    expect(problemMessage({ title: 'Sign-up is closed', detail: 'Ask the admin.' }, 'x')).toBe(
      'Ask the admin.',
    )
    expect(problemMessage({ title: 'Wrong email or password' }, 'x')).toBe(
      'Wrong email or password',
    )
  })

  it('falls back when the body is not a problem', () => {
    expect(problemMessage(undefined, 'fallback')).toBe('fallback')
  })
})

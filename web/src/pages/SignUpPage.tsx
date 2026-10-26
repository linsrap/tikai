import { useNavigate } from '@tanstack/react-router'
import { useSignUp } from '../auth/auth'
import { AuthForm } from './AuthForm'

export function SignUpPage() {
  const signUp = useSignUp()
  const navigate = useNavigate()

  return (
    <AuthForm
      title="Set up Tikai"
      intro={
        <p>
          No account exists yet. The account you create now is the admin account. After that,
          sign-up is closed.
        </p>
      }
      submitLabel="Create admin account"
      pending={signUp.isPending}
      error={signUp.error?.message}
      fields={[
        { name: 'displayName', label: 'Name', type: 'text', autoComplete: 'name' },
        { name: 'email', label: 'Email', type: 'email', autoComplete: 'username' },
        {
          name: 'password',
          label: 'Password (at least 12 characters)',
          type: 'password',
          autoComplete: 'new-password',
          minLength: 12,
        },
      ]}
      onSubmit={(v) =>
        signUp.mutate(
          { displayName: v.displayName ?? '', email: v.email ?? '', password: v.password ?? '' },
          { onSuccess: () => navigate({ to: '/' }) },
        )
      }
    />
  )
}

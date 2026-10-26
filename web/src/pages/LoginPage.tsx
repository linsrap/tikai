import { useNavigate } from '@tanstack/react-router'
import { useLogin } from '../auth/auth'
import { AuthForm } from './AuthForm'

export function LoginPage() {
  const login = useLogin()
  const navigate = useNavigate()

  return (
    <AuthForm
      title="Log in to Tikai"
      submitLabel="Log in"
      pending={login.isPending}
      error={login.error?.message}
      fields={[
        { name: 'email', label: 'Email', type: 'email', autoComplete: 'username' },
        { name: 'password', label: 'Password', type: 'password', autoComplete: 'current-password' },
      ]}
      onSubmit={(v) =>
        login.mutate(
          { email: v.email ?? '', password: v.password ?? '' },
          { onSuccess: () => navigate({ to: '/' }) },
        )
      }
    />
  )
}

import { useState, type FormEvent, type ReactNode } from 'react'
import { Button } from '../ui/Button'
import { ErrorText, Field, Input } from '../ui/form'

type FieldSpec = {
  name: string
  label: string
  type: string
  autoComplete: string
  minLength?: number
}

type Props = {
  title: string
  intro?: ReactNode
  submitLabel: string
  pending: boolean
  error: string | undefined
  fields: FieldSpec[]
  onSubmit: (values: Record<string, string>) => void
}

/** Log in and sign up: a small centred form on the page background. */
export function AuthForm({ title, intro, submitLabel, pending, error, fields, onSubmit }: Props) {
  const [values, setValues] = useState<Record<string, string>>({})

  function submit(event: FormEvent) {
    event.preventDefault()
    onSubmit(values)
  }

  return (
    <main className="flex min-h-screen flex-col items-center justify-center px-4 py-12">
      <div className="w-full max-w-[22rem]">
        <div className="mb-8 flex items-center gap-2 text-[15px] font-semibold tracking-[-0.01em]">
          <span
            aria-hidden="true"
            className="flex size-6 items-center justify-center rounded-md bg-primary font-mono text-[13px] font-medium text-on-primary"
          >
            t
          </span>
          Tikai
        </div>
        <h1 className="m-0 text-xl font-semibold tracking-[-0.015em]">{title}</h1>
        {intro && <div className="mt-2 text-base text-fg-muted">{intro}</div>}
        <form onSubmit={submit} className="mt-6 flex flex-col gap-4">
          {fields.map((field) => (
            <Field key={field.name} label={field.label}>
              <Input
                name={field.name}
                type={field.type}
                autoComplete={field.autoComplete}
                minLength={field.minLength}
                required
                className="h-9"
                value={values[field.name] ?? ''}
                onChange={(e) => setValues({ ...values, [field.name]: e.target.value })}
              />
            </Field>
          ))}
          {error && <ErrorText>{error}</ErrorText>}
          <Button type="submit" variant="primary" className="mt-1 h-9" disabled={pending}>
            {pending ? 'Please wait…' : submitLabel}
          </Button>
        </form>
      </div>
    </main>
  )
}

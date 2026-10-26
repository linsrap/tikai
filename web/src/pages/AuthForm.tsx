import { useState, type FormEvent, type ReactNode } from 'react'

type Field = { name: string; label: string; type: string; autoComplete: string; minLength?: number }

type Props = {
  title: string
  intro?: ReactNode
  submitLabel: string
  pending: boolean
  error: string | undefined
  fields: Field[]
  onSubmit: (values: Record<string, string>) => void
}

export function AuthForm({ title, intro, submitLabel, pending, error, fields, onSubmit }: Props) {
  const [values, setValues] = useState<Record<string, string>>({})

  function submit(event: FormEvent) {
    event.preventDefault()
    onSubmit(values)
  }

  return (
    <main className="shell narrow">
      <h1>{title}</h1>
      {intro}
      <form onSubmit={submit} className="stack">
        {fields.map((field) => (
          <label key={field.name} className="field">
            <span>{field.label}</span>
            <input
              name={field.name}
              type={field.type}
              autoComplete={field.autoComplete}
              minLength={field.minLength}
              required
              value={values[field.name] ?? ''}
              onChange={(e) => setValues({ ...values, [field.name]: e.target.value })}
            />
          </label>
        ))}
        {error && (
          <p role="alert" className="error">
            {error}
          </p>
        )}
        <button type="submit" disabled={pending}>
          {pending ? 'Please wait…' : submitLabel}
        </button>
      </form>
    </main>
  )
}

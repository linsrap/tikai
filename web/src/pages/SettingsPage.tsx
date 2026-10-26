import { AppShell } from './AppShell'
import { ProvidersSection } from './ProvidersSection'

export function SettingsPage() {
  return (
    <AppShell>
      <h1>Settings</h1>
      <h2>Your LLM providers</h2>
      <p className="muted">
        Tikai uses your own API key: for refining tickets, reviews and agent runs. Keys are
        encrypted on the hub and only sent to the provider. A project can also have its own
        providers (on the project page).
      </p>
      <ProvidersSection owner={{ kind: 'user' }} />
    </AppShell>
  )
}

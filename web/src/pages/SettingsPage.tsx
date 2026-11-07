import { PageBody, PageHeader, SettingsSection } from '../ui/layout'
import { usePageTitle } from '../ui/title'
import { AppShell } from './AppShell'
import { ProvidersSection } from './ProvidersSection'

export function SettingsPage() {
  usePageTitle('Settings')
  return (
    <AppShell>
      <PageHeader title="Settings" />
      <PageBody>
        <SettingsSection
          title="Your LLM providers"
          description="Tikai uses your own API key: for refining tickets, reviews and agent runs. Keys are encrypted on the hub and only sent to the provider. A project can also have its own providers (in its settings)."
        >
          <ProvidersSection owner={{ kind: 'user' }} />
        </SettingsSection>
      </PageBody>
    </AppShell>
  )
}

import { AlertDialog, Tooltip as RadixTooltip } from 'radix-ui'
import { useState, type ReactNode } from 'react'
import { Button } from './Button'
import { cx } from './cx'
import { Field, Input } from './form'

const surface = 'rounded-lg bg-surface text-fg shadow-overlay'

/**
 * Asks before something that cannot be undone (stop a run, reject a change). Radix keeps the focus inside, closes on
 * Escape and gives the focus back to the trigger. With `typeToConfirm` the action stays off until that text is typed
 * (deleting a project with everything in it).
 */
export function ConfirmDialog({
  trigger,
  title,
  description,
  confirmLabel,
  cancelLabel = 'Cancel',
  danger = false,
  typeToConfirm,
  onConfirm,
}: {
  trigger: ReactNode
  title: ReactNode
  description?: ReactNode
  confirmLabel: string
  cancelLabel?: string
  danger?: boolean
  typeToConfirm?: string
  onConfirm: () => void
}) {
  const [typed, setTyped] = useState('')
  const confirmed = typeToConfirm === undefined || typed.trim() === typeToConfirm.trim()
  return (
    <AlertDialog.Root onOpenChange={() => setTyped('')}>
      <AlertDialog.Trigger asChild>{trigger}</AlertDialog.Trigger>
      <AlertDialog.Portal>
        <AlertDialog.Overlay className="fixed inset-0 z-40 bg-[rgb(20_20_18/0.35)]" />
        <AlertDialog.Content
          className={cx(
            surface,
            'fixed top-1/2 left-1/2 z-50 w-[min(28rem,calc(100vw-2rem))] -translate-x-1/2 -translate-y-1/2 p-5',
          )}
        >
          <AlertDialog.Title className="m-0 text-lg font-semibold">{title}</AlertDialog.Title>
          {description && (
            <AlertDialog.Description className="mt-2 mb-0 text-base text-fg-muted">
              {description}
            </AlertDialog.Description>
          )}
          {typeToConfirm !== undefined && (
            <div className="mt-4">
              <Field label={`Type “${typeToConfirm}” to confirm`}>
                <Input
                  value={typed}
                  autoComplete="off"
                  spellCheck={false}
                  onChange={(e) => setTyped(e.target.value)}
                />
              </Field>
            </div>
          )}
          <div className="mt-5 flex justify-end gap-2">
            <AlertDialog.Cancel asChild>
              <Button>{cancelLabel}</Button>
            </AlertDialog.Cancel>
            <AlertDialog.Action asChild>
              <Button
                variant="primary"
                disabled={!confirmed}
                className={cx(
                  danger && 'border-bad-solid bg-bad-solid hover:border-bad hover:bg-bad',
                )}
                onClick={onConfirm}
              >
                {confirmLabel}
              </Button>
            </AlertDialog.Action>
          </div>
        </AlertDialog.Content>
      </AlertDialog.Portal>
    </AlertDialog.Root>
  )
}

/** A short hint on hover or focus, for icon-only controls. The trigger still needs its own aria-label. */
export function Tooltip({
  content,
  children,
  side = 'right',
}: {
  content: ReactNode
  children: ReactNode
  side?: 'top' | 'bottom' | 'left' | 'right'
}) {
  return (
    <RadixTooltip.Provider delayDuration={300}>
      <RadixTooltip.Root>
        <RadixTooltip.Trigger asChild>{children}</RadixTooltip.Trigger>
        <RadixTooltip.Portal>
          <RadixTooltip.Content
            side={side}
            sideOffset={6}
            className="z-50 rounded-md bg-primary px-2 py-1 text-xs font-medium text-on-primary"
          >
            {content}
          </RadixTooltip.Content>
        </RadixTooltip.Portal>
      </RadixTooltip.Root>
    </RadixTooltip.Provider>
  )
}

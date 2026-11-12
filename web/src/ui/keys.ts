import type { KeyboardEvent } from 'react'

/**
 * For a textarea in a form: Enter sends the form, Shift+Enter adds a line. Not while an input method composes a
 * character (Enter then picks the character). The form's onSubmit still decides whether there is anything to send.
 */
export function submitOnEnter(event: KeyboardEvent<HTMLTextAreaElement>) {
  if (event.key !== 'Enter' || event.shiftKey || event.nativeEvent.isComposing) {
    return
  }
  event.preventDefault()
  event.currentTarget.form?.requestSubmit()
}

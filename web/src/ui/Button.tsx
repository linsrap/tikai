import { Slot } from 'radix-ui'
import { forwardRef, type ButtonHTMLAttributes } from 'react'
import { buttonClass, type ButtonSize, type ButtonVariant } from './buttonClass'
import { cx } from './cx'

type Props = ButtonHTMLAttributes<HTMLButtonElement> & {
  variant?: ButtonVariant
  size?: ButtonSize
  /** Render the single child (e.g. a Link) with the button's look instead of a button. */
  asChild?: boolean
}

/** A button. Defaults to type="button", so it never submits a form by accident. */
export const Button = forwardRef<HTMLButtonElement, Props>(function Button(
  { variant = 'secondary', size = 'md', asChild = false, className, type, ...props },
  ref,
) {
  const classes = cx(buttonClass(variant, size), className)
  if (asChild) return <Slot.Root ref={ref} className={classes} {...props} />
  return <button ref={ref} type={type ?? 'button'} className={classes} {...props} />
})

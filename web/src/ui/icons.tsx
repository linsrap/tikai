import type { ReactNode, SVGProps } from 'react'

type IconProps = SVGProps<SVGSVGElement> & { size?: number }

/** A 16×16 stroke icon; decorative, so hidden from screen readers (the control around it carries the name). */
function icon(paths: ReactNode) {
  return function Icon({ size = 16, strokeWidth = 1.5, ...props }: IconProps) {
    return (
      <svg
        width={size}
        height={size}
        viewBox="0 0 16 16"
        fill="none"
        stroke="currentColor"
        strokeWidth={strokeWidth}
        strokeLinecap="round"
        strokeLinejoin="round"
        aria-hidden="true"
        focusable="false"
        {...props}
      >
        {paths}
      </svg>
    )
  }
}

export const InboxIcon = icon(
  <>
    <path d="M2 9.5h3.2l1 1.8h3.6l1-1.8H14" />
    <path d="M3.6 3.2h8.8L14 9.5v3.3H2V9.5z" />
  </>,
)
export const BoardIcon = icon(<path d="M2 8h2.5l2-4.5 3 9 2-4.5H14" />)
export const FolderIcon = icon(<path d="M2 4.2h4l1.5 1.5H14v6.8H2z" />)
export const SettingsIcon = icon(
  <>
    <circle cx="8" cy="8" r="2.2" />
    <path d="M8 1.8v1.6M8 12.6v1.6M1.8 8h1.6M12.6 8h1.6M3.6 3.6l1.1 1.1M11.3 11.3l1.1 1.1M3.6 12.4l1.1-1.1M11.3 4.7l1.1-1.1" />
  </>,
)
export const LogOutIcon = icon(<path d="M6 2.5H3.5v11H6M10 5l3 3-3 3M13 8H6.5" />)
export const PlusIcon = icon(<path d="M8 3v10M3 8h10" />)
export const CheckIcon = icon(<path d="M3 8.5l3.2 3L13 4.5" />)
export const XIcon = icon(<path d="M4 4l8 8M12 4l-8 8" />)
export const ChevronDownIcon = icon(<path d="M4 6l4 4 4-4" />)
export const ChevronRightIcon = icon(<path d="M6 4l4 4-4 4" />)
export const TerminalIcon = icon(
  <>
    <rect x="2" y="3" width="12" height="10" rx="1.5" />
    <path d="M5 6.5l2 1.5-2 1.5M8.5 10H11" />
  </>,
)
export const StopIcon = icon(<rect x="4" y="4" width="8" height="8" rx="1" />)
export const RetryIcon = icon(<path d="M13 8a5 5 0 1 1-1.5-3.6M13 2.5v2.8h-2.8" />)
export const BranchIcon = icon(
  <>
    <circle cx="5" cy="3.5" r="1.5" />
    <circle cx="5" cy="12.5" r="1.5" />
    <circle cx="11" cy="6" r="1.5" />
    <path d="M5 5v6M11 7.5c0 2-2 2.5-6 3.5" />
  </>,
)
export const DownloadIcon = icon(<path d="M8 2.5v8M4.5 7.5L8 11l3.5-3.5M3 13.5h10" />)
export const MoreIcon = icon(
  <>
    <circle cx="3.5" cy="8" r="0.6" fill="currentColor" />
    <circle cx="8" cy="8" r="0.6" fill="currentColor" />
    <circle cx="12.5" cy="8" r="0.6" fill="currentColor" />
  </>,
)
export const CommentIcon = icon(<path d="M2.5 3.5h11v7.5H7l-3 2.5V11H2.5z" />)
export const FileIcon = icon(<path d="M4 2h5l3 3v9H4zM9 2v3h3" />)
export const SunIcon = icon(
  <>
    <circle cx="8" cy="8" r="2.8" />
    <path d="M8 1.5v1.4M8 13.1v1.4M1.5 8h1.4M13.1 8h1.4M3.4 3.4l1 1M11.6 11.6l1 1M3.4 12.6l1-1M11.6 4.4l1-1" />
  </>,
)
export const MoonIcon = icon(<path d="M13 9.5A5.5 5.5 0 0 1 6.5 3a5.5 5.5 0 1 0 6.5 6.5z" />)
export const MonitorIcon = icon(
  <>
    <rect x="2" y="2.5" width="12" height="8.5" rx="1.2" />
    <path d="M6 14h4M8 11v3" />
  </>,
)

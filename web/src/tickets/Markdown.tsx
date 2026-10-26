import ReactMarkdown, { type Components } from 'react-markdown'
import remarkGfm from 'remark-gfm'

// Links leave the app in a new tab and do not tell the target where they came from.
const components: Components = {
  a: ({ href, title, children }) => (
    <a href={href} title={title} target="_blank" rel="noopener noreferrer">
      {children}
    </a>
  ),
}

/**
 * Renders ticket markdown (GitHub flavour: tables, task lists, strikethrough).
 * Sanitized by construction: raw HTML is dropped (skipHtml) and react-markdown's default URL filter
 * blanks javascript:, data: and other unsafe link targets. Nothing goes through innerHTML.
 */
export function Markdown({ text }: { text: string }) {
  return (
    <div className="markdown">
      <ReactMarkdown remarkPlugins={[remarkGfm]} skipHtml components={components}>
        {text}
      </ReactMarkdown>
    </div>
  )
}

import { render } from '@testing-library/react'
import { Markdown } from './Markdown'

function renderMarkdown(text: string) {
  return render(<Markdown text={text} />).container
}

describe('Markdown', () => {
  it('renders headings, emphasis and code', () => {
    const html = renderMarkdown('# Goal\n\nPeople can **log in**.\n\n```\ncode\n```')
    expect(html.querySelector('h1')).toHaveTextContent('Goal')
    expect(html.querySelector('strong')).toHaveTextContent('log in')
    expect(html.querySelector('pre code')).toHaveTextContent('code')
  })

  it('renders GitHub tables, task lists and strikethrough', () => {
    const html = renderMarkdown(
      '| a | b |\n| - | - |\n| 1 | 2 |\n\n- [x] done\n- [ ] open\n\n~~old~~',
    )
    expect(html.querySelectorAll('td')).toHaveLength(2)
    expect(html.querySelectorAll('input[type="checkbox"]')).toHaveLength(2)
    expect(html.querySelector('del')).toHaveTextContent('old')
  })

  it('drops raw HTML, including scripts and event handlers', () => {
    const html = renderMarkdown(
      'before\n\n<script>alert(1)</script>\n\n<img src="x" onerror="alert(2)">\n\n<b onclick="alert(3)">bold</b> after',
    )
    expect(html.querySelector('script')).toBeNull()
    expect(html.querySelector('img')).toBeNull()
    expect(html.querySelector('b')).toBeNull()
    expect(html.innerHTML).not.toMatch(/onerror|onclick|alert/)
    expect(html).toHaveTextContent('before')
  })

  it('neutralizes javascript: and data: links and images', () => {
    const html = renderMarkdown(
      '[click](javascript:alert(1)) [data](data:text/html;base64,PHNjcmlwdD4=) ![img](javascript:alert(2))',
    )
    for (const element of html.querySelectorAll('a, img')) {
      const url = element.getAttribute('href') ?? element.getAttribute('src') ?? ''
      expect(url).not.toMatch(/^\s*(javascript|data):/i)
    }
    expect(html.innerHTML).not.toContain('javascript:')
  })

  it('opens normal links in a new tab without a referrer', () => {
    const link = renderMarkdown('[docs](https://example.org/docs)').querySelector('a')
    expect(link).toHaveAttribute('href', 'https://example.org/docs')
    expect(link).toHaveAttribute('target', '_blank')
    expect(link).toHaveAttribute('rel', 'noopener noreferrer')
  })
})

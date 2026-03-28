import { Marked } from 'marked'
import { markedHighlight } from 'marked-highlight'
import hljs from 'highlight.js'
import DOMPurify from 'dompurify'

const MAX_HIGHLIGHT_CHARS = 12000

function escapeHtml(value: string): string {
  return value
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;')
    .replace(/'/g, '&#39;')
}

const markdown = new Marked(
  markedHighlight({
    langPrefix: 'hljs language-',
    highlight(code, lang) {
      if (code.length > MAX_HIGHLIGHT_CHARS) {
        return escapeHtml(code)
      }

      const normalizedLanguage = lang?.toLowerCase()
      if (normalizedLanguage && hljs.getLanguage(normalizedLanguage)) {
        return hljs.highlight(code, { language: normalizedLanguage, ignoreIllegals: true }).value
      }

      // Avoid expensive auto-detection on unknown languages.
      return escapeHtml(code)
    },
  })
)

markdown.setOptions({
  breaks: true,
  gfm: true,
})

export function renderMarkdown(text: string): string {
  return DOMPurify.sanitize(markdown.parse(text) as string, {
    ADD_ATTR: ['class'],
  })
}

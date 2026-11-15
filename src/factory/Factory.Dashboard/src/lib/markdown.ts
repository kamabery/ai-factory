import { Marked, type TokenizerAndRendererExtension } from 'marked'

const esc = (s: string) => s.replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[c]!)

// A backslash is kept literally (no markdown escapes), so Windows paths such as D:\code\.x render as typed.
const literalBackslash: TokenizerAndRendererExtension = {
  name: 'literalBackslash',
  level: 'inline',
  start: (src) => src.indexOf('\\'),
  tokenizer: (src) => (src[0] === '\\' ? { type: 'text', raw: '\\', text: '\\' } : undefined),
}

// Raw HTML in agent output is shown as text, never rendered.
const marked = new Marked({
  gfm: true,
  extensions: [literalBackslash],
  renderer: { html: (token: { text: string } | string) => esc(typeof token === 'string' ? token : token.text) },
})

/** Markdown to HTML, safe to set as innerHTML. */
export const md = (text: string | undefined | null): string => marked.parse(text ?? '', { async: false }) as string

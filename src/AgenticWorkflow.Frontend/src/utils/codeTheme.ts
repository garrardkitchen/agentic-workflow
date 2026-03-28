export const CODE_THEMES = [
  'github-dark',
  'atom-one-dark',
  'night-owl',
  'tokyo-night-dark',
  'monokai',
] as const

const DEFAULT_THEME = 'github-dark'
const THEME_STYLE_ID = 'hljs-theme-style'

const themeLoaders: Record<(typeof CODE_THEMES)[number], () => Promise<string>> = {
  'github-dark': () => import('highlight.js/styles/github-dark.css?inline').then(m => m.default),
  'atom-one-dark': () => import('highlight.js/styles/atom-one-dark.css?inline').then(m => m.default),
  'night-owl': () => import('highlight.js/styles/night-owl.css?inline').then(m => m.default),
  'tokyo-night-dark': () => import('highlight.js/styles/tokyo-night-dark.css?inline').then(m => m.default),
  monokai: () => import('highlight.js/styles/monokai.css?inline').then(m => m.default),
}

export function normalizeCodeTheme(theme?: string): string {
  if (!theme) return DEFAULT_THEME
  return CODE_THEMES.includes(theme as typeof CODE_THEMES[number]) ? theme : DEFAULT_THEME
}

export async function applyCodeTheme(theme?: string): Promise<string> {
  const normalized = normalizeCodeTheme(theme)
  const css = await themeLoaders[normalized as (typeof CODE_THEMES)[number]]()
  const existing = document.getElementById(THEME_STYLE_ID) as HTMLStyleElement | null

  if (existing) {
    if (existing.textContent !== css) existing.textContent = css
    return normalized
  }

  const style = document.createElement('style')
  style.id = THEME_STYLE_ID
  style.textContent = css
  document.head.appendChild(style)
  return normalized
}

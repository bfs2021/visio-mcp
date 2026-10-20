/** Theme only the editor shell; page and exported drawing colours belong to the document. */
export function applyChromeTheme(theme: 'light' | 'dark'): void {
  document.documentElement.dataset.theme = theme;
  document.documentElement.style.colorScheme = theme;
  window.dispatchEvent(new Event('editor-theme-changed'));
}

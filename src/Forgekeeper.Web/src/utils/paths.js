// Open-file / open-folder helpers for the model detail page.
// The API returns windowsPath / windowsBasePath when Library__WindowsPathPrefix is set; otherwise we fall
// back to the container path so "Copy" still gives something useful.

/** Path to copy for a variant (Windows if mapped, else the container path). */
export function variantCopyPath(variant, model) {
  if (variant?.windowsPath) return variant.windowsPath
  if (variant?.absolutePath) return variant.absolutePath
  const base = (model?.basePath || '').replace(/\/+$/, '')
  return base ? `${base}/${variant?.filePath || ''}` : (variant?.filePath || '')
}

/** Folder that contains a variant file (Windows if mapped). */
export function variantFolderPath(variant, model) {
  const p = variantCopyPath(variant, model)
  const i = Math.max(p.lastIndexOf('\\'), p.lastIndexOf('/'))
  return i > 0 ? p.slice(0, i) : (model?.windowsBasePath || model?.basePath || p)
}

/** Model folder to copy. */
export function modelFolderPath(model) {
  return model?.windowsBasePath || model?.basePath || ''
}

/** Files a browser tab can show directly; everything else is downloaded. */
export function isBrowserViewable(fileName) {
  const ext = (fileName || '').split('.').pop()?.toLowerCase()
  return ['pdf', 'png', 'jpg', 'jpeg', 'webp', 'gif', 'txt', 'md'].includes(ext)
}

/** Copy text to the clipboard. Falls back to a hidden textarea when the async API is unavailable. */
export async function copyText(text, doc = globalThis.document, nav = globalThis.navigator) {
  if (!text) return false
  try {
    if (nav?.clipboard?.writeText) {
      await nav.clipboard.writeText(text)
      return true
    }
  } catch { /* fall through */ }
  if (!doc?.createElement) return false
  const ta = doc.createElement('textarea')
  ta.value = text
  ta.setAttribute('readonly', '')
  ta.style.position = 'fixed'
  ta.style.opacity = '0'
  doc.body.appendChild(ta)
  ta.select()
  let ok = false
  try { ok = doc.execCommand('copy') } catch { ok = false }
  doc.body.removeChild(ta)
  return ok
}

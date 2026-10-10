// Helpers for the model 3D viewer (#65).

const PREVIEWABLE = ['.stl']

export function isPreviewableName(name) {
  const n = (name || '').toLowerCase()
  return PREVIEWABLE.some((ext) => n.endsWith(ext))
}

/** Pick the default variant to preview: the largest STL (not the first, which is often a loose part). */
export function pickDefaultPreview(variants, isPreviewable = (v) => isPreviewableName(v.fileName || v.filePath)) {
  let best = null
  for (const v of variants || []) {
    if (!isPreviewable(v)) continue
    if (!best || (v.fileSizeBytes || 0) > (best.fileSizeBytes || 0)) best = v
  }
  return best
}

/**
 * Card display name (#66): strip a redundant creator-name prefix
 * ("Dark Realms Forge - Foo" by "Dark Realms Forge" -> "Foo"). Full name stays in the tooltip.
 */
export function displayName(name, creatorName) {
  if (!name) return ''
  const n = name.trim()
  if (!creatorName) return n
  const words = creatorName.trim().split(/\s+/)
  // Try the full creator name, then shorter leading word runs ("Dark Realms" of "Dark Realms Forge").
  for (let k = words.length; k >= 1; k--) {
    const prefix = words.slice(0, k).join(' ')
    if (prefix.length < 4) break
    if (!n.toLowerCase().startsWith(prefix.toLowerCase())) continue
    const after = n.slice(prefix.length)
    // Full-name match may be followed by any separator; partial matches need an explicit dash/colon.
    const sep = k === words.length ? /^[\s\-–—:|_]+/ : /^\s*[\-–—:|]\s*/
    const m = after.match(sep)
    if (!m) continue
    const rest = after.slice(m[0].length)
    if (rest.length >= 3) return rest
  }
  return n
}

// Canonical camelCase variant type keys. Each maps (case-insensitively) to a
// VariantType enum name on the API, so they are also valid ?variantType= values.
export const VARIANT_TYPE_ORDER = ['supported', 'presupported', 'unsupported', 'lycheeProject', 'chituboxProject', 'gcode', 'printProject', 'previewImage', 'other']

const BY_LOWER = Object.fromEntries(VARIANT_TYPE_ORDER.map((t) => [t.toLowerCase(), t]))

// API serializes enums as PascalCase ('LycheeProject'); normalize to our keys.
// Unknown/missing types fall into 'other'.
export function normalizeVariantType(type) {
  if (type == null) return 'other'
  return BY_LOWER[String(type).replace(/[\s_-]/g, '').toLowerCase()] || 'other'
}

export function groupVariants(variants) {
  const groups = {}
  for (const v of variants || []) {
    const type = normalizeVariantType(v.variantType)
    ;(groups[type] ||= []).push(v)
  }
  return groups
}

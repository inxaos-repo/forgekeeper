import { test } from 'node:test'
import assert from 'node:assert/strict'
import { normalizeVariantType, groupVariants, VARIANT_TYPE_ORDER } from './variants.js'

test('normalizes PascalCase API values to camelCase keys', () => {
  assert.equal(normalizeVariantType('LycheeProject'), 'lycheeProject')
  assert.equal(normalizeVariantType('Supported'), 'supported')
  assert.equal(normalizeVariantType('lycheeProject'), 'lycheeProject')
  assert.equal(normalizeVariantType('PREVIEWIMAGE'), 'previewImage')
})

test('unknown or missing types fall into other', () => {
  assert.equal(normalizeVariantType('Weird'), 'other')
  assert.equal(normalizeVariantType(null), 'other')
  assert.equal(normalizeVariantType(undefined), 'other')
})

test('groups PascalCase variants so ordered groups are non-empty', () => {
  const g = groupVariants([{ variantType: 'LycheeProject' }, { variantType: 'Supported' }, { variantType: 'Supported' }, {}])
  assert.equal(g.lycheeProject.length, 1)
  assert.equal(g.supported.length, 2)
  assert.equal(g.other.length, 1)
  assert.deepEqual(VARIANT_TYPE_ORDER.filter((t) => g[t]), ['supported', 'lycheeProject', 'other'])
})

test('every key maps case-insensitively to an API VariantType enum name', () => {
  const api = ['Unsupported', 'Supported', 'Presupported', 'LycheeProject', 'ChituboxProject', 'Gcode', 'PrintProject', 'PreviewImage', 'Other']
  for (const k of VARIANT_TYPE_ORDER) assert.ok(api.some((a) => a.toLowerCase() === k.toLowerCase()), k)
})

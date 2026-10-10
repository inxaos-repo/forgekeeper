// Run: node --test src/utils/
import test from 'node:test'
import assert from 'node:assert/strict'
import { pickDefaultPreview } from './viewer.js'

test('picks the largest STL, ignoring non-STL files', () => {
  const v = [
    { id: 1, fileName: 'Mesh_Z1_Y1_X2.stl', fileSizeBytes: 1000 },
    { id: 2, fileName: 'SrcMesh.stl', fileSizeBytes: 14_000_000 },
    { id: 3, fileName: 'huge.zip', fileSizeBytes: 99_000_000 },
  ]
  assert.equal(pickDefaultPreview(v).id, 2)
})

test('returns null when nothing is previewable', () => {
  assert.equal(pickDefaultPreview([{ fileName: 'a.obj' }]), null)
  assert.equal(pickDefaultPreview(undefined), null)
})

import { displayName } from './viewer.js'
test('displayName strips creator prefix only when meaningful', () => {
  assert.equal(displayName('Dark Realms Forge - Ashfall Gate', 'Dark Realms Forge'), 'Ashfall Gate')
  assert.equal(displayName('Dark Realms - Ashfall City - Building 2', 'Dark Realms Forge'), 'Ashfall City - Building 2')
  assert.equal(displayName('Dark Realmsong', 'Dark Realms Forge'), 'Dark Realmsong')
  assert.equal(displayName('M3DM', 'M3DM'), 'M3DM')
  assert.equal(displayName('Foo', null), 'Foo')
})

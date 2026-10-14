// Run: node --test src/utils/
import test from 'node:test'
import assert from 'node:assert/strict'
import { variantCopyPath, variantFolderPath, modelFolderPath, isBrowserViewable, copyText } from './paths.js'

const unc = '\\\\samba.k8s.inxaos.com\\warehousepool\\3d printing'

test('prefers the Windows path when mapped', () => {
  const v = { windowsPath: `${unc}\\C\\M\\a.stl`, absolutePath: '/library/C/M/a.stl' }
  assert.equal(variantCopyPath(v, {}), `${unc}\\C\\M\\a.stl`)
  assert.equal(variantFolderPath(v, {}), `${unc}\\C\\M`)
})

test('falls back to container paths', () => {
  const m = { basePath: '/library/C/M/' }
  assert.equal(variantCopyPath({ filePath: 'sub/a.stl' }, m), '/library/C/M/sub/a.stl')
  assert.equal(variantFolderPath({ filePath: 'sub/a.stl' }, m), '/library/C/M/sub')
  assert.equal(modelFolderPath(m), '/library/C/M/')
  assert.equal(modelFolderPath({ basePath: '/x', windowsBasePath: 'W:\\x' }), 'W:\\x')
})

test('browser-viewable types', () => {
  assert.ok(isBrowserViewable('guide.PDF'))
  assert.ok(!isBrowserViewable('part.stl'))
})

test('copyText uses navigator.clipboard', async () => {
  let got
  assert.equal(await copyText('hi', undefined, { clipboard: { writeText: async (t) => { got = t } } }), true)
  assert.equal(got, 'hi')
  assert.equal(await copyText('', undefined, {}), false)
})

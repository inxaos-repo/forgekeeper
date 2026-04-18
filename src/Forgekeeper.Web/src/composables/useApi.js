/**
 * Forgekeeper API composable
 * Provides typed functions for all backend API endpoints.
 */
import { ref } from 'vue'

const API_BASE = import.meta.env.VITE_API_BASE || '/api/v1'

export function useApi() {
  const loading = ref(false)
  const error = ref(null)

  async function request(path, options = {}) {
    loading.value = true
    error.value = null
    try {
      const headers = { ...options.headers }
      if (options.body) {
        headers['Content-Type'] = 'application/json'
      }
      const res = await fetch(`${API_BASE}${path}`, { ...options, headers })
      if (!res.ok) {
        const text = await res.text().catch(() => '')
        throw new Error(`HTTP ${res.status}: ${res.statusText}${text ? ` — ${text}` : ''}`)
      }
      if (res.status === 204) return null
      return await res.json()
    } catch (e) {
      error.value = e.message
      throw e
    } finally {
      loading.value = false
    }
  }

  const get = (path) => request(path)
  const post = (path, body) =>
    request(path, { method: 'POST', body: body != null ? JSON.stringify(body) : undefined })
  const put = (path, body) =>
    request(path, { method: 'PUT', body: JSON.stringify(body) })
  const patch = (path, body) =>
    request(path, { method: 'PATCH', body: JSON.stringify(body) })
  const del = (path) => request(path, { method: 'DELETE' })

  // ─── Models ────────────────────────────────────────────
  function getModels(params = {}) {
    const qs = buildQuery(params)
    return get(`/models${qs}`)
  }

  function getModel(id) {
    return get(`/models/${id}`)
  }

  function updateModel(id, data) {
    return patch(`/models/${id}`, data)
  }

  function deleteModel(id) {
    return del(`/models/${id}`)
  }

  /** Bulk update multiple models (tag, categorize, etc.) */
  function bulkUpdateModels(data) {
    return post('/models/bulk', data)
  }

  /** Bulk add/remove tags on multiple models */
  function bulkTagModels(data) {
    return post('/models/bulk-tags', data)
  }

  /** Bulk update metadata fields on multiple models (Mp3tag-style) */
  function bulkMetadata(data) {
    return post('/models/bulk-metadata', data)
  }

  /** Rename a model's directory and/or reassign to a different creator */
  function renameModel(id, data) {
    return post(`/models/${id}/rename`, data)
  }

  /** Preview what models would be renamed to under a given template (no files moved) */
  function renamePreview(data) {
    return post('/models/rename/preview', data)
  }

  /** Bulk reassign models to a different creator (optionally moving files on disk) */
  function bulkCreatorReassign(data) {
    return post('/models/bulk-creator', data)
  }

  // ─── Related Models ────────────────────────────────────
  function addRelatedModel(modelId, relatedModelId, relation = 'related') {
    return post(`/models/${modelId}/related`, { relatedModelId, relation })
  }

  function removeRelatedModel(modelId, relatedModelId) {
    return del(`/models/${modelId}/related/${relatedModelId}`)
  }

  // ─── Creators ──────────────────────────────────────────
  function getCreators(params = {}) {
    const qs = buildQuery(params)
    return get(`/creators${qs}`)
  }

  function getCreator(id) {
    return get(`/creators/${id}`)
  }

  function getCreatorModels(id, params = {}) {
    const qs = buildQuery(params)
    return get(`/creators/${id}/models${qs}`)
  }

  // ─── Variants ──────────────────────────────────────────
  function getVariantDownloadUrl(variantId) {
    return `${API_BASE}/variants/${variantId}/download`
  }

  function getVariantThumbnailUrl(variantId) {
    return `${API_BASE}/variants/${variantId}/thumbnail`
  }

  // ─── Tags ──────────────────────────────────────────────
  function getTags(params = {}) {
    const qs = buildQuery(params)
    return get(`/tags${qs}`)
  }

  function addTagToModel(modelId, tagName) {
    return post(`/models/${modelId}/tags`, { tags: [tagName] })
  }

  function removeTagFromModel(modelId, tag) {
    return del(`/models/${modelId}/tags/${encodeURIComponent(tag)}`)
  }

  // ─── Scanning ──────────────────────────────────────────
  function triggerScan() {
    return post('/scan')
  }

  function triggerIncrementalScan() {
    return post('/scan/incremental')
  }

  function getScanStatus() {
    return get('/scan/status')
  }

  // ─── Import ────────────────────────────────────────────
  function processUnsorted() {
    return post('/import/process')
  }

  function getImportQueue(params = {}) {
    const qs = buildQuery(params)
    return get(`/import/queue${qs}`)
  }

  function confirmImport(itemId, data) {
    return post(`/import/queue/${itemId}/confirm`, data)
  }

  function rejectImport(itemId) {
    return del(`/import/queue/${itemId}`)
  }

  /** Alias for rejectImport — dismiss an import queue item */
  function dismissImport(itemId) {
    return del(`/import/queue/${itemId}`)
  }

  // ─── Sources ───────────────────────────────────────────
  function getSources() {
    return get('/sources')
  }

  function getSource(slug) {
    return get(`/sources/${encodeURIComponent(slug)}`)
  }

  function createSource(data) {
    return post('/sources', data)
  }

  function updateSource(slug, data) {
    return patch(`/sources/${encodeURIComponent(slug)}`, data)
  }

  function deleteSource(slug) {
    return del(`/sources/${encodeURIComponent(slug)}`)
  }

  // ─── Stats ─────────────────────────────────────────────
  function getStats() {
    return get('/stats')
  }

  function getCreatorStats() {
    return get('/stats/creators')
  }

  // ─── Plugins ───────────────────────────────────────────
  function getPlugins() {
    return get('/plugins')
  }

  function getPluginStatus(slug) {
    return get(`/plugins/${encodeURIComponent(slug)}/status`)
  }

  function getPluginConfig(slug) {
    return get(`/plugins/${encodeURIComponent(slug)}/config`)
  }

  function updatePluginConfig(slug, config) {
    return put(`/plugins/${encodeURIComponent(slug)}/config`, config)
  }

  function triggerPluginSync(slug) {
    return post(`/plugins/${encodeURIComponent(slug)}/sync`)
  }

  function getPluginAdminHtml(slug) {
    return get(`/plugins/${encodeURIComponent(slug)}/admin`)
  }

  return {
    loading,
    error,
    // Raw helpers
    get,
    post,
    put,
    patch,
    del,
    // Models
    getModels,
    getModel,
    updateModel,
    deleteModel,
    bulkUpdateModels,
    bulkTagModels,
    bulkMetadata,
    renameModel,
    renamePreview,
    bulkCreatorReassign,
    // Related Models
    addRelatedModel,
    removeRelatedModel,
    // Creators
    getCreators,
    getCreator,
    getCreatorModels,
    // Variants
    getVariantDownloadUrl,
    getVariantThumbnailUrl,
    // Tags
    getTags,
    addTagToModel,
    removeTagFromModel,
    // Scanning
    triggerScan,
    triggerIncrementalScan,
    getScanStatus,
    // Import
    processUnsorted,
    getImportQueue,
    confirmImport,
    rejectImport,
    dismissImport,
    // Sources
    getSources,
    getSource,
    createSource,
    updateSource,
    deleteSource,
    // Stats
    getStats,
    getCreatorStats,
    // Plugins
    getPlugins,
    getPluginStatus,
    getPluginConfig,
    updatePluginConfig,
    triggerPluginSync,
    getPluginAdminHtml,
  }
}

// ─── Helpers ───────────────────────────────────────────────
function buildQuery(params) {
  const entries = Object.entries(params).filter(
    ([, v]) => v !== undefined && v !== null && v !== ''
  )
  if (!entries.length) return ''
  return '?' + entries.map(([k, v]) => `${encodeURIComponent(k)}=${encodeURIComponent(v)}`).join('&')
}

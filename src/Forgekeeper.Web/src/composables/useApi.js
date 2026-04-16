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
      // Only set Content-Type for requests with a body
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
  const patch = (path, body) =>
    request(path, { method: 'PATCH', body: JSON.stringify(body) })
  const del = (path) => request(path, { method: 'DELETE' })

  // ─── Models ────────────────────────────────────────────
  /**
   * Search/browse models with filters.
   * @param {Object} params - query params (search, source, creatorId, category,
   *   gameSystem, scale, tag, printed, sortBy, sortDir, page, pageSize)
   */
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

  return {
    loading,
    error,
    // Raw helpers
    get,
    post,
    patch,
    del,
    // Models
    getModels,
    getModel,
    updateModel,
    deleteModel,
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
    // Sources
    getSources,
    getSource,
    createSource,
    updateSource,
    deleteSource,
    // Stats
    getStats,
    getCreatorStats,
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

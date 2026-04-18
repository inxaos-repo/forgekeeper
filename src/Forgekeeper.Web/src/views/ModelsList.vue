<!--
  ModelsList.vue — Main browse/search page
  Search bar, filter sidebar, sort options, paginated grid of model cards
  Supports bulk selection + Mp3tag-style bulk metadata editor panel
-->
<script setup>
import { ref, reactive, computed, watch, onMounted } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { useApi } from '../composables/useApi.js'
import ModelCard from '../components/ModelCard.vue'
import FilterSidebar from '../components/FilterSidebar.vue'
import CreatorAutocomplete from '../components/CreatorAutocomplete.vue'

const route = useRoute()
const router = useRouter()
const api = useApi()

const models = ref([])
const totalCount = ref(0)
const totalPages = ref(0)
const searchQuery = ref(route.query.search || '')
const showFilters = ref(false)

const filters = reactive({
  source: route.query.source || undefined,
  creatorId: route.query.creatorId || undefined,
  category: route.query.category || undefined,
  gameSystem: route.query.gameSystem || undefined,
  scale: route.query.scale || undefined,
  tag: route.query.tag || undefined,
  printed: route.query.printed || undefined,
  minRating: route.query.minRating || undefined,
  licenseType: route.query.licenseType || undefined,
  collectionName: route.query.collectionName || undefined,
})

const sortBy = ref(route.query.sortBy || 'name')
const sortDir = ref(route.query.sortDir || 'asc')
const page = ref(parseInt(route.query.page) || 1)
const pageSize = ref(parseInt(route.query.pageSize) || 48)

const sortOptions = [
  { value: 'name', label: 'Name' },
  { value: 'createdAt', label: 'Date Added' },
  { value: 'fileCount', label: 'File Count' },
  { value: 'totalSizeBytes', label: 'Size' },
  { value: 'rating', label: 'Rating' },
  { value: 'creatorName', label: 'Creator' },
]

// ─── Bulk Selection ──────────────────────────────────────
const selectedIds = ref(new Set())
const bulkMode = ref(false)
const bulkProcessing = ref(false)
const bulkActiveTab = ref('metadata')
const bulkShowSummary = ref(false)
const bulkSuccessMsg = ref('')
const bulkSuccessTimer = ref(null)

// Bulk editor fields — only non-empty/non-zero values are sent to API
const bulk = reactive({
  // Metadata tab
  category: '',
  gameSystem: '',
  scale: '',
  licenseType: '',
  // Status tab
  rating: 0,
  printStatus: '',
  updateNotes: false,   // checkbox to explicitly set notes (allows clearing)
  notes: '',
  // Organization tab
  creatorName: '',
  collectionName: '',
  addTagsInput: '',     // comma-separated
  removeTagsInput: '',  // comma-separated
})

const categories = [
  'Miniature', 'Terrain', 'Vehicle', 'Prop', 'Bust', 'Base', 'Scenery', 'Accessory', 'Other',
]
const gameSystems = [
  'Warhammer 40K', 'Age of Sigmar', 'D&D', 'Pathfinder', 'Star Wars Legion',
  'Bolt Action', 'Malifaux', 'Kill Team', 'Necromunda', 'Other',
]
const scaleOptions = ['28mm', '32mm', '35mm', '54mm', '75mm', '1:72', '1:48', '1:35', '1:24']
const licenseTypes = [
  { value: 'personal', label: 'Personal Use' },
  { value: 'commercial', label: 'Commercial' },
  { value: 'creative-commons', label: 'Creative Commons' },
  { value: 'unknown', label: 'Unknown' },
]
const printStatuses = [
  { value: 'unprinted', label: 'Unprinted' },
  { value: 'queued', label: 'Queued' },
  { value: 'printed', label: 'Printed' },
  { value: 'failed', label: 'Failed' },
]

const allSelected = computed(() =>
  models.value.length > 0 && models.value.every((m) => selectedIds.value.has(m.id))
)

function toggleSelectAll() {
  if (allSelected.value) {
    selectedIds.value = new Set()
  } else {
    selectedIds.value = new Set(models.value.map((m) => m.id))
  }
}

function toggleSelect(id) {
  const s = new Set(selectedIds.value)
  if (s.has(id)) s.delete(id)
  else s.add(id)
  selectedIds.value = s
}

function resetBulkFields() {
  bulk.category = ''
  bulk.gameSystem = ''
  bulk.scale = ''
  bulk.licenseType = ''
  bulk.rating = 0
  bulk.printStatus = ''
  bulk.updateNotes = false
  bulk.notes = ''
  bulk.creatorName = ''
  bulk.collectionName = ''
  bulk.addTagsInput = ''
  bulk.removeTagsInput = ''
  bulkShowSummary.value = false
  bulkActiveTab.value = 'metadata'
}

function cancelBulk() {
  bulkMode.value = false
  selectedIds.value = new Set()
  resetBulkFields()
}

// Parse comma-separated tag strings
function parseTags(str) {
  return str.split(',').map((t) => t.trim().toLowerCase()).filter(Boolean)
}

// Build summary of what will change
const changeSummary = computed(() => {
  const items = []
  if (bulk.category) items.push(`Category → ${bulk.category}`)
  if (bulk.gameSystem) items.push(`Game System → ${bulk.gameSystem}`)
  if (bulk.scale) items.push(`Scale → ${bulk.scale}`)
  if (bulk.licenseType) {
    const l = licenseTypes.find((x) => x.value === bulk.licenseType)
    items.push(`License → ${l?.label ?? bulk.licenseType}`)
  }
  if (bulk.rating > 0) items.push(`Rating → ${'★'.repeat(bulk.rating)}`)
  if (bulk.printStatus) {
    const p = printStatuses.find((x) => x.value === bulk.printStatus)
    items.push(`Print Status → ${p?.label ?? bulk.printStatus}`)
  }
  if (bulk.updateNotes) items.push(`Notes → "${bulk.notes || '(cleared)'}"`)
  if (bulk.creatorName) items.push(`Creator → ${bulk.creatorName}`)
  if (bulk.collectionName) items.push(`Collection → ${bulk.collectionName}`)
  const addTags = parseTags(bulk.addTagsInput)
  const removeTags = parseTags(bulk.removeTagsInput)
  if (addTags.length) items.push(`Add tags: ${addTags.join(', ')}`)
  if (removeTags.length) items.push(`Remove tags: ${removeTags.join(', ')}`)
  return items
})

const hasChanges = computed(() => changeSummary.value.length > 0)

function requestApply() {
  if (!hasChanges.value) return
  bulkShowSummary.value = true
}

async function executeBulkAction() {
  if (!selectedIds.value.size || !hasChanges.value) return
  bulkProcessing.value = true
  try {
    const ids = [...selectedIds.value]
    const payload = { modelIds: ids }

    if (bulk.category) payload.category = bulk.category
    if (bulk.gameSystem) payload.gameSystem = bulk.gameSystem
    if (bulk.scale) payload.scale = bulk.scale
    if (bulk.licenseType) payload.licenseType = bulk.licenseType
    if (bulk.rating > 0) payload.rating = bulk.rating
    if (bulk.printStatus) payload.printStatus = bulk.printStatus
    if (bulk.updateNotes) payload.notes = bulk.notes
    if (bulk.creatorName) payload.creatorName = bulk.creatorName
    if (bulk.collectionName) payload.collectionName = bulk.collectionName

    const addTags = parseTags(bulk.addTagsInput)
    const removeTags = parseTags(bulk.removeTagsInput)
    if (addTags.length) payload.addTags = addTags
    if (removeTags.length) payload.removeTags = removeTags

    await api.bulkMetadata(payload)

    // Success
    const count = ids.length
    showBulkSuccess(`Applied to ${count} model${count === 1 ? '' : 's'}`)
    bulkShowSummary.value = false
    resetBulkFields()
    selectedIds.value = new Set()
    await fetchModels()
  } catch {
    // error shown by api.error
    bulkShowSummary.value = false
  } finally {
    bulkProcessing.value = false
  }
}

function showBulkSuccess(msg) {
  bulkSuccessMsg.value = msg
  if (bulkSuccessTimer.value) clearTimeout(bulkSuccessTimer.value)
  bulkSuccessTimer.value = setTimeout(() => { bulkSuccessMsg.value = '' }, 3500)
}

// ─── Bulk Actions: Reorganize / Parse / Move Creator ─────────
// Action type currently selected in the Actions tab
const bulkActionType = ref('')  // '' | 'reorganize' | 'parseFilename' | 'moveCreator'

// ─ Reorganize state ───────────────────────────────
const showReorgModal = ref(false)
const reorgTemplate = ref('{source}/{creator}/{name}')
const reorgPresets = ref([])
const selectedReorgPreset = ref('')
const reorgPreviewRows = ref([])
const reorgPreviewing = ref(false)
const reorgApplying = ref(false)
const reorgResult = ref(null)
const showSaveReorgPreset = ref(false)
const saveReorgPresetName = ref('')

// ─ Parse from Filename state ────────────────────────
const showParseModal = ref(false)
const parseTemplate = ref('{creator} - {name}')
const parsePresets = ref([])
const selectedParsePreset = ref('')
const parsePreviewRows = ref([])
const parsePreviewing = ref(false)
const parseApplying = ref(false)
const parseResult = ref(null)
const showSaveParsePreset = ref(false)
const saveParsePresetName = ref('')

const parseMatchCount = computed(() => parsePreviewRows.value.filter((r) => r.matched).length)
const parseMatchPct = computed(() =>
  parsePreviewRows.value.length
    ? Math.round((parseMatchCount.value / parsePreviewRows.value.length) * 100)
    : 0
)

// ─ Move to Creator state ────────────────────────────
const moveCreatorName = ref('')
const moveCreatorApplying = ref(false)

// ─ Preset helpers ────────────────────────────────
async function loadReorgPresets() {
  try {
    reorgPresets.value = await api.getSavedTemplates('reorganize') || []
  } catch { reorgPresets.value = [] }
}

async function loadParsePresets() {
  try {
    parsePresets.value = await api.getSavedTemplates('parse') || []
  } catch { parsePresets.value = [] }
}

function applyReorgPreset(id) {
  const preset = reorgPresets.value.find((p) => String(p.id) === String(id))
  if (preset) reorgTemplate.value = preset.template
}

function applyParsePreset(id) {
  const preset = parsePresets.value.find((p) => String(p.id) === String(id))
  if (preset) parseTemplate.value = preset.template
}

// ─ Reorganize functions ────────────────────────────
async function openReorgModal() {
  reorgPreviewRows.value = []
  reorgResult.value = null
  showSaveReorgPreset.value = false
  await loadReorgPresets()
  showReorgModal.value = true
  await runReorgPreview()
}

async function runReorgPreview() {
  if (!reorgTemplate.value) return
  reorgPreviewing.value = true
  try {
    const ids = [...selectedIds.value]
    const data = await api.reorganizePreview(reorgTemplate.value, ids, 100)
    reorgPreviewRows.value = data?.items || data || []
  } catch {
    reorgPreviewRows.value = []
  } finally {
    reorgPreviewing.value = false
  }
}

async function applyReorganize() {
  reorgApplying.value = true
  try {
    const ids = [...selectedIds.value]
    const result = await api.reorganize(reorgTemplate.value, ids)
    reorgResult.value = result
    if (selectedReorgPreset.value) api.useTemplate(selectedReorgPreset.value).catch(() => {})
    showBulkSuccess(`Reorganized: ${result?.moved ?? '?'} moved, ${result?.skipped ?? 0} skipped, ${result?.failed ?? 0} failed`)
    showReorgModal.value = false
    selectedIds.value = new Set()
    await fetchModels()
  } catch {
    // error shown by api.error
  } finally {
    reorgApplying.value = false
  }
}

async function saveReorgPresetFn() {
  if (!saveReorgPresetName.value.trim()) return
  try {
    await api.createTemplate({ name: saveReorgPresetName.value.trim(), template: reorgTemplate.value, type: 'reorganize' })
    saveReorgPresetName.value = ''
    showSaveReorgPreset.value = false
    await loadReorgPresets()
  } catch { /* error shown by api.error */ }
}

// ─ Parse from Filename functions ─────────────────────
async function openParseModal() {
  parsePreviewRows.value = []
  parseResult.value = null
  showSaveParsePreset.value = false
  await loadParsePresets()
  showParseModal.value = true
  await runParsePreview()
}

async function runParsePreview() {
  if (!parseTemplate.value) return
  parsePreviewing.value = true
  try {
    const ids = [...selectedIds.value]
    const data = await api.parseFilenamePreview(parseTemplate.value, ids, 100)
    parsePreviewRows.value = data?.items || data || []
  } catch {
    parsePreviewRows.value = []
  } finally {
    parsePreviewing.value = false
  }
}

async function applyParseFN() {
  parseApplying.value = true
  try {
    const ids = [...selectedIds.value]
    const result = await api.parseFilenameApply(parseTemplate.value, ids)
    parseResult.value = result
    if (selectedParsePreset.value) api.useTemplate(selectedParsePreset.value).catch(() => {})
    const updated = result?.updated ?? result?.applied ?? '?'
    showBulkSuccess(`Parsed filenames: ${updated} updated`)
    showParseModal.value = false
    selectedIds.value = new Set()
    await fetchModels()
  } catch {
    // error shown by api.error
  } finally {
    parseApplying.value = false
  }
}

async function saveParseFn() {
  if (!saveParsePresetName.value.trim()) return
  try {
    await api.createTemplate({ name: saveParsePresetName.value.trim(), template: parseTemplate.value, type: 'parse' })
    saveParsePresetName.value = ''
    showSaveParsePreset.value = false
    await loadParsePresets()
  } catch { /* error shown by api.error */ }
}

// ─ Move to Creator functions ─────────────────────────
async function applyMoveCreator() {
  if (!moveCreatorName.value) return
  moveCreatorApplying.value = true
  try {
    const ids = [...selectedIds.value]
    await api.bulkCreatorReassign({ modelIds: ids, creatorName: moveCreatorName.value })
    showBulkSuccess(`Moved ${ids.length} model${ids.length === 1 ? '' : 's'} to ${moveCreatorName.value}`)
    moveCreatorName.value = ''
    selectedIds.value = new Set()
    bulkActionType.value = ''
    await fetchModels()
  } catch {
    // error shown by api.error
  } finally {
    moveCreatorApplying.value = false
  }
}

// ─── Fetch + Query Sync ──────────────────────────────────
async function fetchModels() {
  try {
    const params = {
      query: searchQuery.value || undefined,
      ...filters,
      tags: filters.tag || undefined,
      tag: undefined,
      sortBy: sortBy.value,
      sortDescending: sortDir.value === 'desc' ? true : undefined,
      page: page.value,
      pageSize: pageSize.value,
    }
    const result = await api.getModels(params)
    models.value = result?.items || result || []
    totalCount.value = result?.totalCount ?? models.value.length
    totalPages.value = result?.totalPages ?? Math.ceil(totalCount.value / pageSize.value)
  } catch {
    models.value = []
  }
}

function syncQueryParams() {
  const query = {}
  if (searchQuery.value) query.search = searchQuery.value
  if (filters.source) query.source = filters.source
  if (filters.creatorId) query.creatorId = filters.creatorId
  if (filters.category) query.category = filters.category
  if (filters.gameSystem) query.gameSystem = filters.gameSystem
  if (filters.scale) query.scale = filters.scale
  if (filters.tag) query.tag = filters.tag
  if (filters.printed) query.printed = filters.printed
  if (filters.minRating) query.minRating = filters.minRating
  if (filters.licenseType) query.licenseType = filters.licenseType
  if (filters.collectionName) query.collectionName = filters.collectionName
  if (sortBy.value !== 'name') query.sortBy = sortBy.value
  if (sortDir.value !== 'asc') query.sortDir = sortDir.value
  if (page.value > 1) query.page = page.value
  router.replace({ query })
}

function onSearch() {
  page.value = 1
  syncQueryParams()
  fetchModels()
}

function onFiltersUpdate(newFilters) {
  Object.assign(filters, newFilters)
  page.value = 1
  syncQueryParams()
  fetchModels()
}

function onSort(field) {
  if (sortBy.value === field) {
    sortDir.value = sortDir.value === 'asc' ? 'desc' : 'asc'
  } else {
    sortBy.value = field
    sortDir.value = 'asc'
  }
  page.value = 1
  syncQueryParams()
  fetchModels()
}

function goToPage(p) {
  page.value = p
  syncQueryParams()
  fetchModels()
  window.scrollTo(0, 0)
}

const pageRange = computed(() => {
  const total = totalPages.value
  const current = page.value
  const range = []
  const delta = 2
  for (let i = Math.max(1, current - delta); i <= Math.min(total, current + delta); i++) {
    range.push(i)
  }
  return range
})

// Watch route query for external navigation (e.g., global search)
watch(() => route.query.search, (val) => {
  if (val !== searchQuery.value) {
    searchQuery.value = val || ''
    page.value = 1
    fetchModels()
  }
})

watch(() => route.query.creatorId, (val) => {
  if (val !== filters.creatorId) {
    filters.creatorId = val || undefined
    page.value = 1
    fetchModels()
  }
})

onMounted(fetchModels)
</script>

<template>
  <div>
    <!-- Search + Sort bar -->
    <div class="flex flex-col sm:flex-row items-stretch sm:items-center gap-3 mb-6">
      <form @submit.prevent="onSearch" class="flex-1 relative">
        <input
          v-model="searchQuery"
          type="text"
          placeholder="Search models, creators, tags..."
          class="w-full bg-forge-card border border-forge-border rounded-lg px-4 py-2.5 text-sm text-forge-text placeholder-forge-text-muted focus:outline-none focus:border-forge-accent focus:ring-1 focus:ring-forge-accent transition-colors"
        />
        <button
          type="submit"
          class="absolute right-3 top-1/2 -translate-y-1/2 text-forge-text-muted hover:text-forge-accent"
        >🔍</button>
      </form>

      <div class="flex items-center gap-2">
        <select
          :value="sortBy"
          @change="onSort($event.target.value)"
          class="bg-forge-card border border-forge-border rounded-lg px-3 py-2.5 text-sm text-forge-text focus:outline-none focus:border-forge-accent"
        >
          <option v-for="opt in sortOptions" :key="opt.value" :value="opt.value">
            {{ opt.label }}
          </option>
        </select>
        <button
          @click="sortDir = sortDir === 'asc' ? 'desc' : 'asc'; onSearch()"
          class="p-2.5 bg-forge-card border border-forge-border rounded-lg text-forge-text-muted hover:text-forge-accent transition-colors"
          :title="sortDir === 'asc' ? 'Ascending' : 'Descending'"
        >
          {{ sortDir === 'asc' ? '↑' : '↓' }}
        </button>

        <!-- Bulk mode toggle -->
        <button
          @click="bulkMode = !bulkMode; if (!bulkMode) cancelBulk()"
          :class="[
            'p-2.5 border rounded-lg text-sm font-medium transition-colors',
            bulkMode
              ? 'bg-forge-accent/15 border-forge-accent text-forge-accent'
              : 'bg-forge-card border-forge-border text-forge-text-muted hover:text-forge-accent',
          ]"
          title="Bulk select"
        >
          ☑
        </button>

        <button
          @click="showFilters = !showFilters"
          class="lg:hidden p-2.5 bg-forge-card border border-forge-border rounded-lg text-forge-text-muted hover:text-forge-accent"
        >
          🔧
        </button>
      </div>
    </div>

    <!-- ─── Bulk Editor Panel ─────────────────────────────── -->
    <transition name="slide-down">
      <div
        v-if="bulkMode && selectedIds.size > 0"
        class="bg-forge-card border border-forge-accent/40 rounded-xl mb-6 overflow-hidden shadow-lg"
      >
        <!-- Header -->
        <div class="flex items-center justify-between px-4 py-3 bg-forge-accent/10 border-b border-forge-accent/20">
          <div class="flex items-center gap-3">
            <span class="text-forge-accent font-semibold text-sm">
              ✏️ {{ selectedIds.size }} model{{ selectedIds.size === 1 ? '' : 's' }} selected
            </span>
            <span v-if="hasChanges" class="text-xs text-forge-text-muted">
              {{ changeSummary.length }} change{{ changeSummary.length === 1 ? '' : 's' }} pending
            </span>
          </div>
          <button
            @click="cancelBulk"
            class="text-forge-text-muted hover:text-forge-danger text-sm transition-colors"
          >
            ✕ Cancel
          </button>
        </div>

        <!-- Tab bar -->
        <div class="flex border-b border-forge-border">
          <button
            v-for="tab in [
              { id: 'metadata', label: '📋 Metadata' },
              { id: 'status', label: '📊 Status' },
              { id: 'organization', label: '🗂 Organization' },
              { id: 'actions', label: '⚡ Actions' },
            ]"
            :key="tab.id"
            @click="bulkActiveTab = tab.id"
            :class="[
              'px-4 py-2.5 text-sm font-medium transition-colors border-b-2 -mb-px',
              bulkActiveTab === tab.id
                ? 'border-forge-accent text-forge-accent'
                : 'border-transparent text-forge-text-muted hover:text-forge-text',
            ]"
          >
            {{ tab.label }}
          </button>
        </div>

        <!-- Tab: Metadata -->
        <div v-if="bulkActiveTab === 'metadata'" class="p-4 grid grid-cols-1 sm:grid-cols-2 gap-4">
          <div>
            <label class="block text-xs font-medium text-forge-text-muted mb-1">Category</label>
            <select
              v-model="bulk.category"
              class="w-full bg-forge-bg border border-forge-border rounded-lg px-3 py-1.5 text-sm text-forge-text focus:outline-none focus:border-forge-accent"
            >
              <option value="">— no change —</option>
              <option v-for="cat in categories" :key="cat" :value="cat">{{ cat }}</option>
            </select>
          </div>

          <div>
            <label class="block text-xs font-medium text-forge-text-muted mb-1">Game System</label>
            <select
              v-model="bulk.gameSystem"
              class="w-full bg-forge-bg border border-forge-border rounded-lg px-3 py-1.5 text-sm text-forge-text focus:outline-none focus:border-forge-accent"
            >
              <option value="">— no change —</option>
              <option v-for="gs in gameSystems" :key="gs" :value="gs">{{ gs }}</option>
            </select>
          </div>

          <div>
            <label class="block text-xs font-medium text-forge-text-muted mb-1">Scale</label>
            <select
              v-model="bulk.scale"
              class="w-full bg-forge-bg border border-forge-border rounded-lg px-3 py-1.5 text-sm text-forge-text focus:outline-none focus:border-forge-accent"
            >
              <option value="">— no change —</option>
              <option v-for="s in scaleOptions" :key="s" :value="s">{{ s }}</option>
            </select>
          </div>

          <div>
            <label class="block text-xs font-medium text-forge-text-muted mb-1">License Type</label>
            <select
              v-model="bulk.licenseType"
              class="w-full bg-forge-bg border border-forge-border rounded-lg px-3 py-1.5 text-sm text-forge-text focus:outline-none focus:border-forge-accent"
            >
              <option value="">— no change —</option>
              <option v-for="lic in licenseTypes" :key="lic.value" :value="lic.value">{{ lic.label }}</option>
            </select>
          </div>
        </div>

        <!-- Tab: Status -->
        <div v-if="bulkActiveTab === 'status'" class="p-4 grid grid-cols-1 sm:grid-cols-2 gap-4">
          <div>
            <label class="block text-xs font-medium text-forge-text-muted mb-1">Rating</label>
            <div class="flex items-center gap-1">
              <button
                v-for="star in 5"
                :key="star"
                @click="bulk.rating = bulk.rating === star ? 0 : star"
                :class="[
                  'text-2xl transition-colors leading-none',
                  star <= bulk.rating ? 'text-yellow-400' : 'text-forge-border hover:text-yellow-300',
                ]"
              >★</button>
              <span v-if="bulk.rating" class="text-xs text-forge-text-muted ml-2">
                {{ bulk.rating }}/5
              </span>
              <span v-else class="text-xs text-forge-text-muted ml-2">no change</span>
            </div>
          </div>

          <div>
            <label class="block text-xs font-medium text-forge-text-muted mb-1">Print Status</label>
            <select
              v-model="bulk.printStatus"
              class="w-full bg-forge-bg border border-forge-border rounded-lg px-3 py-1.5 text-sm text-forge-text focus:outline-none focus:border-forge-accent"
            >
              <option value="">— no change —</option>
              <option v-for="ps in printStatuses" :key="ps.value" :value="ps.value">{{ ps.label }}</option>
            </select>
          </div>

          <div class="sm:col-span-2">
            <div class="flex items-center gap-2 mb-1">
              <input
                id="bulk-update-notes"
                v-model="bulk.updateNotes"
                type="checkbox"
                class="rounded border-forge-border bg-forge-bg text-forge-accent focus:ring-forge-accent"
              />
              <label for="bulk-update-notes" class="text-xs font-medium text-forge-text-muted cursor-pointer">
                Update Notes (overwrites existing notes on all selected models)
              </label>
            </div>
            <textarea
              v-if="bulk.updateNotes"
              v-model="bulk.notes"
              rows="3"
              placeholder="Notes to apply to all selected models..."
              class="w-full bg-forge-bg border border-forge-border rounded-lg px-3 py-1.5 text-sm text-forge-text placeholder-forge-text-muted focus:outline-none focus:border-forge-accent resize-none"
            />
          </div>
        </div>

        <!-- Tab: Organization -->
        <div v-if="bulkActiveTab === 'organization'" class="p-4 grid grid-cols-1 sm:grid-cols-2 gap-4">
          <div>
            <label class="block text-xs font-medium text-forge-text-muted mb-1">Reassign Creator</label>
            <CreatorAutocomplete
              v-model="bulk.creatorName"
              placeholder="Search creators..."
            />
          </div>

          <div>
            <label class="block text-xs font-medium text-forge-text-muted mb-1">Collection Name</label>
            <input
              v-model="bulk.collectionName"
              type="text"
              placeholder="e.g. Space Marines Vanguard..."
              class="w-full bg-forge-bg border border-forge-border rounded-lg px-3 py-1.5 text-sm text-forge-text placeholder-forge-text-muted focus:outline-none focus:border-forge-accent"
            />
          </div>

          <div>
            <label class="block text-xs font-medium text-forge-text-muted mb-1">
              Add Tags
              <span class="font-normal text-forge-text-muted">(comma-separated)</span>
            </label>
            <input
              v-model="bulk.addTagsInput"
              type="text"
              placeholder="space marines, primaris, painted..."
              class="w-full bg-forge-bg border border-forge-border rounded-lg px-3 py-1.5 text-sm text-forge-text placeholder-forge-text-muted focus:outline-none focus:border-forge-accent"
            />
            <!-- Preview chips -->
            <div v-if="parseTags(bulk.addTagsInput).length" class="flex flex-wrap gap-1 mt-1.5">
              <span
                v-for="tag in parseTags(bulk.addTagsInput)"
                :key="tag"
                class="inline-flex items-center gap-1 px-2 py-0.5 rounded-full text-xs bg-forge-accent/15 text-forge-accent"
              >
                + {{ tag }}
              </span>
            </div>
          </div>

          <div>
            <label class="block text-xs font-medium text-forge-text-muted mb-1">
              Remove Tags
              <span class="font-normal text-forge-text-muted">(comma-separated)</span>
            </label>
            <input
              v-model="bulk.removeTagsInput"
              type="text"
              placeholder="old-tag, draft..."
              class="w-full bg-forge-bg border border-forge-border rounded-lg px-3 py-1.5 text-sm text-forge-text placeholder-forge-text-muted focus:outline-none focus:border-forge-accent"
            />
            <!-- Preview chips -->
            <div v-if="parseTags(bulk.removeTagsInput).length" class="flex flex-wrap gap-1 mt-1.5">
              <span
                v-for="tag in parseTags(bulk.removeTagsInput)"
                :key="tag"
                class="inline-flex items-center gap-1 px-2 py-0.5 rounded-full text-xs bg-forge-danger/15 text-forge-danger"
              >
                − {{ tag }}
              </span>
            </div>
          </div>
        </div>

        <!-- Tab: Actions -->
        <div v-if="bulkActiveTab === 'actions'" class="p-4 space-y-4">
          <!-- Action selector -->
          <div>
            <label class="block text-xs font-medium text-forge-text-muted mb-1">Action</label>
            <select
              v-model="bulkActionType"
              class="w-full sm:w-72 bg-forge-bg border border-forge-border rounded-lg px-3 py-1.5 text-sm text-forge-text focus:outline-none focus:border-forge-accent"
            >
              <option value="">— select an action —</option>
              <option value="reorganize">Reorganize Directories...</option>
              <option value="parseFilename">Parse from Filename...</option>
              <option value="moveCreator">Move to Creator...</option>
            </select>
          </div>

          <!-- Reorganize config -->
          <div v-if="bulkActionType === 'reorganize'" class="space-y-3">
            <div class="flex flex-wrap gap-2 items-end">
              <div class="flex-1 min-w-48">
                <label class="block text-xs font-medium text-forge-text-muted mb-1">Template</label>
                <input
                  v-model="reorgTemplate"
                  type="text"
                  placeholder="{source}/{creator}/{name}"
                  class="w-full bg-forge-bg border border-forge-border rounded-lg px-3 py-1.5 text-sm text-forge-text placeholder-forge-text-muted focus:outline-none focus:border-forge-accent font-mono"
                />
              </div>
              <div v-if="reorgPresets.length" class="w-48">
                <label class="block text-xs font-medium text-forge-text-muted mb-1">Saved Presets</label>
                <select
                  v-model="selectedReorgPreset"
                  @change="applyReorgPreset(selectedReorgPreset)"
                  class="w-full bg-forge-bg border border-forge-border rounded-lg px-3 py-1.5 text-sm text-forge-text focus:outline-none focus:border-forge-accent"
                >
                  <option value="">— choose preset —</option>
                  <option v-for="p in reorgPresets" :key="p.id" :value="String(p.id)">
                    {{ p.name }}
                  </option>
                </select>
              </div>
            </div>
            <p class="text-xs text-forge-text-muted">
              Available tokens: <code class="bg-forge-bg px-1 rounded">{source}</code>
              <code class="bg-forge-bg px-1 rounded ml-1">{creator}</code>
              <code class="bg-forge-bg px-1 rounded ml-1">{name}</code>
              <code class="bg-forge-bg px-1 rounded ml-1">{category}</code>
            </p>
            <div class="flex gap-2">
              <button
                @click="openReorgModal"
                :disabled="!reorgTemplate || !selectedIds.size"
                class="px-4 py-1.5 bg-forge-accent hover:bg-forge-accent-hover text-forge-bg rounded-lg text-sm font-medium transition-colors disabled:opacity-40 disabled:cursor-not-allowed"
              >
                Preview &amp; Apply →
              </button>
              <button
                @click="showSaveReorgPreset = !showSaveReorgPreset"
                class="px-3 py-1.5 bg-forge-card border border-forge-border rounded-lg text-sm text-forge-text-muted hover:text-forge-text transition-colors"
              >
                Save as Preset
              </button>
            </div>
            <div v-if="showSaveReorgPreset" class="flex gap-2 items-center">
              <input
                v-model="saveReorgPresetName"
                type="text"
                placeholder="Preset name..."
                class="flex-1 bg-forge-bg border border-forge-border rounded-lg px-3 py-1.5 text-sm text-forge-text placeholder-forge-text-muted focus:outline-none focus:border-forge-accent"
                @keydown.enter="saveReorgPresetFn"
              />
              <button
                @click="saveReorgPresetFn"
                :disabled="!saveReorgPresetName.trim()"
                class="px-3 py-1.5 bg-forge-accent hover:bg-forge-accent-hover text-forge-bg rounded-lg text-sm font-medium transition-colors disabled:opacity-40"
              >
                Save
              </button>
              <button @click="showSaveReorgPreset = false" class="text-sm text-forge-text-muted hover:text-forge-text">Cancel</button>
            </div>
          </div>

          <!-- Parse from Filename config -->
          <div v-if="bulkActionType === 'parseFilename'" class="space-y-3">
            <div class="flex flex-wrap gap-2 items-end">
              <div class="flex-1 min-w-48">
                <label class="block text-xs font-medium text-forge-text-muted mb-1">Template</label>
                <input
                  v-model="parseTemplate"
                  type="text"
                  placeholder="{creator} - {name}"
                  class="w-full bg-forge-bg border border-forge-border rounded-lg px-3 py-1.5 text-sm text-forge-text placeholder-forge-text-muted focus:outline-none focus:border-forge-accent font-mono"
                />
              </div>
              <div v-if="parsePresets.length" class="w-48">
                <label class="block text-xs font-medium text-forge-text-muted mb-1">Saved Presets</label>
                <select
                  v-model="selectedParsePreset"
                  @change="applyParsePreset(selectedParsePreset)"
                  class="w-full bg-forge-bg border border-forge-border rounded-lg px-3 py-1.5 text-sm text-forge-text focus:outline-none focus:border-forge-accent"
                >
                  <option value="">— choose preset —</option>
                  <option v-for="p in parsePresets" :key="p.id" :value="String(p.id)">
                    {{ p.name }}
                  </option>
                </select>
              </div>
            </div>
            <p class="text-xs text-forge-text-muted">
              Extract from directory names. Tokens:
              <code class="bg-forge-bg px-1 rounded">{creator}</code>
              <code class="bg-forge-bg px-1 rounded ml-1">{name}</code>
              <code class="bg-forge-bg px-1 rounded ml-1">{category}</code>
              — use any separator between them.
            </p>
            <div class="flex gap-2">
              <button
                @click="openParseModal"
                :disabled="!parseTemplate || !selectedIds.size"
                class="px-4 py-1.5 bg-forge-accent hover:bg-forge-accent-hover text-forge-bg rounded-lg text-sm font-medium transition-colors disabled:opacity-40 disabled:cursor-not-allowed"
              >
                Preview &amp; Apply →
              </button>
              <button
                @click="showSaveParsePreset = !showSaveParsePreset"
                class="px-3 py-1.5 bg-forge-card border border-forge-border rounded-lg text-sm text-forge-text-muted hover:text-forge-text transition-colors"
              >
                Save as Preset
              </button>
            </div>
            <div v-if="showSaveParsePreset" class="flex gap-2 items-center">
              <input
                v-model="saveParsePresetName"
                type="text"
                placeholder="Preset name..."
                class="flex-1 bg-forge-bg border border-forge-border rounded-lg px-3 py-1.5 text-sm text-forge-text placeholder-forge-text-muted focus:outline-none focus:border-forge-accent"
                @keydown.enter="saveParseFn"
              />
              <button
                @click="saveParseFn"
                :disabled="!saveParsePresetName.trim()"
                class="px-3 py-1.5 bg-forge-accent hover:bg-forge-accent-hover text-forge-bg rounded-lg text-sm font-medium transition-colors disabled:opacity-40"
              >
                Save
              </button>
              <button @click="showSaveParsePreset = false" class="text-sm text-forge-text-muted hover:text-forge-text">Cancel</button>
            </div>
          </div>

          <!-- Move to Creator config -->
          <div v-if="bulkActionType === 'moveCreator'" class="space-y-3">
            <div class="max-w-sm">
              <label class="block text-xs font-medium text-forge-text-muted mb-1">Target Creator</label>
              <CreatorAutocomplete
                v-model="moveCreatorName"
                placeholder="Search creators..."
              />
            </div>
            <button
              @click="applyMoveCreator"
              :disabled="!moveCreatorName || moveCreatorApplying || !selectedIds.size"
              class="px-4 py-1.5 bg-forge-accent hover:bg-forge-accent-hover text-forge-bg rounded-lg text-sm font-medium transition-colors disabled:opacity-40 disabled:cursor-not-allowed"
            >
              {{ moveCreatorApplying ? 'Moving…' : `Move ${selectedIds.size} model${selectedIds.size === 1 ? '' : 's'} →` }}
            </button>
          </div>

          <!-- Empty state when no action selected -->
          <p v-if="!bulkActionType" class="text-xs text-forge-text-muted italic">
            Select an action above to get started.
          </p>
        </div>

        <!-- Footer: Apply / Summary -->
        <div class="border-t border-forge-border px-4 py-3">
          <!-- Summary confirmation -->
          <div v-if="bulkShowSummary" class="mb-3 p-3 bg-forge-bg rounded-lg border border-forge-border">
            <p class="text-xs font-semibold text-forge-text mb-2">
              Applying to {{ selectedIds.size }} model{{ selectedIds.size === 1 ? '' : 's' }}:
            </p>
            <ul class="text-xs text-forge-text-muted space-y-1">
              <li v-for="item in changeSummary" :key="item" class="flex items-start gap-1.5">
                <span class="text-forge-accent mt-0.5">▸</span>
                <span>{{ item }}</span>
              </li>
            </ul>
            <div class="flex gap-2 mt-3">
              <button
                @click="executeBulkAction"
                :disabled="bulkProcessing"
                class="px-4 py-1.5 bg-forge-accent hover:bg-forge-accent-hover text-forge-bg rounded-lg text-sm font-medium transition-colors disabled:opacity-50"
              >
                {{ bulkProcessing ? 'Applying…' : 'Confirm & Apply' }}
              </button>
              <button
                @click="bulkShowSummary = false"
                class="px-3 py-1.5 text-sm text-forge-text-muted hover:text-forge-text transition-colors"
              >
                Back
              </button>
            </div>
          </div>

          <!-- Apply button row -->
          <div v-else class="flex items-center gap-3">
            <button
              @click="requestApply"
              :disabled="!hasChanges || bulkProcessing"
              class="px-4 py-1.5 bg-forge-accent hover:bg-forge-accent-hover text-forge-bg rounded-lg text-sm font-medium transition-colors disabled:opacity-40 disabled:cursor-not-allowed"
            >
              Apply to {{ selectedIds.size }} model{{ selectedIds.size === 1 ? '' : 's' }}
            </button>
            <span v-if="!hasChanges" class="text-xs text-forge-text-muted italic">
              Fill in at least one field above to enable
            </span>
            <span v-else class="text-xs text-forge-text-muted">
              {{ changeSummary.length }} field{{ changeSummary.length === 1 ? '' : 's' }} will change
            </span>
            <button
              @click="resetBulkFields"
              v-if="hasChanges"
              class="ml-auto text-xs text-forge-text-muted hover:text-forge-danger transition-colors"
            >
              Clear fields
            </button>
          </div>
        </div>
      </div>
    </transition>

    <!-- Prompt when bulk mode is on but nothing selected -->
    <div
      v-if="bulkMode && selectedIds.size === 0"
      class="bg-forge-card border border-forge-border rounded-xl px-4 py-3 mb-6 flex items-center gap-3 text-sm text-forge-text-muted"
    >
      <span>☑️</span>
      <span>Bulk mode active — select models below to edit them together</span>
      <button @click="cancelBulk" class="ml-auto text-xs hover:text-forge-danger transition-colors">Cancel</button>
    </div>

    <!-- Result count + select all -->
    <div class="flex items-center gap-3 mb-4">
      <label
        v-if="bulkMode"
        class="flex items-center gap-2 cursor-pointer text-sm text-forge-text-muted hover:text-forge-text"
      >
        <input
          type="checkbox"
          :checked="allSelected"
          @change="toggleSelectAll"
          class="rounded border-forge-border bg-forge-bg text-forge-accent focus:ring-forge-accent"
        />
        Select all on page
      </label>
      <p class="text-sm text-forge-text-muted">
        {{ totalCount.toLocaleString() }} models found
      </p>
    </div>

    <div class="flex gap-6">
      <!-- Filter sidebar -->
      <div
        :class="[
          'w-64 shrink-0',
          showFilters ? 'block' : 'hidden lg:block',
        ]"
      >
        <div class="bg-forge-card border border-forge-border rounded-xl p-4 sticky top-20">
          <FilterSidebar :filters="filters" @update:filters="onFiltersUpdate" />
        </div>
      </div>

      <!-- Model grid -->
      <div class="flex-1 min-w-0">
        <!-- Loading -->
        <div v-if="api.loading.value && !models.length" class="flex justify-center py-20">
          <div class="w-8 h-8 border-2 border-forge-accent border-t-transparent rounded-full animate-spin"></div>
        </div>

        <!-- Empty state -->
        <div v-else-if="!models.length" class="text-center py-20">
          <span class="text-5xl">🗿</span>
          <p class="text-forge-text-muted mt-4">No models found</p>
          <p class="text-sm text-forge-text-muted mt-1">Try adjusting your search or filters</p>
        </div>

        <!-- Grid -->
        <div
          v-else
          class="grid grid-cols-2 sm:grid-cols-3 md:grid-cols-3 lg:grid-cols-4 xl:grid-cols-4 gap-4"
        >
          <div v-for="model in models" :key="model.id" class="relative">
            <!-- Bulk checkbox overlay -->
            <label
              v-if="bulkMode"
              class="absolute top-2 left-2 z-10 cursor-pointer"
              @click.stop
            >
              <input
                type="checkbox"
                :checked="selectedIds.has(model.id)"
                @change="toggleSelect(model.id)"
                class="w-5 h-5 rounded border-forge-border bg-forge-bg/80 text-forge-accent focus:ring-forge-accent"
              />
            </label>
            <ModelCard
              :model="model"
              :class="{ 'ring-2 ring-forge-accent/50': selectedIds.has(model.id) }"
            />
          </div>
        </div>

        <!-- Pagination -->
        <div v-if="totalPages > 1" class="flex items-center justify-center gap-2 mt-8">
          <button
            @click="goToPage(1)"
            :disabled="page === 1"
            class="px-3 py-1.5 rounded-lg text-sm bg-forge-card border border-forge-border text-forge-text-muted hover:text-forge-accent disabled:opacity-30 disabled:cursor-not-allowed"
          >
            «
          </button>
          <button
            @click="goToPage(page - 1)"
            :disabled="page === 1"
            class="px-3 py-1.5 rounded-lg text-sm bg-forge-card border border-forge-border text-forge-text-muted hover:text-forge-accent disabled:opacity-30 disabled:cursor-not-allowed"
          >
            ‹
          </button>

          <button
            v-for="p in pageRange"
            :key="p"
            @click="goToPage(p)"
            :class="[
              'px-3 py-1.5 rounded-lg text-sm border transition-colors',
              p === page
                ? 'bg-forge-accent text-forge-bg border-forge-accent font-medium'
                : 'bg-forge-card border-forge-border text-forge-text-muted hover:text-forge-accent',
            ]"
          >
            {{ p }}
          </button>

          <button
            @click="goToPage(page + 1)"
            :disabled="page >= totalPages"
            class="px-3 py-1.5 rounded-lg text-sm bg-forge-card border border-forge-border text-forge-text-muted hover:text-forge-accent disabled:opacity-30 disabled:cursor-not-allowed"
          >
            ›
          </button>
          <button
            @click="goToPage(totalPages)"
            :disabled="page >= totalPages"
            class="px-3 py-1.5 rounded-lg text-sm bg-forge-card border border-forge-border text-forge-text-muted hover:text-forge-accent disabled:opacity-30 disabled:cursor-not-allowed"
          >
            »
          </button>
        </div>
      </div>
    </div>

    <!-- Success toast -->
    <transition name="fade">
      <div
        v-if="bulkSuccessMsg"
        class="fixed bottom-4 right-4 bg-emerald-600/90 text-white px-4 py-2 rounded-lg shadow-lg text-sm flex items-center gap-2"
      >
        <span>✅</span> {{ bulkSuccessMsg }}
      </div>
    </transition>

    <!-- Error toast -->
    <div
      v-if="api.error.value"
      class="fixed bottom-4 right-4 bg-forge-danger/90 text-white px-4 py-2 rounded-lg shadow-lg text-sm"
    >
      {{ api.error.value }}
    </div>

    <!-- ─── Reorganize Modal ───────────────────────────────── -->
    <transition name="fade">
      <div
        v-if="showReorgModal"
        class="fixed inset-0 z-50 flex items-center justify-center p-4 bg-black/60"
        @click.self="showReorgModal = false"
      >
        <div class="bg-forge-card border border-forge-border rounded-xl shadow-2xl w-full max-w-3xl max-h-[90vh] flex flex-col">
          <!-- Header -->
          <div class="flex items-center justify-between px-5 py-4 border-b border-forge-border">
            <h2 class="text-base font-semibold text-forge-text">Reorganize Directories</h2>
            <button @click="showReorgModal = false" class="text-forge-text-muted hover:text-forge-danger text-lg leading-none">×</button>
          </div>

          <!-- Body -->
          <div class="overflow-y-auto flex-1 p-5 space-y-4">
            <!-- Template + presets row -->
            <div class="flex flex-wrap gap-3 items-end">
              <div class="flex-1 min-w-48">
                <label class="block text-xs font-medium text-forge-text-muted mb-1">Template</label>
                <input
                  v-model="reorgTemplate"
                  type="text"
                  class="w-full bg-forge-bg border border-forge-border rounded-lg px-3 py-1.5 text-sm text-forge-text focus:outline-none focus:border-forge-accent font-mono"
                />
              </div>
              <div v-if="reorgPresets.length" class="w-52">
                <label class="block text-xs font-medium text-forge-text-muted mb-1">Preset</label>
                <select
                  v-model="selectedReorgPreset"
                  @change="applyReorgPreset(selectedReorgPreset)"
                  class="w-full bg-forge-bg border border-forge-border rounded-lg px-3 py-1.5 text-sm text-forge-text focus:outline-none focus:border-forge-accent"
                >
                  <option value="">— choose preset —</option>
                  <option v-for="p in reorgPresets" :key="p.id" :value="String(p.id)">{{ p.name }}</option>
                </select>
              </div>
              <button
                @click="runReorgPreview"
                :disabled="reorgPreviewing"
                class="px-3 py-1.5 bg-forge-card border border-forge-border rounded-lg text-sm text-forge-text-muted hover:text-forge-accent transition-colors disabled:opacity-50"
              >
                {{ reorgPreviewing ? 'Loading…' : '🔄 Refresh Preview' }}
              </button>
            </div>

            <!-- Save as Preset -->
            <div class="flex items-center gap-2">
              <button
                @click="showSaveReorgPreset = !showSaveReorgPreset"
                class="text-xs text-forge-text-muted hover:text-forge-accent transition-colors"
              >
                + Save as Preset
              </button>
              <template v-if="showSaveReorgPreset">
                <input
                  v-model="saveReorgPresetName"
                  type="text"
                  placeholder="Preset name..."
                  class="flex-1 max-w-xs bg-forge-bg border border-forge-border rounded-lg px-3 py-1 text-xs text-forge-text focus:outline-none focus:border-forge-accent"
                  @keydown.enter="saveReorgPresetFn"
                />
                <button @click="saveReorgPresetFn" :disabled="!saveReorgPresetName.trim()" class="px-2 py-1 bg-forge-accent text-forge-bg rounded text-xs font-medium disabled:opacity-40">Save</button>
                <button @click="showSaveReorgPreset = false" class="text-xs text-forge-text-muted hover:text-forge-text">Cancel</button>
              </template>
            </div>

            <!-- Preview table -->
            <div v-if="reorgPreviewing" class="flex justify-center py-8">
              <div class="w-6 h-6 border-2 border-forge-accent border-t-transparent rounded-full animate-spin"></div>
            </div>
            <div v-else-if="reorgPreviewRows.length" class="overflow-x-auto rounded-lg border border-forge-border">
              <table class="w-full text-xs">
                <thead class="bg-forge-bg text-forge-text-muted">
                  <tr>
                    <th class="px-3 py-2 text-left font-medium">Model</th>
                    <th class="px-3 py-2 text-left font-medium">Current Path</th>
                    <th class="px-3 py-2 text-left font-medium">→ New Path</th>
                    <th class="px-3 py-2 text-left font-medium">Status</th>
                  </tr>
                </thead>
                <tbody class="divide-y divide-forge-border">
                  <tr
                    v-for="row in reorgPreviewRows"
                    :key="row.modelId || row.id"
                    :class="row.willMove === false ? 'bg-forge-bg/30 text-forge-text-muted' : ''"
                  >
                    <td class="px-3 py-2 font-medium text-forge-text truncate max-w-[12rem]">{{ row.modelName || row.name }}</td>
                    <td class="px-3 py-2 font-mono text-forge-text-muted truncate max-w-[16rem]" :title="row.currentPath">{{ row.currentPath }}</td>
                    <td class="px-3 py-2 font-mono truncate max-w-[16rem]" :class="row.willMove !== false ? 'text-emerald-400' : 'text-forge-text-muted'" :title="row.newPath">{{ row.newPath }}</td>
                    <td class="px-3 py-2">
                      <span v-if="row.willMove === false" class="text-forge-text-muted">no change</span>
                      <span v-else class="text-emerald-400">✓ move</span>
                    </td>
                  </tr>
                </tbody>
              </table>
            </div>
            <p v-else-if="!reorgPreviewing" class="text-sm text-forge-text-muted text-center py-4">
              No preview data — click Refresh Preview.
            </p>
          </div>

          <!-- Footer -->
          <div class="px-5 py-4 border-t border-forge-border flex items-center justify-between gap-3">
            <span class="text-xs text-forge-text-muted">
              {{ selectedIds.size }} model{{ selectedIds.size === 1 ? '' : 's' }} selected
              <template v-if="reorgPreviewRows.length">· {{ reorgPreviewRows.filter(r => r.willMove !== false).length }} will move</template>
            </span>
            <div class="flex gap-2">
              <button
                @click="showReorgModal = false"
                class="px-4 py-1.5 text-sm text-forge-text-muted hover:text-forge-text transition-colors"
              >
                Cancel
              </button>
              <button
                @click="applyReorganize"
                :disabled="reorgApplying || !reorgPreviewRows.length"
                class="px-4 py-1.5 bg-forge-accent hover:bg-forge-accent-hover text-forge-bg rounded-lg text-sm font-medium transition-colors disabled:opacity-40 disabled:cursor-not-allowed"
              >
                {{ reorgApplying ? 'Reorganizing…' : 'Apply Reorganize' }}
              </button>
            </div>
          </div>
        </div>
      </div>
    </transition>

    <!-- ─── Parse from Filename Modal ────────────────────────── -->
    <transition name="fade">
      <div
        v-if="showParseModal"
        class="fixed inset-0 z-50 flex items-center justify-center p-4 bg-black/60"
        @click.self="showParseModal = false"
      >
        <div class="bg-forge-card border border-forge-border rounded-xl shadow-2xl w-full max-w-3xl max-h-[90vh] flex flex-col">
          <!-- Header -->
          <div class="flex items-center justify-between px-5 py-4 border-b border-forge-border">
            <h2 class="text-base font-semibold text-forge-text">Parse from Filename</h2>
            <button @click="showParseModal = false" class="text-forge-text-muted hover:text-forge-danger text-lg leading-none">×</button>
          </div>

          <!-- Body -->
          <div class="overflow-y-auto flex-1 p-5 space-y-4">
            <!-- Template + presets -->
            <div class="flex flex-wrap gap-3 items-end">
              <div class="flex-1 min-w-48">
                <label class="block text-xs font-medium text-forge-text-muted mb-1">Template</label>
                <input
                  v-model="parseTemplate"
                  type="text"
                  class="w-full bg-forge-bg border border-forge-border rounded-lg px-3 py-1.5 text-sm text-forge-text focus:outline-none focus:border-forge-accent font-mono"
                />
              </div>
              <div v-if="parsePresets.length" class="w-52">
                <label class="block text-xs font-medium text-forge-text-muted mb-1">Preset</label>
                <select
                  v-model="selectedParsePreset"
                  @change="applyParsePreset(selectedParsePreset)"
                  class="w-full bg-forge-bg border border-forge-border rounded-lg px-3 py-1.5 text-sm text-forge-text focus:outline-none focus:border-forge-accent"
                >
                  <option value="">— choose preset —</option>
                  <option v-for="p in parsePresets" :key="p.id" :value="String(p.id)">{{ p.name }}</option>
                </select>
              </div>
              <button
                @click="runParsePreview"
                :disabled="parsePreviewing"
                class="px-3 py-1.5 bg-forge-card border border-forge-border rounded-lg text-sm text-forge-text-muted hover:text-forge-accent transition-colors disabled:opacity-50"
              >
                {{ parsePreviewing ? 'Loading…' : '🔄 Refresh Preview' }}
              </button>
            </div>

            <!-- Save as Preset -->
            <div class="flex items-center gap-2">
              <button
                @click="showSaveParsePreset = !showSaveParsePreset"
                class="text-xs text-forge-text-muted hover:text-forge-accent transition-colors"
              >
                + Save as Preset
              </button>
              <template v-if="showSaveParsePreset">
                <input
                  v-model="saveParsePresetName"
                  type="text"
                  placeholder="Preset name..."
                  class="flex-1 max-w-xs bg-forge-bg border border-forge-border rounded-lg px-3 py-1 text-xs text-forge-text focus:outline-none focus:border-forge-accent"
                  @keydown.enter="saveParseFn"
                />
                <button @click="saveParseFn" :disabled="!saveParsePresetName.trim()" class="px-2 py-1 bg-forge-accent text-forge-bg rounded text-xs font-medium disabled:opacity-40">Save</button>
                <button @click="showSaveParsePreset = false" class="text-xs text-forge-text-muted hover:text-forge-text">Cancel</button>
              </template>
            </div>

            <!-- Match stats bar -->
            <div v-if="parsePreviewRows.length && !parsePreviewing" class="flex items-center gap-3">
              <span class="text-xs text-forge-text-muted">Match rate:</span>
              <div class="flex-1 bg-forge-bg rounded-full h-2 overflow-hidden">
                <div
                  class="h-2 rounded-full transition-all"
                  :class="parseMatchPct >= 70 ? 'bg-emerald-500' : parseMatchPct >= 40 ? 'bg-yellow-500' : 'bg-forge-danger'"
                  :style="{ width: parseMatchPct + '%' }"
                />
              </div>
              <span class="text-xs font-semibold" :class="parseMatchPct >= 70 ? 'text-emerald-400' : parseMatchPct >= 40 ? 'text-yellow-400' : 'text-forge-danger'">
                {{ parseMatchPct }}%
              </span>
              <span class="text-xs text-forge-text-muted">({{ parseMatchCount }}/{{ parsePreviewRows.length }} matched)</span>
            </div>

            <!-- Preview table -->
            <div v-if="parsePreviewing" class="flex justify-center py-8">
              <div class="w-6 h-6 border-2 border-forge-accent border-t-transparent rounded-full animate-spin"></div>
            </div>
            <div v-else-if="parsePreviewRows.length" class="overflow-x-auto rounded-lg border border-forge-border">
              <table class="w-full text-xs">
                <thead class="bg-forge-bg text-forge-text-muted">
                  <tr>
                    <th class="px-3 py-2 text-left font-medium">Directory Name</th>
                    <th class="px-3 py-2 text-left font-medium">Creator</th>
                    <th class="px-3 py-2 text-left font-medium">Name</th>
                    <th class="px-3 py-2 text-left font-medium">Category</th>
                    <th class="px-3 py-2 text-left font-medium">Match</th>
                  </tr>
                </thead>
                <tbody class="divide-y divide-forge-border">
                  <tr
                    v-for="row in parsePreviewRows"
                    :key="row.modelId || row.id"
                    :class="row.matched ? 'bg-emerald-900/10' : 'bg-red-900/10'"
                  >
                    <td class="px-3 py-2 font-mono text-forge-text-muted truncate max-w-[14rem]" :title="row.directoryName">{{ row.directoryName }}</td>
                    <td class="px-3 py-2 truncate max-w-[10rem]">{{ row.extractedCreator || '—' }}</td>
                    <td class="px-3 py-2 truncate max-w-[12rem]">{{ row.extractedName || '—' }}</td>
                    <td class="px-3 py-2">{{ row.extractedCategory || '—' }}</td>
                    <td class="px-3 py-2">
                      <span v-if="row.matched" class="text-emerald-400">✓ match</span>
                      <span v-else class="text-forge-danger">✕ no match</span>
                    </td>
                  </tr>
                </tbody>
              </table>
            </div>
            <p v-else-if="!parsePreviewing" class="text-sm text-forge-text-muted text-center py-4">
              No preview data — click Refresh Preview.
            </p>
          </div>

          <!-- Footer -->
          <div class="px-5 py-4 border-t border-forge-border flex items-center justify-between gap-3">
            <span class="text-xs text-forge-text-muted">
              {{ selectedIds.size }} model{{ selectedIds.size === 1 ? '' : 's' }} selected
              <template v-if="parsePreviewRows.length">· {{ parseMatchCount }} will be updated</template>
            </span>
            <div class="flex gap-2">
              <button
                @click="showParseModal = false"
                class="px-4 py-1.5 text-sm text-forge-text-muted hover:text-forge-text transition-colors"
              >
                Cancel
              </button>
              <button
                @click="applyParseFN"
                :disabled="parseApplying || !parseMatchCount"
                class="px-4 py-1.5 bg-forge-accent hover:bg-forge-accent-hover text-forge-bg rounded-lg text-sm font-medium transition-colors disabled:opacity-40 disabled:cursor-not-allowed"
              >
                {{ parseApplying ? 'Applying…' : `Apply to ${parseMatchCount} Matched` }}
              </button>
            </div>
          </div>
        </div>
      </div>
    </transition>
  </div>
</template>

<style scoped>
.slide-down-enter-active,
.slide-down-leave-active {
  transition: all 0.2s ease;
}
.slide-down-enter-from,
.slide-down-leave-to {
  opacity: 0;
  transform: translateY(-8px);
}

.fade-enter-active,
.fade-leave-active {
  transition: opacity 0.3s ease;
}
.fade-enter-from,
.fade-leave-to {
  opacity: 0;
}
</style>

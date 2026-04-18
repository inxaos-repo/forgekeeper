<!--
  ImportWizard.vue — 4-step manual import wizard
  Step 1: Select source folder and scan
  Step 2: Review & match detected models
  Step 3: Edit metadata per model
  Step 4: Preview paths, set options, confirm import
-->
<script setup>
import { ref, computed, watch } from 'vue'

const emit = defineEmits(['close'])

// ── State ─────────────────────────────────────────────────────────────────────
const step = ref(1)
const scanning = ref(false)
const scanError = ref(null)

// Step 1
const folderPath = ref('')
const recursive = ref(true)

// Step 2
const scanResults = ref([])   // raw models from /api/v1/import/scan
const filter = ref('all')     // 'all' | 'matched' | 'unmatched'
const selectAll = ref(false)
const bulkCreator = ref('')
const bulkTags = ref('')

// Step 3 — left-panel selection, right-panel metadata editor
const editorIndex = ref(0)    // which model in selectedModels is active

// Step 4
const renameTemplate = ref('{Creator CleanName}/{Model CleanName}/{File CleanName}')
const optExtract = ref(true)
const optThumbnails = ref(true)
const optMetadataJson = ref(true)
const importMode = ref('copy')
const importing = ref(false)

// ── Constants ─────────────────────────────────────────────────────────────────
const SOURCES = ['mmf', 'thangs', 'patreon', 'cults3d', 'thingiverse', 'manual']
const SCALES = ['28mm', '32mm', '54mm', '75mm']

const TOKEN_HINTS = [
  '{Creator CleanName}',
  '{Model CleanName}',
  '{File CleanName}',
  '{Year}',
  '{Source}',
]

// ── Computed ──────────────────────────────────────────────────────────────────
const filteredModels = computed(() => {
  if (filter.value === 'matched')   return scanResults.value.filter(m => m._matchStatus === 'matched')
  if (filter.value === 'unmatched') return scanResults.value.filter(m => m._matchStatus !== 'matched')
  return scanResults.value
})

const selectedModels = computed(() => scanResults.value.filter(m => m._selected))

const activeModel = computed(() => selectedModels.value[editorIndex.value] ?? null)

// When all visible items are checked → sync selectAll
watch(
  () => filteredModels.value.map(m => m._selected),
  (vals) => { selectAll.value = vals.length > 0 && vals.every(Boolean) },
  { deep: true }
)

// ── Helpers ───────────────────────────────────────────────────────────────────
function matchStatusClass(status) {
  if (status === 'matched')  return 'bg-forge-accent'
  if (status === 'partial')  return 'bg-source-mmf'
  return 'bg-forge-danger'
}

function matchStatusLabel(status) {
  if (status === 'matched')  return '●'
  if (status === 'partial')  return '◑'
  return '○'
}

function fmtSize(bytes) {
  if (!bytes) return '—'
  if (bytes < 1024) return `${bytes} B`
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`
  return `${(bytes / 1024 / 1024).toFixed(1)} MB`
}

function applyTemplate(model, file) {
  return renameTemplate.value
    .replace('{Creator CleanName}', cleanName(model._editCreator || model.creator || 'Unknown'))
    .replace('{Model CleanName}',   cleanName(model._editName   || model.name   || 'Unknown'))
    .replace('{File CleanName}',    cleanName(file))
    .replace('{Year}',              new Date().getFullYear())
    .replace('{Source}',            model._metadata?.source || 'manual')
}

function cleanName(str) {
  return (str || '')
    .replace(/[^a-zA-Z0-9\s\-_]/g, '')
    .replace(/\s+/g, ' ')
    .trim()
}

function getSharedValue(field) {
  const vals = selectedModels.value.map(m => m._metadata?.[field] ?? '')
  if (vals.length === 0) return ''
  const first = vals[0]
  return vals.every(v => v === first) ? first : '<mixed>'
}

function setSharedValue(field, value) {
  if (value === '<mixed>') return
  selectedModels.value.forEach(m => {
    if (!m._metadata) m._metadata = {}
    m._metadata[field] = value
  })
}

// ── Tag chip helpers ───────────────────────────────────────────────────────────
const tagInput = ref('')

function addTag(model, tag) {
  const t = tag.trim()
  if (!t) return
  if (!model._metadata.tags) model._metadata.tags = []
  if (!model._metadata.tags.includes(t)) model._metadata.tags.push(t)
}

function removeTag(model, tag) {
  if (!model._metadata?.tags) return
  model._metadata.tags = model._metadata.tags.filter(t => t !== tag)
}

function onTagKeydown(model, e) {
  if (e.key === 'Enter' || e.key === ',') {
    e.preventDefault()
    addTag(model, tagInput.value)
    tagInput.value = ''
  }
}

// ── Step navigation ───────────────────────────────────────────────────────────
function prevStep() { if (step.value > 1) step.value-- }

function canProceedStep2() { return selectedModels.value.length > 0 }

// ── API calls ─────────────────────────────────────────────────────────────────
async function runScan() {
  if (!folderPath.value.trim()) return
  scanning.value = true
  scanError.value = null
  try {
    const res = await fetch('/api/v1/import/scan', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ path: folderPath.value.trim(), recursive: recursive.value }),
    })
    if (!res.ok) throw new Error(`Scan failed: ${res.status} ${res.statusText}`)
    const data = await res.json()

    // Normalize: API might return { models: [...] } or an array directly
    const models = Array.isArray(data) ? data : (data.models || data.items || [])

    scanResults.value = models.map(m => ({
      ...m,
      _selected: false,
      _editCreator: m.suggestedCreator || m.creator || '',
      _editName:    m.suggestedName    || m.name    || '',
      _matchStatus: m.matchStatus      || (m.confidence >= 0.8 ? 'matched' : m.confidence >= 0.4 ? 'partial' : 'unmatched'),
      _metadata: {
        source:     m.source || 'manual',
        tags:       m.tags   || [],
        scale:      m.scale  || '',
        gameSystem: m.gameSystem || '',
        category:   m.category  || '',
        license:    m.license   || '',
        collection: m.collection || '',
      },
    }))

    step.value = 2
  } catch (err) {
    scanError.value = err.message
  } finally {
    scanning.value = false
  }
}

async function applyBulkMetadata() {
  if (!selectedModels.value.length) return
  const ids = selectedModels.value.map(m => m.id).filter(Boolean)
  const fields = {}
  const addTags = []

  if (bulkCreator.value.trim()) {
    fields.creator = bulkCreator.value.trim()
    selectedModels.value.forEach(m => { m._editCreator = fields.creator })
  }
  if (bulkTags.value.trim()) {
    bulkTags.value.split(',').map(t => t.trim()).filter(Boolean).forEach(t => addTags.push(t))
    selectedModels.value.forEach(m => {
      if (!m._metadata.tags) m._metadata.tags = []
      addTags.forEach(t => { if (!m._metadata.tags.includes(t)) m._metadata.tags.push(t) })
    })
  }

  if (!ids.length) return // local-only edit, no server call needed

  try {
    await fetch('/api/v1/models/bulk-metadata', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ modelIds: ids, fields, addTags, removeTags: [] }),
    })
  } catch { /* non-fatal — local state already updated */ }

  bulkCreator.value = ''
  bulkTags.value = ''
}

function toggleSelectAll() {
  const newVal = !selectAll.value
  filteredModels.value.forEach(m => { m._selected = newVal })
}

// ── Preview paths (client-side) ───────────────────────────────────────────────
const previewPaths = computed(() => {
  return selectedModels.value.flatMap(model => {
    const files = model.files || [model._editName || model.name || 'file.stl']
    return files.map(file => {
      const filename = typeof file === 'string' ? file : (file.name || file.filename || 'file')
      const newPath = applyTemplate(model, filename)
      return {
        model: model._editName || model.name,
        original: model.originalPath || model.path || filename,
        newPath,
        changed: newPath !== (model.originalPath || model.path || filename),
      }
    })
  })
})

async function doImport() {
  importing.value = true
  // Future: POST /api/v1/import/execute
  // For now, close the wizard after a brief simulated delay
  await new Promise(r => setTimeout(r, 800))
  importing.value = false
  emit('close')
}
</script>

<template>
  <!-- Backdrop -->
  <div class="fixed inset-0 z-50 flex items-center justify-center p-4 bg-black/60 backdrop-blur-sm" @click.self="emit('close')">
    <!-- Modal -->
    <div class="w-full max-w-4xl max-h-[90vh] bg-forge-card border border-forge-border rounded-xl shadow-2xl flex flex-col overflow-hidden">

      <!-- Header -->
      <div class="flex items-center justify-between px-6 py-4 border-b border-forge-border shrink-0">
        <div>
          <h2 class="text-lg font-bold text-forge-text">📥 Manual Import Wizard</h2>
          <p class="text-xs text-forge-text-muted mt-0.5">Step {{ step }} of 4</p>
        </div>
        <!-- Step indicator -->
        <div class="hidden sm:flex items-center gap-2 mx-4">
          <template v-for="n in 4" :key="n">
            <div
              :class="[
                'w-7 h-7 rounded-full flex items-center justify-center text-xs font-bold transition-colors',
                step === n
                  ? 'bg-forge-accent text-forge-bg'
                  : step > n
                    ? 'bg-forge-accent/40 text-forge-text'
                    : 'bg-forge-bg border border-forge-border text-forge-text-muted',
              ]"
            >{{ n }}</div>
            <div v-if="n < 4" :class="['w-6 h-0.5', step > n ? 'bg-forge-accent/60' : 'bg-forge-border']"></div>
          </template>
        </div>
        <button @click="emit('close')" class="text-forge-text-muted hover:text-forge-text transition-colors p-1">
          <svg class="w-5 h-5" fill="none" stroke="currentColor" viewBox="0 0 24 24">
            <path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M6 18L18 6M6 6l12 12"/>
          </svg>
        </button>
      </div>

      <!-- Body (scrollable) -->
      <div class="flex-1 overflow-y-auto p-6">

        <!-- ─── STEP 1: Select Source ─────────────────────────────────────── -->
        <div v-if="step === 1" class="space-y-6">
          <div>
            <h3 class="text-base font-semibold text-forge-text mb-1">Select Source Folder</h3>
            <p class="text-sm text-forge-text-muted">Enter the server-side path to the folder containing your model files.</p>
          </div>

          <div class="space-y-4">
            <div>
              <label class="block text-sm font-medium text-forge-text mb-1">Folder Path</label>
              <input
                v-model="folderPath"
                type="text"
                placeholder="/mnt/storage/unsorted/new-bundle"
                class="w-full bg-forge-bg border border-forge-border rounded-lg px-3 py-2 text-sm text-forge-text placeholder:text-forge-text-muted focus:outline-none focus:border-forge-accent"
                @keydown.enter="runScan"
              />
            </div>

            <label class="flex items-center gap-2 cursor-pointer select-none">
              <input v-model="recursive" type="checkbox" class="rounded border-forge-border accent-forge-accent w-4 h-4" />
              <span class="text-sm text-forge-text">Scan subdirectories recursively</span>
            </label>
          </div>

          <!-- Error -->
          <div v-if="scanError" class="bg-forge-danger/10 border border-forge-danger/30 rounded-lg p-3 text-sm text-forge-danger">
            {{ scanError }}
          </div>

          <!-- Scan spinner -->
          <div v-if="scanning" class="flex items-center gap-3 text-sm text-forge-text-muted">
            <div class="w-5 h-5 border-2 border-forge-accent border-t-transparent rounded-full animate-spin shrink-0"></div>
            Scanning folder…
          </div>
        </div>

        <!-- ─── STEP 2: Review & Match ─────────────────────────────────────── -->
        <div v-else-if="step === 2" class="space-y-4">
          <div class="flex flex-col sm:flex-row sm:items-center justify-between gap-3">
            <div>
              <h3 class="text-base font-semibold text-forge-text">Review Detected Models</h3>
              <p class="text-sm text-forge-text-muted">{{ scanResults.length }} models found · {{ selectedModels.length }} selected</p>
            </div>
            <!-- Filter tabs -->
            <div class="flex gap-1.5">
              <button
                v-for="f in ['all', 'matched', 'unmatched']"
                :key="f"
                @click="filter = f"
                :class="[
                  'px-3 py-1 rounded-lg text-xs font-medium transition-colors capitalize',
                  filter === f
                    ? 'bg-forge-accent text-forge-bg'
                    : 'bg-forge-bg border border-forge-border text-forge-text-muted hover:text-forge-text',
                ]"
              >{{ f }}</button>
            </div>
          </div>

          <!-- Bulk assign bar -->
          <div class="flex flex-col sm:flex-row gap-2 p-3 bg-forge-bg border border-forge-border rounded-lg">
            <input
              v-model="bulkCreator"
              type="text"
              placeholder="Bulk assign creator…"
              class="flex-1 bg-forge-card border border-forge-border rounded-lg px-3 py-1.5 text-sm text-forge-text placeholder:text-forge-text-muted focus:outline-none focus:border-forge-accent"
            />
            <input
              v-model="bulkTags"
              type="text"
              placeholder="Tags (comma-separated)…"
              class="flex-1 bg-forge-card border border-forge-border rounded-lg px-3 py-1.5 text-sm text-forge-text placeholder:text-forge-text-muted focus:outline-none focus:border-forge-accent"
            />
            <button
              @click="applyBulkMetadata"
              :disabled="!selectedModels.length"
              class="px-3 py-1.5 rounded-lg text-sm font-medium bg-forge-accent hover:bg-forge-accent-hover text-forge-bg disabled:opacity-40 disabled:cursor-not-allowed transition-colors shrink-0"
            >
              Apply to {{ selectedModels.length }} selected
            </button>
          </div>

          <!-- Table -->
          <div class="overflow-x-auto rounded-xl border border-forge-border">
            <table class="w-full text-sm">
              <thead class="bg-forge-bg border-b border-forge-border">
                <tr>
                  <th class="px-3 py-2 text-left">
                    <input
                      type="checkbox"
                      :checked="selectAll"
                      @change="toggleSelectAll"
                      class="rounded border-forge-border accent-forge-accent w-4 h-4"
                    />
                  </th>
                  <th class="px-3 py-2 text-left text-xs font-semibold text-forge-text-muted uppercase tracking-wide">Status</th>
                  <th class="px-3 py-2 text-left text-xs font-semibold text-forge-text-muted uppercase tracking-wide">Creator</th>
                  <th class="px-3 py-2 text-left text-xs font-semibold text-forge-text-muted uppercase tracking-wide">Model Name</th>
                  <th class="px-3 py-2 text-left text-xs font-semibold text-forge-text-muted uppercase tracking-wide">Files</th>
                  <th class="px-3 py-2 text-left text-xs font-semibold text-forge-text-muted uppercase tracking-wide">Size</th>
                </tr>
              </thead>
              <tbody class="divide-y divide-forge-border">
                <tr
                  v-for="model in filteredModels"
                  :key="model.id || model._editName"
                  :class="['transition-colors', model._selected ? 'bg-forge-accent/5' : 'hover:bg-forge-bg/50']"
                >
                  <td class="px-3 py-2">
                    <input
                      type="checkbox"
                      v-model="model._selected"
                      class="rounded border-forge-border accent-forge-accent w-4 h-4"
                    />
                  </td>
                  <td class="px-3 py-2">
                    <span
                      :class="['inline-block w-3 h-3 rounded-full', matchStatusClass(model._matchStatus)]"
                      :title="model._matchStatus"
                    ></span>
                  </td>
                  <td class="px-3 py-2">
                    <input
                      v-model="model._editCreator"
                      type="text"
                      class="w-32 bg-forge-bg border border-forge-border rounded px-2 py-1 text-xs text-forge-text focus:outline-none focus:border-forge-accent"
                    />
                  </td>
                  <td class="px-3 py-2">
                    <input
                      v-model="model._editName"
                      type="text"
                      class="w-48 bg-forge-bg border border-forge-border rounded px-2 py-1 text-xs text-forge-text focus:outline-none focus:border-forge-accent"
                    />
                  </td>
                  <td class="px-3 py-2 text-forge-text-muted text-xs">
                    {{ Array.isArray(model.files) ? model.files.length : (model.fileCount || '—') }}
                  </td>
                  <td class="px-3 py-2 text-forge-text-muted text-xs whitespace-nowrap">
                    {{ fmtSize(model.totalSize || model.size) }}
                  </td>
                </tr>
                <tr v-if="!filteredModels.length">
                  <td colspan="6" class="px-3 py-8 text-center text-forge-text-muted text-sm">
                    No models match the current filter.
                  </td>
                </tr>
              </tbody>
            </table>
          </div>

          <p v-if="!selectedModels.length" class="text-xs text-forge-text-muted">
            Select at least one model to continue.
          </p>
        </div>

        <!-- ─── STEP 3: Edit Metadata ─────────────────────────────────────── -->
        <div v-else-if="step === 3" class="flex gap-4 min-h-[420px]">
          <!-- Left: model list -->
          <div class="w-52 shrink-0 overflow-y-auto space-y-1 pr-1">
            <p class="text-xs font-semibold text-forge-text-muted uppercase tracking-wide mb-2">
              {{ selectedModels.length }} Models
            </p>
            <button
              v-for="(model, idx) in selectedModels"
              :key="model.id || model._editName"
              @click="editorIndex = idx"
              :class="[
                'w-full text-left px-3 py-2 rounded-lg text-sm transition-colors truncate',
                editorIndex === idx
                  ? 'bg-forge-accent text-forge-bg font-medium'
                  : 'text-forge-text hover:bg-forge-bg',
              ]"
            >
              {{ model._editName || model.name || 'Unnamed' }}
            </button>
          </div>

          <!-- Right: metadata editor -->
          <div class="flex-1 overflow-y-auto">
            <div v-if="activeModel" class="space-y-4">
              <div class="flex items-center justify-between">
                <h3 class="text-base font-semibold text-forge-text">
                  {{ activeModel._editName || 'Metadata' }}
                </h3>
                <span class="text-xs text-forge-text-muted">
                  {{ editorIndex + 1 }} / {{ selectedModels.length }}
                </span>
              </div>

              <div class="grid grid-cols-1 sm:grid-cols-2 gap-4">
                <!-- Creator -->
                <div>
                  <label class="block text-xs font-medium text-forge-text-muted mb-1">Creator</label>
                  <input
                    :value="activeModel._editCreator"
                    @input="activeModel._editCreator = $event.target.value"
                    type="text"
                    placeholder="Creator name"
                    class="w-full bg-forge-bg border border-forge-border rounded-lg px-3 py-2 text-sm text-forge-text placeholder:text-forge-text-muted focus:outline-none focus:border-forge-accent"
                  />
                </div>

                <!-- Source -->
                <div>
                  <label class="block text-xs font-medium text-forge-text-muted mb-1">Source</label>
                  <select
                    :value="activeModel._metadata.source"
                    @change="activeModel._metadata.source = $event.target.value"
                    class="w-full bg-forge-bg border border-forge-border rounded-lg px-3 py-2 text-sm text-forge-text focus:outline-none focus:border-forge-accent"
                  >
                    <option v-for="src in SOURCES" :key="src" :value="src">{{ src }}</option>
                  </select>
                </div>

                <!-- Scale -->
                <div>
                  <label class="block text-xs font-medium text-forge-text-muted mb-1">Scale</label>
                  <select
                    :value="activeModel._metadata.scale"
                    @change="activeModel._metadata.scale = $event.target.value"
                    class="w-full bg-forge-bg border border-forge-border rounded-lg px-3 py-2 text-sm text-forge-text focus:outline-none focus:border-forge-accent"
                  >
                    <option value="">— Select scale —</option>
                    <option v-for="sc in SCALES" :key="sc" :value="sc">{{ sc }}</option>
                  </select>
                </div>

                <!-- Game System -->
                <div>
                  <label class="block text-xs font-medium text-forge-text-muted mb-1">Game System</label>
                  <input
                    :value="activeModel._metadata.gameSystem"
                    @input="activeModel._metadata.gameSystem = $event.target.value"
                    type="text"
                    placeholder="e.g. Warhammer 40K"
                    class="w-full bg-forge-bg border border-forge-border rounded-lg px-3 py-2 text-sm text-forge-text placeholder:text-forge-text-muted focus:outline-none focus:border-forge-accent"
                  />
                </div>

                <!-- Category -->
                <div>
                  <label class="block text-xs font-medium text-forge-text-muted mb-1">Category</label>
                  <input
                    :value="activeModel._metadata.category"
                    @input="activeModel._metadata.category = $event.target.value"
                    type="text"
                    placeholder="e.g. Terrain, Infantry"
                    class="w-full bg-forge-bg border border-forge-border rounded-lg px-3 py-2 text-sm text-forge-text placeholder:text-forge-text-muted focus:outline-none focus:border-forge-accent"
                  />
                </div>

                <!-- License -->
                <div>
                  <label class="block text-xs font-medium text-forge-text-muted mb-1">License</label>
                  <input
                    :value="activeModel._metadata.license"
                    @input="activeModel._metadata.license = $event.target.value"
                    type="text"
                    placeholder="e.g. CC BY-NC"
                    class="w-full bg-forge-bg border border-forge-border rounded-lg px-3 py-2 text-sm text-forge-text placeholder:text-forge-text-muted focus:outline-none focus:border-forge-accent"
                  />
                </div>

                <!-- Collection -->
                <div class="sm:col-span-2">
                  <label class="block text-xs font-medium text-forge-text-muted mb-1">Collection</label>
                  <input
                    :value="activeModel._metadata.collection"
                    @input="activeModel._metadata.collection = $event.target.value"
                    type="text"
                    placeholder="e.g. Space Marines Omnibus"
                    class="w-full bg-forge-bg border border-forge-border rounded-lg px-3 py-2 text-sm text-forge-text placeholder:text-forge-text-muted focus:outline-none focus:border-forge-accent"
                  />
                </div>

                <!-- Tags chip input -->
                <div class="sm:col-span-2">
                  <label class="block text-xs font-medium text-forge-text-muted mb-1">Tags</label>
                  <div class="bg-forge-bg border border-forge-border rounded-lg px-3 py-2 flex flex-wrap gap-1.5 min-h-[42px] focus-within:border-forge-accent transition-colors">
                    <span
                      v-for="tag in activeModel._metadata.tags"
                      :key="tag"
                      class="inline-flex items-center gap-1 bg-forge-accent/20 text-forge-accent text-xs px-2 py-0.5 rounded-full"
                    >
                      {{ tag }}
                      <button @click="removeTag(activeModel, tag)" class="hover:text-forge-danger transition-colors leading-none">×</button>
                    </span>
                    <input
                      v-model="tagInput"
                      @keydown="onTagKeydown(activeModel, $event)"
                      @blur="if (tagInput.trim()) { addTag(activeModel, tagInput); tagInput = '' }"
                      type="text"
                      placeholder="Add tag, press Enter…"
                      class="flex-1 min-w-24 bg-transparent text-sm text-forge-text placeholder:text-forge-text-muted focus:outline-none"
                    />
                  </div>
                </div>
              </div>

              <!-- Nav arrows -->
              <div class="flex gap-2 pt-2">
                <button
                  v-if="editorIndex > 0"
                  @click="editorIndex--"
                  class="px-3 py-1.5 rounded-lg text-sm bg-forge-bg border border-forge-border text-forge-text hover:bg-forge-card transition-colors"
                >
                  ← Prev
                </button>
                <button
                  v-if="editorIndex < selectedModels.length - 1"
                  @click="editorIndex++"
                  class="px-3 py-1.5 rounded-lg text-sm bg-forge-bg border border-forge-border text-forge-text hover:bg-forge-card transition-colors ml-auto"
                >
                  Next →
                </button>
              </div>
            </div>

            <div v-else class="flex items-center justify-center h-full text-forge-text-muted text-sm">
              No models selected.
            </div>
          </div>
        </div>

        <!-- ─── STEP 4: Preview & Confirm ─────────────────────────────────── -->
        <div v-else-if="step === 4" class="space-y-5">
          <!-- Rename template -->
          <div>
            <label class="block text-sm font-medium text-forge-text mb-1">Rename Template</label>
            <input
              v-model="renameTemplate"
              type="text"
              class="w-full bg-forge-bg border border-forge-border rounded-lg px-3 py-2 text-sm text-forge-text font-mono focus:outline-none focus:border-forge-accent"
            />
            <div class="flex flex-wrap gap-1.5 mt-2">
              <button
                v-for="token in TOKEN_HINTS"
                :key="token"
                @click="renameTemplate += token"
                class="text-xs px-2 py-0.5 bg-forge-accent/10 text-forge-accent border border-forge-accent/30 rounded hover:bg-forge-accent/20 transition-colors font-mono"
              >
                {{ token }}
              </button>
            </div>
          </div>

          <!-- Path preview list -->
          <div>
            <p class="text-sm font-medium text-forge-text mb-2">
              Path Preview ({{ previewPaths.length }} files)
            </p>
            <div class="overflow-y-auto max-h-52 rounded-xl border border-forge-border bg-forge-bg divide-y divide-forge-border">
              <div
                v-for="(p, idx) in previewPaths"
                :key="idx"
                class="px-3 py-2 text-xs font-mono"
              >
                <div class="text-forge-text-muted truncate">{{ p.original }}</div>
                <div :class="['truncate', p.changed ? 'text-forge-accent' : 'text-forge-text-muted']">
                  → {{ p.newPath }}
                </div>
              </div>
              <div v-if="!previewPaths.length" class="px-3 py-6 text-center text-forge-text-muted">
                No files to preview.
              </div>
            </div>
          </div>

          <!-- Options -->
          <div class="grid grid-cols-1 sm:grid-cols-2 gap-4">
            <div class="space-y-2">
              <p class="text-sm font-medium text-forge-text">Options</p>
              <label class="flex items-center gap-2 cursor-pointer select-none">
                <input v-model="optExtract" type="checkbox" class="rounded border-forge-border accent-forge-accent w-4 h-4" />
                <span class="text-sm text-forge-text">Extract archives (.zip, .7z)</span>
              </label>
              <label class="flex items-center gap-2 cursor-pointer select-none">
                <input v-model="optThumbnails" type="checkbox" class="rounded border-forge-border accent-forge-accent w-4 h-4" />
                <span class="text-sm text-forge-text">Generate thumbnails</span>
              </label>
              <label class="flex items-center gap-2 cursor-pointer select-none">
                <input v-model="optMetadataJson" type="checkbox" class="rounded border-forge-border accent-forge-accent w-4 h-4" />
                <span class="text-sm text-forge-text">Write metadata.json</span>
              </label>
            </div>

            <div class="space-y-2">
              <p class="text-sm font-medium text-forge-text">Import Mode</p>
              <label class="flex items-center gap-2 cursor-pointer select-none">
                <input v-model="importMode" type="radio" value="copy" class="accent-forge-accent w-4 h-4" />
                <span class="text-sm text-forge-text">Copy files (keep originals)</span>
              </label>
              <label class="flex items-center gap-2 cursor-pointer select-none">
                <input v-model="importMode" type="radio" value="move" class="accent-forge-accent w-4 h-4" />
                <span class="text-sm text-forge-text">Move files (remove originals)</span>
              </label>
            </div>
          </div>
        </div>

      </div>

      <!-- Footer -->
      <div class="flex items-center justify-between px-6 py-4 border-t border-forge-border shrink-0">
        <!-- Back -->
        <button
          v-if="step > 1"
          @click="prevStep"
          class="px-4 py-2 rounded-lg text-sm font-medium bg-forge-bg border border-forge-border text-forge-text hover:bg-forge-card transition-colors"
        >
          ← Back
        </button>
        <div v-else></div>

        <!-- Right actions -->
        <div class="flex items-center gap-2">
          <button
            @click="emit('close')"
            class="px-4 py-2 rounded-lg text-sm font-medium text-forge-text-muted hover:text-forge-text transition-colors"
          >
            Cancel
          </button>

          <!-- Step 1: Scan -->
          <button
            v-if="step === 1"
            @click="runScan"
            :disabled="!folderPath.trim() || scanning"
            class="px-5 py-2 rounded-lg text-sm font-medium bg-forge-accent hover:bg-forge-accent-hover text-forge-bg disabled:opacity-40 disabled:cursor-not-allowed transition-colors"
          >
            {{ scanning ? '🔄 Scanning…' : '🔍 Scan Folder' }}
          </button>

          <!-- Step 2: Next -->
          <button
            v-else-if="step === 2"
            @click="step = 3"
            :disabled="!canProceedStep2"
            class="px-5 py-2 rounded-lg text-sm font-medium bg-forge-accent hover:bg-forge-accent-hover text-forge-bg disabled:opacity-40 disabled:cursor-not-allowed transition-colors"
          >
            Next → Edit Metadata
          </button>

          <!-- Step 3: Next -->
          <button
            v-else-if="step === 3"
            @click="step = 4"
            class="px-5 py-2 rounded-lg text-sm font-medium bg-forge-accent hover:bg-forge-accent-hover text-forge-bg transition-colors"
          >
            Next → Preview
          </button>

          <!-- Step 4: Import -->
          <button
            v-else-if="step === 4"
            @click="doImport"
            :disabled="importing || !selectedModels.length"
            class="px-5 py-2 rounded-lg text-sm font-medium bg-forge-accent hover:bg-forge-accent-hover text-forge-bg disabled:opacity-40 disabled:cursor-not-allowed transition-colors"
          >
            {{ importing ? '⏳ Importing…' : `📥 Import ${selectedModels.length} Model${selectedModels.length !== 1 ? 's' : ''}` }}
          </button>
        </div>
      </div>

    </div>
  </div>
</template>

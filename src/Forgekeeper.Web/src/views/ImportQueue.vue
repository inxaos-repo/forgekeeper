<!--
  ImportQueue.vue — Multi-directory import watch workflow
  ─────────────────────────────────────────────────────────
  Top section: WatchDirectoriesPanel (settings panel)
  Queue section: task list / inbox feel
    - Batch actions (select all, bulk confirm, dismiss)
    - Filtering by status + source dir + sort
    - Quick Import (one-click) + Edit & Import (form)
    - File preview expand
    - Import Activity Feed (collapsed at bottom)
-->
<script setup>
import { ref, computed, onMounted } from 'vue'
import { useApi } from '../composables/useApi.js'
import SourceBadge from '../components/SourceBadge.vue'
import ImportWizard from '../components/ImportWizard.vue'
import WatchDirectoriesPanel from '../components/WatchDirectoriesPanel.vue'

const api = useApi()

// ─── State ─────────────────────────────────────────────────
const items = ref([])
const scanning = ref(false)
const showWizard = ref(false)

// Filters + sort
const filterStatus = ref('')
const filterSourceDir = ref('')
const sortBy = ref('confidence')  // confidence | date | name

// Batch selection
const selectedIds = ref(new Set())
const bulkConfirming = ref(false)
const bulkDismissing = ref(false)

// Activity feed
const showActivityFeed = ref(false)
const activityItems = ref([])
const showAutoSorted = ref(false)

const sources = ['mmf', 'thangs', 'patreon', 'cults3d', 'thingiverse', 'manual']

const STATUS_TABS = [
  { value: '', label: 'All' },
  { value: 'AwaitingReview', label: 'Awaiting Review' },
  { value: 'Pending', label: 'Pending' },
  { value: 'AutoSorted', label: 'Auto-sorted' },
  { value: 'Failed', label: 'Failed' },
]

// ─── Computed ──────────────────────────────────────────────

const pendingItems = computed(() =>
  items.value.filter(i => {
    const s = (i.status || '').toLowerCase()
    return s !== 'autosorted' && s !== 'confirmed' && s !== 'failed'
  })
)

const autoSortedItems = computed(() =>
  items.value.filter(i => (i.status || '').toLowerCase() === 'autosorted')
)

const failedItems = computed(() =>
  items.value.filter(i => (i.status || '').toLowerCase() === 'failed')
)

const highConfidenceItems = computed(() =>
  pendingItems.value.filter(i => confidence(i) >= 0.8)
)

/** Unique source directories for the filter dropdown */
const sourceDirs = computed(() => {
  const dirs = new Set()
  items.value.forEach(i => {
    const d = i.sourceDirectory || i.watchDirectory || ''
    if (d) dirs.add(d)
  })
  return [...dirs]
})

const filteredItems = computed(() => {
  let list = pendingItems.value

  if (filterSourceDir.value) {
    list = list.filter(i =>
      (i.sourceDirectory || i.watchDirectory || '') === filterSourceDir.value
    )
  }

  list = [...list].sort((a, b) => {
    if (sortBy.value === 'confidence') {
      return confidence(b) - confidence(a)
    }
    if (sortBy.value === 'date') {
      return new Date(b.detectedAt || 0) - new Date(a.detectedAt || 0)
    }
    if (sortBy.value === 'name') {
      const na = (a.detectedModelName || a.detectedFilename || '').toLowerCase()
      const nb = (b.detectedModelName || b.detectedFilename || '').toLowerCase()
      return na.localeCompare(nb)
    }
    return 0
  })

  return list
})

const allSelected = computed(() =>
  filteredItems.value.length > 0 &&
  filteredItems.value.every(i => selectedIds.value.has(i.id))
)

const selectedItems = computed(() =>
  filteredItems.value.filter(i => selectedIds.value.has(i.id))
)

// ─── Helpers ───────────────────────────────────────────────

function confidence(item) {
  return item.confidence ?? item.confidenceScore ?? 0
}

function confidenceColor(c) {
  if (c >= 0.8) return 'text-forge-accent'
  if (c >= 0.5) return 'text-yellow-400'
  return 'text-forge-danger'
}

function confidenceBarColor(c) {
  if (c >= 0.8) return 'bg-forge-accent'
  if (c >= 0.5) return 'bg-yellow-400'
  return 'bg-forge-danger'
}

function confidenceLabel(c) {
  if (c >= 0.8) return 'High'
  if (c >= 0.5) return 'Medium'
  return 'Low'
}

function formatBytes(bytes) {
  if (!bytes) return '0 B'
  if (bytes < 1024) return `${bytes} B`
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`
  return `${(bytes / (1024 * 1024)).toFixed(1)} MB`
}

function fileIcon(ext) {
  const e = (ext || '').toLowerCase()
  if (e === 'stl') return '🔷'
  if (e === '3mf') return '📦'
  if (e === 'lys') return '🪄'
  if (e === 'ctb' || e === 'cbddlp') return '🖨️'
  if (e === 'gcode') return '⚙️'
  if (e === 'zip') return '🗜️'
  if (['jpg', 'jpeg', 'png', 'webp'].includes(e)) return '🖼️'
  return '📄'
}

function formatTimestamp(ts) {
  if (!ts) return ''
  const d = new Date(ts)
  return d.toLocaleString()
}

function formatPath(p) {
  if (!p) return ''
  if (p.length <= 60) return p
  const parts = p.replace(/\\/g, '/').split('/')
  return '…/' + parts.slice(-2).join('/')
}

// ─── Data Loading ──────────────────────────────────────────

function mapItem(item) {
  return {
    ...item,
    editCreator: item.suggestedCreator || item.detectedCreator || '',
    editSource: item.suggestedSource || (item.detectedSource != null ? String(item.detectedSource).toLowerCase() : 'manual'),
    editName: item.suggestedName || item.detectedModelName || item.detectedFilename || '',
    editTags: item.suggestedTags || [],
    confirming: false,
    dismissing: false,
    showFiles: false,
    editing: false,  // show edit form vs quick-import mode
  }
}

async function fetchQueue() {
  try {
    const params = {}
    if (filterStatus.value) params.status = filterStatus.value
    const result = await api.getImportQueue(params)
    const raw = result?.items || result || []
    items.value = raw.map(mapItem)
    // Clear stale selections
    selectedIds.value = new Set([...selectedIds.value].filter(id => items.value.some(i => i.id === id)))
  } catch {
    items.value = []
  }
}

async function triggerScan() {
  scanning.value = true
  try {
    await api.processUnsorted()
    await fetchQueue()
  } catch { /* error via api.error */ } finally {
    scanning.value = false
  }
}

// ─── Item Actions ──────────────────────────────────────────

async function confirmItem(item) {
  item.confirming = true
  try {
    await api.confirmImport(item.id, {
      creator: item.editCreator,
      modelName: item.editName,
      sourceSlug: item.editSource,
      tags: item.editTags,
    })
    // Move to activity feed
    activityItems.value.unshift({
      id: item.id,
      modelName: item.editName,
      creator: item.editCreator,
      sourceDir: item.sourceDirectory || item.watchDirectory || '',
      method: item.editing ? 'manual' : 'quick',
      confidence: confidence(item),
      confirmedAt: new Date().toISOString(),
    })
    items.value = items.value.filter(i => i.id !== item.id)
    selectedIds.value.delete(item.id)
  } catch { /* error shown */ } finally {
    item.confirming = false
  }
}

async function dismissItem(item) {
  item.dismissing = true
  try {
    await api.dismissImport(item.id)
    items.value = items.value.filter(i => i.id !== item.id)
    selectedIds.value.delete(item.id)
  } catch { /* error shown */ } finally {
    item.dismissing = false
  }
}

// ─── Batch Actions ─────────────────────────────────────────

function toggleSelectAll() {
  if (allSelected.value) {
    filteredItems.value.forEach(i => selectedIds.value.delete(i.id))
    selectedIds.value = new Set(selectedIds.value) // trigger reactivity
  } else {
    filteredItems.value.forEach(i => selectedIds.value.add(i.id))
    selectedIds.value = new Set(selectedIds.value)
  }
}

function toggleSelect(id) {
  const s = new Set(selectedIds.value)
  if (s.has(id)) s.delete(id)
  else s.add(id)
  selectedIds.value = s
}

async function bulkConfirmSelected() {
  bulkConfirming.value = true
  const targets = selectedItems.value.slice()
  for (const item of targets) {
    if (!item.editName || !item.editCreator) continue
    await confirmItem(item)
  }
  bulkConfirming.value = false
}

async function bulkConfirmHighConfidence() {
  bulkConfirming.value = true
  const targets = highConfidenceItems.value.slice()
  for (const item of targets) {
    await confirmItem(item)
  }
  bulkConfirming.value = false
}

async function bulkDismissSelected() {
  bulkDismissing.value = true
  const targets = selectedItems.value.slice()
  for (const item of targets) {
    await dismissItem(item)
  }
  selectedIds.value = new Set()
  bulkDismissing.value = false
}

onMounted(fetchQueue)
</script>

<template>
  <div>
    <!-- ── Watch Directories Panel ──────────────────────────── -->
    <WatchDirectoriesPanel @scan-complete="fetchQueue" />

    <!-- ── Queue Header ─────────────────────────────────────── -->
    <div class="flex items-center justify-between mb-4">
      <div>
        <h1 class="text-2xl font-bold text-forge-text">Import Queue</h1>
        <p class="text-sm text-forge-text-muted mt-1">
          <span :class="pendingItems.length ? 'text-forge-text' : ''">
            {{ pendingItems.length }} item{{ pendingItems.length !== 1 ? 's' : '' }} pending
          </span>
          <span v-if="autoSortedItems.length" class="ml-2 text-forge-accent">
            · {{ autoSortedItems.length }} auto-sorted
          </span>
          <span v-if="failedItems.length" class="ml-2 text-forge-danger">
            · {{ failedItems.length }} failed
          </span>
        </p>
      </div>
      <div class="flex items-center gap-2">
        <button
          v-if="highConfidenceItems.length && !bulkConfirming"
          @click="bulkConfirmHighConfidence"
          class="px-4 py-2 rounded-lg text-sm font-medium bg-forge-card border border-forge-accent text-forge-accent hover:bg-forge-accent/10 transition-colors"
        >
          ✓ Import {{ highConfidenceItems.length }} high confidence
        </button>
        <button
          v-else-if="bulkConfirming"
          disabled
          class="px-4 py-2 rounded-lg text-sm font-medium bg-forge-card border border-forge-border text-forge-text-muted cursor-not-allowed"
        >
          🔄 Importing…
        </button>
        <button
          @click="showWizard = true"
          class="px-4 py-2 rounded-lg text-sm font-medium bg-forge-card border border-forge-border text-forge-text hover:text-forge-accent transition-colors"
        >
          📥 Manual Import
        </button>
        <button
          @click="triggerScan"
          :disabled="scanning"
          :class="[
            'px-4 py-2 rounded-lg text-sm font-medium transition-colors',
            scanning
              ? 'bg-forge-card text-forge-text-muted cursor-not-allowed'
              : 'bg-forge-accent hover:bg-forge-accent-hover text-forge-bg',
          ]"
        >
          {{ scanning ? '🔄 Scanning…' : '🔍 Scan Unsorted' }}
        </button>
      </div>
    </div>

    <!-- Scanning indicator -->
    <div v-if="scanning" class="bg-forge-card border border-forge-accent/30 rounded-xl p-4 mb-4">
      <div class="flex items-center gap-3 text-sm">
        <div class="w-5 h-5 border-2 border-forge-accent border-t-transparent rounded-full animate-spin shrink-0"></div>
        <p class="text-forge-text font-medium">Scanning unsorted files…</p>
      </div>
    </div>

    <!-- ── Filters + Sort ────────────────────────────────────── -->
    <div class="flex flex-wrap items-center gap-2 mb-4">
      <!-- Status tabs -->
      <div class="flex gap-1">
        <button
          v-for="tab in STATUS_TABS"
          :key="tab.value"
          @click="filterStatus = tab.value; fetchQueue()"
          :class="[
            'px-3 py-1.5 rounded-lg text-xs font-medium transition-colors',
            filterStatus === tab.value
              ? 'bg-forge-accent text-forge-bg'
              : 'bg-forge-card border border-forge-border text-forge-text-muted hover:text-forge-text',
          ]"
        >
          {{ tab.label }}
        </button>
      </div>

      <!-- Source dir filter -->
      <select
        v-if="sourceDirs.length"
        v-model="filterSourceDir"
        class="ml-2 bg-forge-card border border-forge-border rounded-lg px-2 py-1.5 text-xs text-forge-text focus:outline-none focus:border-forge-accent"
      >
        <option value="">All directories</option>
        <option v-for="d in sourceDirs" :key="d" :value="d">{{ formatPath(d) }}</option>
      </select>

      <!-- Sort -->
      <div class="ml-auto flex items-center gap-1.5 text-xs text-forge-text-muted">
        <span>Sort:</span>
        <button
          v-for="s in [['confidence', '🎯 Confidence'], ['date', '📅 Date'], ['name', '🔤 Name']]"
          :key="s[0]"
          @click="sortBy = s[0]"
          :class="[
            'px-2.5 py-1 rounded transition-colors',
            sortBy === s[0]
              ? 'bg-forge-card border border-forge-accent text-forge-accent'
              : 'hover:text-forge-text',
          ]"
        >
          {{ s[1] }}
        </button>
      </div>
    </div>

    <!-- ── Batch Action Bar ──────────────────────────────────── -->
    <div
      v-if="filteredItems.length"
      class="flex items-center gap-3 bg-forge-card border border-forge-border rounded-xl px-4 py-2.5 mb-4 text-sm"
    >
      <!-- Select all -->
      <label class="flex items-center gap-2 cursor-pointer text-forge-text-muted hover:text-forge-text">
        <input
          type="checkbox"
          :checked="allSelected"
          @change="toggleSelectAll"
          class="w-4 h-4 accent-forge-accent"
        />
        <span class="text-xs">{{ allSelected ? 'Deselect all' : 'Select all' }}</span>
      </label>
      <span class="text-forge-border">|</span>
      <span class="text-xs text-forge-text-muted">{{ selectedIds.size }} selected</span>

      <template v-if="selectedIds.size">
        <button
          @click="bulkConfirmSelected"
          :disabled="bulkConfirming"
          class="ml-1 px-3 py-1 rounded-lg text-xs font-medium bg-forge-accent/10 border border-forge-accent text-forge-accent hover:bg-forge-accent/20 disabled:opacity-50 transition-colors"
        >
          ✓ Import selected
        </button>
        <button
          @click="bulkDismissSelected"
          :disabled="bulkDismissing"
          class="px-3 py-1 rounded-lg text-xs font-medium bg-forge-bg border border-forge-border text-forge-text-muted hover:text-forge-danger hover:border-forge-danger disabled:opacity-50 transition-colors"
        >
          ✕ Dismiss selected
        </button>
      </template>
    </div>

    <!-- ── Loading ───────────────────────────────────────────── -->
    <div v-if="api.loading.value && !items.length" class="flex justify-center py-20">
      <div class="w-8 h-8 border-2 border-forge-accent border-t-transparent rounded-full animate-spin"></div>
    </div>

    <!-- ── Empty state ───────────────────────────────────────── -->
    <div v-else-if="!items.length" class="text-center py-20">
      <span class="text-5xl">📭</span>
      <p class="text-forge-text-muted mt-4">No files pending import</p>
      <p class="text-sm text-forge-text-muted mt-1">
        Drop files into a watch directory then click <strong class="text-forge-accent">Scan All</strong> or <strong class="text-forge-accent">Scan Unsorted</strong>
      </p>
    </div>

    <template v-else>
      <!-- ── Pending / AwaitingReview items ─────────────────── -->
      <div v-if="filteredItems.length" class="space-y-3 mb-8">
        <div
          v-for="item in filteredItems"
          :key="item.id"
          :class="[
            'bg-forge-card border rounded-xl transition-colors',
            confidence(item) >= 0.8 ? 'border-forge-accent/40' : 'border-forge-border',
            selectedIds.has(item.id) ? 'ring-1 ring-forge-accent/50' : '',
          ]"
        >
          <div class="p-5">
            <div class="flex items-start gap-3">
              <!-- Checkbox -->
              <input
                type="checkbox"
                :checked="selectedIds.has(item.id)"
                @change="toggleSelect(item.id)"
                class="mt-1 w-4 h-4 accent-forge-accent shrink-0 cursor-pointer"
              />

              <div class="flex-1 min-w-0 flex flex-col lg:flex-row lg:items-start gap-4">
                <!-- Left: info + fields -->
                <div class="flex-1 space-y-3 min-w-0">
                  <!-- File path + source dir badge -->
                  <div class="flex items-start gap-2 min-w-0">
                    <span class="text-lg shrink-0">📁</span>
                    <div class="min-w-0 flex-1">
                      <p class="text-sm font-semibold text-forge-text break-all">
                        {{ item.detectedModelName || item.detectedFilename || item.originalPath }}
                      </p>
                      <p class="text-xs text-forge-text-muted font-mono mt-0.5 break-all">
                        {{ item.originalPath }}
                      </p>
                      <div class="flex items-center gap-2 mt-1">
                        <span v-if="item.sourceDirectory || item.watchDirectory"
                          class="text-xs px-1.5 py-0.5 rounded bg-forge-bg border border-forge-border text-forge-text-muted font-mono"
                          :title="item.sourceDirectory || item.watchDirectory">
                          📂 {{ formatPath(item.sourceDirectory || item.watchDirectory) }}
                        </span>
                        <span v-if="item.detectedAt" class="text-xs text-forge-text-muted">
                          {{ formatTimestamp(item.detectedAt) }}
                        </span>
                      </div>
                    </div>
                  </div>

                  <!-- Confidence bar -->
                  <div v-if="confidence(item) > 0" class="space-y-1">
                    <div class="flex items-center justify-between">
                      <span class="text-xs text-forge-text-muted">Detection confidence</span>
                      <span :class="['text-xs font-semibold', confidenceColor(confidence(item))]">
                        {{ confidenceLabel(confidence(item)) }} ({{ Math.round(confidence(item) * 100) }}%)
                      </span>
                    </div>
                    <div class="h-1.5 bg-forge-bg rounded-full overflow-hidden">
                      <div
                        :class="[confidenceBarColor(confidence(item)), 'h-full rounded-full transition-all']"
                        :style="{ width: `${Math.round(confidence(item) * 100)}%` }"
                      ></div>
                    </div>
                  </div>

                  <!-- Edit form (always visible in editing mode, collapsed otherwise) -->
                  <div v-if="item.editing" class="grid grid-cols-1 sm:grid-cols-3 gap-3 pt-1">
                    <div>
                      <label class="block text-xs text-forge-text-muted mb-1">Model Name</label>
                      <input
                        v-model="item.editName"
                        type="text"
                        placeholder="Model name…"
                        class="w-full bg-forge-bg border border-forge-border rounded-lg px-3 py-1.5 text-sm text-forge-text focus:outline-none focus:border-forge-accent"
                      />
                    </div>
                    <div>
                      <label class="block text-xs text-forge-text-muted mb-1">Creator</label>
                      <input
                        v-model="item.editCreator"
                        type="text"
                        placeholder="Creator name…"
                        class="w-full bg-forge-bg border border-forge-border rounded-lg px-3 py-1.5 text-sm text-forge-text focus:outline-none focus:border-forge-accent"
                      />
                    </div>
                    <div>
                      <label class="block text-xs text-forge-text-muted mb-1">Source</label>
                      <select
                        v-model="item.editSource"
                        class="w-full bg-forge-bg border border-forge-border rounded-lg px-3 py-1.5 text-sm text-forge-text focus:outline-none focus:border-forge-accent"
                      >
                        <option v-for="src in sources" :key="src" :value="src">{{ src }}</option>
                      </select>
                    </div>
                  </div>

                  <!-- Quick summary (when not editing) -->
                  <div v-else class="flex flex-wrap gap-3 text-xs text-forge-text-muted">
                    <span v-if="item.editCreator" class="flex items-center gap-1">
                      <span>👤</span>
                      <span class="text-forge-text">{{ item.editCreator }}</span>
                    </span>
                    <span v-if="item.editName && item.editName !== (item.detectedModelName || item.detectedFilename)" class="flex items-center gap-1">
                      <span>📝</span>
                      <span class="text-forge-text">{{ item.editName }}</span>
                    </span>
                    <span v-if="item.editSource" class="flex items-center gap-1">
                      <span>🔗</span>
                      <span>{{ item.editSource }}</span>
                    </span>
                  </div>

                  <!-- File preview toggle -->
                  <div v-if="item.files?.length">
                    <button
                      @click="item.showFiles = !item.showFiles"
                      class="flex items-center gap-1.5 text-xs text-forge-text-muted hover:text-forge-accent transition-colors"
                    >
                      <span>{{ item.showFiles ? '▾' : '▸' }}</span>
                      <span>{{ item.files.length }} file{{ item.files.length !== 1 ? 's' : '' }}
                        ({{ formatBytes(item.files.reduce((a, f) => a + (f.sizeBytes || 0), 0)) }})</span>
                    </button>
                    <div v-if="item.showFiles" class="mt-2 space-y-1 bg-forge-bg rounded-lg p-3 max-h-48 overflow-y-auto">
                      <div
                        v-for="file in item.files"
                        :key="file.relativePath || file.fileName"
                        class="flex items-center gap-2 text-xs"
                      >
                        <span class="shrink-0">{{ fileIcon(file.fileType) }}</span>
                        <span class="text-forge-text-muted font-mono truncate flex-1">{{ file.relativePath || file.fileName }}</span>
                        <span v-if="file.detectedVariant" class="shrink-0 px-1.5 py-0.5 rounded bg-forge-card text-forge-accent text-xs">
                          {{ file.detectedVariant }}
                        </span>
                        <span class="shrink-0 text-forge-text-muted">{{ formatBytes(file.sizeBytes) }}</span>
                      </div>
                    </div>
                  </div>
                </div>

                <!-- Right: Action buttons -->
                <div class="flex lg:flex-col gap-2 shrink-0">
                  <template v-if="!item.editing">
                    <!-- Quick Import -->
                    <button
                      @click="confirmItem(item)"
                      :disabled="item.confirming || !item.editName || !item.editCreator"
                      :class="[
                        'px-4 py-2 rounded-lg text-sm font-medium transition-colors whitespace-nowrap',
                        (item.confirming || !item.editName || !item.editCreator)
                          ? 'bg-forge-accent/40 text-forge-bg cursor-not-allowed'
                          : 'bg-forge-accent hover:bg-forge-accent-hover text-forge-bg',
                      ]"
                      :title="confidence(item) >= 0.8 ? 'High confidence — import with detected values' : 'Import with current values'"
                    >
                      {{ item.confirming ? '…' : '⚡ Quick Import' }}
                    </button>
                    <!-- Edit & Import -->
                    <button
                      @click="item.editing = true"
                      class="px-4 py-2 rounded-lg text-sm font-medium bg-forge-bg border border-forge-border text-forge-text-muted hover:text-forge-accent hover:border-forge-accent transition-colors whitespace-nowrap"
                    >
                      ✏️ Edit & Import
                    </button>
                  </template>
                  <template v-else>
                    <!-- Confirm from edit form -->
                    <button
                      @click="confirmItem(item)"
                      :disabled="item.confirming || !item.editName || !item.editCreator"
                      :class="[
                        'px-4 py-2 rounded-lg text-sm font-medium transition-colors whitespace-nowrap',
                        (item.confirming || !item.editName || !item.editCreator)
                          ? 'bg-forge-accent/40 text-forge-bg cursor-not-allowed'
                          : 'bg-forge-accent hover:bg-forge-accent-hover text-forge-bg',
                      ]"
                    >
                      {{ item.confirming ? '…' : '✓ Confirm' }}
                    </button>
                    <!-- Cancel edit -->
                    <button
                      @click="item.editing = false"
                      class="px-4 py-2 rounded-lg text-sm font-medium bg-forge-bg border border-forge-border text-forge-text-muted hover:text-forge-text transition-colors"
                    >
                      Cancel
                    </button>
                  </template>
                  <!-- Dismiss -->
                  <button
                    @click="dismissItem(item)"
                    :disabled="item.dismissing"
                    class="px-4 py-2 rounded-lg text-sm font-medium bg-forge-bg border border-forge-border text-forge-text-muted hover:text-forge-danger hover:border-forge-danger transition-colors"
                  >
                    {{ item.dismissing ? '…' : '✕ Dismiss' }}
                  </button>
                </div>
              </div>
            </div>
          </div>
        </div>
      </div>

      <!-- ── Failed items ─────────────────────────────────────── -->
      <div v-if="failedItems.length" class="mb-6">
        <p class="text-sm font-medium text-forge-danger mb-2">⚠ Failed imports ({{ failedItems.length }})</p>
        <div class="space-y-2">
          <div
            v-for="item in failedItems"
            :key="item.id"
            class="bg-forge-card border border-forge-danger/30 rounded-xl p-4 flex items-center gap-4"
          >
            <span class="text-lg shrink-0">❌</span>
            <div class="flex-1 min-w-0">
              <p class="text-sm font-medium text-forge-text truncate">
                {{ item.detectedModelName || item.detectedFilename || item.originalPath }}
              </p>
              <p class="text-xs text-forge-danger mt-0.5">{{ item.errorMessage || 'Unknown error' }}</p>
            </div>
            <button
              @click="dismissItem(item)"
              class="shrink-0 px-3 py-1 rounded-lg text-xs border border-forge-border text-forge-text-muted hover:text-forge-danger hover:border-forge-danger transition-colors"
            >
              Dismiss
            </button>
          </div>
        </div>
      </div>

      <!-- ── Auto-sorted items (collapsible) ─────────────────── -->
      <div v-if="autoSortedItems.length" class="mb-6">
        <button
          @click="showAutoSorted = !showAutoSorted"
          class="flex items-center gap-2 text-sm font-medium text-forge-text-muted hover:text-forge-text transition-colors mb-3"
        >
          <span>{{ showAutoSorted ? '▾' : '▸' }}</span>
          <span>Auto-sorted ({{ autoSortedItems.length }})</span>
          <span class="text-xs text-forge-accent ml-1">confidence ≥ 80%</span>
        </button>
        <div v-if="showAutoSorted" class="space-y-2">
          <div
            v-for="item in autoSortedItems"
            :key="item.id"
            class="bg-forge-card border border-forge-accent/20 rounded-xl p-4 flex items-center gap-4"
          >
            <span class="text-lg shrink-0">✅</span>
            <div class="flex-1 min-w-0">
              <p class="text-sm font-medium text-forge-text truncate">
                {{ item.detectedModelName || item.detectedFilename }}
              </p>
              <p class="text-xs text-forge-text-muted mt-0.5 flex gap-3 flex-wrap">
                <span v-if="item.detectedCreator">👤 {{ item.detectedCreator }}</span>
                <span v-if="confidence(item)">{{ Math.round(confidence(item) * 100) }}% confidence</span>
                <span v-if="item.sourceDirectory || item.watchDirectory" class="font-mono">📂 {{ formatPath(item.sourceDirectory || item.watchDirectory) }}</span>
              </p>
            </div>
            <span class="text-xs text-forge-accent font-medium shrink-0">Auto-sorted</span>
          </div>
        </div>
      </div>

      <!-- ── Import Activity Feed ──────────────────────────────── -->
      <div v-if="activityItems.length" class="mt-4">
        <button
          @click="showActivityFeed = !showActivityFeed"
          class="flex items-center gap-2 text-sm font-medium text-forge-text-muted hover:text-forge-text transition-colors mb-3"
        >
          <span>{{ showActivityFeed ? '▾' : '▸' }}</span>
          <span>Activity feed ({{ activityItems.length }} this session)</span>
        </button>
        <div v-if="showActivityFeed" class="space-y-1.5 bg-forge-card border border-forge-border rounded-xl p-4">
          <div
            v-for="act in activityItems"
            :key="act.id + act.confirmedAt"
            class="flex items-center gap-3 text-xs text-forge-text-muted py-1.5 border-b border-forge-border/50 last:border-0"
          >
            <span class="shrink-0">{{ act.method === 'manual' ? '✏️' : '⚡' }}</span>
            <div class="flex-1 min-w-0">
              <span class="text-forge-text font-medium truncate">{{ act.modelName }}</span>
              <span class="ml-2">by {{ act.creator }}</span>
              <span v-if="act.sourceDir" class="ml-2 font-mono">📂 {{ formatPath(act.sourceDir) }}</span>
            </div>
            <span class="shrink-0">{{ Math.round(act.confidence * 100) }}%</span>
            <span class="shrink-0 text-forge-text-muted">{{ formatTimestamp(act.confirmedAt) }}</span>
          </div>
        </div>
      </div>
    </template>

    <!-- Error toast -->
    <div
      v-if="api.error.value"
      class="fixed bottom-4 right-4 bg-forge-danger/90 text-white px-4 py-2 rounded-lg shadow-lg text-sm z-50"
    >
      {{ api.error.value }}
    </div>

    <!-- Import Wizard -->
    <ImportWizard v-if="showWizard" @close="showWizard = false; fetchQueue()" />
  </div>
</template>



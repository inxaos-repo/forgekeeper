<!--
  ImportQueue.vue — Import queue for unsorted files
  Shows pending items with confidence, lets user confirm/edit metadata or dismiss.
  Scan Unsorted → detects creators, confidence scores → user reviews/confirms/dismisses.
  High-confidence (≥0.8) items shown with auto-confirm option.
  Auto-sorted items (already processed) shown in a separate collapsed section.
-->
<script setup>
import { ref, computed, onMounted, onBeforeUnmount } from 'vue'
import { useApi } from '../composables/useApi.js'
import SourceBadge from '../components/SourceBadge.vue'
import ImportWizard from '../components/ImportWizard.vue'

const api = useApi()

const items = ref([])
const scanning = ref(false)
const filterStatus = ref('')
const showWizard = ref(false)
const showAutoSorted = ref(false)

const sources = ['mmf', 'thangs', 'patreon', 'cults3d', 'thingiverse', 'manual']

// Split items by status
const pendingItems = computed(() =>
  items.value.filter(i => {
    const s = (i.status || '').toLowerCase()
    return s !== 'autosorted' && s !== 'confirmed' && s !== 'failed'
  })
)

const autoSortedItems = computed(() =>
  items.value.filter(i => (i.status || '').toLowerCase() === 'autosorted')
)

const highConfidenceItems = computed(() =>
  pendingItems.value.filter(i => (i.confidence ?? i.confidenceScore ?? 0) >= 0.8)
)

async function fetchQueue() {
  try {
    const params = {}
    if (filterStatus.value) params.status = filterStatus.value
    const result = await api.getImportQueue(params)
    const raw = result?.items || result || []
    items.value = raw.map((item) => ({
      ...item,
      editCreator: item.suggestedCreator || item.detectedCreator || '',
      editSource: item.suggestedSource || (item.detectedSource != null ? String(item.detectedSource).toLowerCase() : 'manual'),
      editName: item.suggestedName || item.detectedModelName || item.detectedFilename || '',
      confirming: false,
      dismissing: false,
      showFiles: false,
    }))
  } catch {
    items.value = []
  }
}

async function triggerScan() {
  scanning.value = true
  try {
    // processUnsorted returns the newly detected items directly
    const newItems = await api.processUnsorted()
    // Refresh the full queue to get all pending items
    await fetchQueue()
  } catch (e) {
    // error shown via api.error.value
  } finally {
    scanning.value = false
  }
}

async function confirmItem(item) {
  item.confirming = true
  try {
    await api.confirmImport(item.id, {
      creator: item.editCreator,
      modelName: item.editName,
      sourceSlug: item.editSource,
    })
    items.value = items.value.filter((i) => i.id !== item.id)
  } catch { /* error shown */ } finally {
    item.confirming = false
  }
}

async function dismissItem(item) {
  item.dismissing = true
  try {
    await api.dismissImport(item.id)
    items.value = items.value.filter((i) => i.id !== item.id)
  } catch { /* error shown */ } finally {
    item.dismissing = false
  }
}

async function confirmAll() {
  for (const item of highConfidenceItems.value) {
    if (!item.confirming) await confirmItem(item)
  }
}

// Confidence helpers
function confidenceColor(c) {
  if (c >= 0.8) return 'text-forge-accent'
  if (c >= 0.5) return 'text-yellow-400'
  return 'text-forge-danger'
}

function confidenceLabel(c) {
  if (c >= 0.8) return 'High'
  if (c >= 0.5) return 'Medium'
  return 'Low'
}

function confidenceWidth(c) {
  return `${Math.round((c || 0) * 100)}%`
}

function confidenceBarColor(c) {
  if (c >= 0.8) return 'bg-forge-accent'
  if (c >= 0.5) return 'bg-yellow-400'
  return 'bg-forge-danger'
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
  return '📄'
}

onMounted(fetchQueue)
</script>

<template>
  <div>
    <!-- Header -->
    <div class="flex items-center justify-between mb-6">
      <div>
        <h1 class="text-2xl font-bold text-forge-text">Import Queue</h1>
        <p class="text-sm text-forge-text-muted mt-1">
          {{ pendingItems.length }} item{{ pendingItems.length !== 1 ? 's' : '' }} pending review
          <span v-if="autoSortedItems.length" class="ml-2 text-forge-accent">
            · {{ autoSortedItems.length }} auto-sorted
          </span>
        </p>
      </div>
      <div class="flex items-center gap-2">
        <button
          v-if="highConfidenceItems.length"
          @click="confirmAll"
          class="px-4 py-2 rounded-lg text-sm font-medium bg-forge-card border border-forge-accent text-forge-accent hover:bg-forge-accent/10 transition-colors"
        >
          ✓ Auto-confirm {{ highConfidenceItems.length }} high confidence
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

    <!-- Scan running indicator -->
    <div v-if="scanning" class="bg-forge-card border border-forge-accent/30 rounded-xl p-4 mb-6">
      <div class="flex items-center gap-3 text-sm">
        <div class="w-5 h-5 border-2 border-forge-accent border-t-transparent rounded-full animate-spin shrink-0"></div>
        <p class="text-forge-text font-medium">Scanning unsorted files…</p>
      </div>
    </div>

    <!-- Filter tabs -->
    <div class="flex gap-2 mb-4">
      <button
        v-for="s in ['', 'AwaitingReview', 'Pending', 'AutoSorted']"
        :key="s"
        @click="filterStatus = s; fetchQueue()"
        :class="[
          'px-3 py-1.5 rounded-lg text-xs font-medium transition-colors',
          filterStatus === s
            ? 'bg-forge-accent text-forge-bg'
            : 'bg-forge-card border border-forge-border text-forge-text-muted hover:text-forge-text',
        ]"
      >
        {{ s || 'All' }}
      </button>
    </div>

    <!-- Loading -->
    <div v-if="api.loading.value && !items.length" class="flex justify-center py-20">
      <div class="w-8 h-8 border-2 border-forge-accent border-t-transparent rounded-full animate-spin"></div>
    </div>

    <!-- Empty state -->
    <div v-else-if="!items.length" class="text-center py-20">
      <span class="text-5xl">📭</span>
      <p class="text-forge-text-muted mt-4">No files pending import</p>
      <p class="text-sm text-forge-text-muted mt-1">
        Drop files into the <code class="text-forge-accent">unsorted/</code> directory then click Scan Unsorted
      </p>
    </div>

    <template v-else>
      <!-- ── Pending / AwaitingReview items ───────────────── -->
      <div v-if="pendingItems.length" class="space-y-4 mb-8">
        <div
          v-for="item in pendingItems"
          :key="item.id"
          :class="[
            'bg-forge-card border rounded-xl p-5 transition-colors',
            (item.confidence ?? item.confidenceScore ?? 0) >= 0.8
              ? 'border-forge-accent/40'
              : 'border-forge-border',
          ]"
        >
          <div class="flex flex-col lg:flex-row lg:items-start gap-4">
            <!-- Left: Detected info -->
            <div class="flex-1 space-y-3 min-w-0">
              <!-- Path -->
              <div class="flex items-start gap-2">
                <span class="text-lg shrink-0">📁</span>
                <div class="min-w-0">
                  <p class="text-sm font-semibold text-forge-text break-all">
                    {{ item.detectedModelName || item.detectedFilename || item.originalPath }}
                  </p>
                  <p class="text-xs text-forge-text-muted font-mono mt-0.5 break-all">
                    {{ item.originalPath }}
                  </p>
                </div>
              </div>

              <!-- Confidence bar -->
              <div v-if="(item.confidence ?? item.confidenceScore) != null" class="space-y-1">
                <div class="flex items-center justify-between">
                  <span class="text-xs text-forge-text-muted">Detection confidence</span>
                  <span :class="['text-xs font-semibold', confidenceColor(item.confidence ?? item.confidenceScore)]">
                    {{ confidenceLabel(item.confidence ?? item.confidenceScore) }}
                    ({{ Math.round((item.confidence ?? item.confidenceScore) * 100) }}%)
                  </span>
                </div>
                <div class="h-1.5 bg-forge-bg rounded-full overflow-hidden">
                  <div
                    :class="[confidenceBarColor(item.confidence ?? item.confidenceScore), 'h-full rounded-full transition-all']"
                    :style="{ width: confidenceWidth(item.confidence ?? item.confidenceScore) }"
                  ></div>
                </div>
              </div>

              <!-- Editable fields -->
              <div class="grid grid-cols-1 sm:grid-cols-3 gap-3">
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
              <button
                @click="confirmItem(item)"
                :disabled="item.confirming || !item.editName || !item.editCreator"
                :class="[
                  'px-4 py-2 rounded-lg text-sm font-medium transition-colors',
                  (item.confirming || !item.editName || !item.editCreator)
                    ? 'bg-forge-accent/40 text-forge-bg cursor-not-allowed'
                    : 'bg-forge-accent hover:bg-forge-accent-hover text-forge-bg',
                ]"
              >
                {{ item.confirming ? '…' : '✓ Confirm' }}
              </button>
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

      <!-- ── Auto-sorted items (collapsible) ─────────────── -->
      <div v-if="autoSortedItems.length" class="mt-6">
        <button
          @click="showAutoSorted = !showAutoSorted"
          class="flex items-center gap-2 text-sm font-medium text-forge-text-muted hover:text-forge-text transition-colors mb-3"
        >
          <span>{{ showAutoSorted ? '▾' : '▸' }}</span>
          <span>Auto-sorted items ({{ autoSortedItems.length }})</span>
          <span class="text-xs text-forge-accent ml-1">confidence ≥ 80%</span>
        </button>

        <div v-if="showAutoSorted" class="space-y-3">
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
              <p class="text-xs text-forge-text-muted mt-0.5 flex gap-3">
                <span v-if="item.detectedCreator">Creator: {{ item.detectedCreator }}</span>
                <span v-if="item.confidence ?? item.confidenceScore">
                  {{ Math.round(((item.confidence ?? item.confidenceScore) || 0) * 100) }}% confidence
                </span>
              </p>
            </div>
            <span class="text-xs text-forge-accent font-medium shrink-0">Auto-sorted</span>
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

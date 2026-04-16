<!--
  ImportQueue.vue — Import queue for unsorted files
  Shows pending items with confidence, lets user confirm/edit metadata or reject.
  Can trigger unsorted/ scan and shows scan status.
-->
<script setup>
import { ref, onMounted, onBeforeUnmount } from 'vue'
import { useApi } from '../composables/useApi.js'
import SourceBadge from '../components/SourceBadge.vue'

const api = useApi()

const items = ref([])
const scanStatus = ref(null)
const scanning = ref(false)
const statusPollTimer = ref(null)
const filterStatus = ref('')

const sources = ['mmf', 'thangs', 'patreon', 'cults3d', 'thingiverse', 'manual']

async function fetchQueue() {
  try {
    const params = {}
    if (filterStatus.value) params.status = filterStatus.value
    const result = await api.getImportQueue(params)
    items.value = (result?.items || result || []).map((item) => ({
      ...item,
      editCreator: item.suggestedCreator || item.detectedCreator || '',
      editSource: item.suggestedSource || item.detectedSource || 'manual',
      editName: item.suggestedName || item.detectedModelName || item.detectedFilename || '',
      confirming: false,
      rejecting: false,
    }))
  } catch {
    items.value = []
  }
}

async function fetchScanStatus() {
  try {
    scanStatus.value = await api.getScanStatus()
  } catch { /* ignore */ }
}

async function triggerScan() {
  scanning.value = true
  try {
    await api.processUnsorted()
    // Start polling scan status
    startStatusPoll()
  } catch {
    scanning.value = false
  }
}

function startStatusPoll() {
  stopStatusPoll()
  statusPollTimer.value = setInterval(async () => {
    await fetchScanStatus()
    if (!scanStatus.value?.isRunning) {
      stopStatusPoll()
      scanning.value = false
      await fetchQueue()
    }
  }, 2000)
}

function stopStatusPoll() {
  if (statusPollTimer.value) {
    clearInterval(statusPollTimer.value)
    statusPollTimer.value = null
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
  } catch { /* error shown */ }
  finally { item.confirming = false }
}

async function rejectItem(item) {
  item.rejecting = true
  try {
    await api.rejectImport(item.id)
    items.value = items.value.filter((i) => i.id !== item.id)
  } catch { /* error shown */ }
  finally { item.rejecting = false }
}

async function confirmAll() {
  const highConfidence = items.value.filter((i) => (i.confidence ?? i.confidenceScore ?? 0) >= 0.8)
  for (const item of highConfidence) {
    await confirmItem(item)
  }
}

function confidenceColor(confidence) {
  if (confidence >= 0.8) return 'text-forge-accent'
  if (confidence >= 0.5) return 'text-source-mmf'
  return 'text-forge-danger'
}

function confidenceLabel(confidence) {
  if (confidence >= 0.8) return 'High'
  if (confidence >= 0.5) return 'Medium'
  return 'Low'
}

function confidenceWidth(confidence) {
  return `${Math.round((confidence || 0) * 100)}%`
}

function confidenceBarColor(confidence) {
  if (confidence >= 0.8) return 'bg-forge-accent'
  if (confidence >= 0.5) return 'bg-source-mmf'
  return 'bg-forge-danger'
}

onMounted(() => {
  fetchQueue()
  fetchScanStatus()
})

onBeforeUnmount(stopStatusPoll)
</script>

<template>
  <div>
    <div class="flex items-center justify-between mb-6">
      <div>
        <h1 class="text-2xl font-bold text-forge-text">Import Queue</h1>
        <p class="text-sm text-forge-text-muted mt-1">
          {{ items.length }} items pending review
        </p>
      </div>
      <div class="flex items-center gap-2">
        <!-- Confirm all high-confidence -->
        <button
          v-if="items.some((i) => (i.confidence ?? i.confidenceScore ?? 0) >= 0.8)"
          @click="confirmAll"
          class="px-4 py-2 rounded-lg text-sm font-medium bg-forge-card border border-forge-accent text-forge-accent hover:bg-forge-accent/10 transition-colors"
        >
          ✓ Auto-confirm high confidence
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
          {{ scanning ? '🔄 Scanning...' : '🔍 Scan for New Files' }}
        </button>
      </div>
    </div>

    <!-- Scan status -->
    <div
      v-if="scanStatus?.isRunning || scanning"
      class="bg-forge-card border border-forge-accent/30 rounded-xl p-4 mb-6"
    >
      <div class="flex items-center gap-3 text-sm">
        <div class="w-5 h-5 border-2 border-forge-accent border-t-transparent rounded-full animate-spin shrink-0"></div>
        <div class="flex-1">
          <p class="text-forge-text font-medium">{{ scanStatus?.status || 'Scanning unsorted files...' }}</p>
          <div v-if="scanStatus?.directoriesScanned || scanStatus?.modelsFound" class="flex gap-4 text-xs text-forge-text-muted mt-1">
            <span v-if="scanStatus?.directoriesScanned">{{ scanStatus.directoriesScanned }} dirs scanned</span>
            <span v-if="scanStatus?.modelsFound">{{ scanStatus.modelsFound }} models found</span>
            <span v-if="scanStatus?.newModels">{{ scanStatus.newModels }} new</span>
          </div>
        </div>
      </div>
    </div>

    <!-- Filter tabs -->
    <div class="flex gap-2 mb-4">
      <button
        v-for="status in ['', 'AwaitingReview', 'Pending', 'AutoSorted']"
        :key="status"
        @click="filterStatus = status; fetchQueue()"
        :class="[
          'px-3 py-1.5 rounded-lg text-xs font-medium transition-colors',
          filterStatus === status
            ? 'bg-forge-accent text-forge-bg'
            : 'bg-forge-card border border-forge-border text-forge-text-muted hover:text-forge-text',
        ]"
      >
        {{ status || 'All' }}
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
        Drop files into the <code class="text-forge-accent">unsorted/</code> directory and scan
      </p>
    </div>

    <!-- Import items -->
    <div v-else class="space-y-4">
      <div
        v-for="item in items"
        :key="item.id"
        class="bg-forge-card border border-forge-border rounded-xl p-5"
      >
        <div class="flex flex-col lg:flex-row lg:items-start gap-4">
          <!-- Left: Detected info -->
          <div class="flex-1 space-y-3">
            <div class="flex items-start gap-3">
              <span class="text-lg">📁</span>
              <div class="min-w-0">
                <p class="text-sm font-medium text-forge-text break-all">
                  {{ item.detectedFilename || item.originalPath || item.path }}
                </p>
                <p v-if="item.originalPath || item.path" class="text-xs text-forge-text-muted font-mono mt-0.5 break-all">
                  {{ item.originalPath || item.path }}
                </p>
              </div>
            </div>

            <!-- Confidence bar -->
            <div v-if="(item.confidence ?? item.confidenceScore) != null" class="space-y-1">
              <div class="flex items-center justify-between">
                <span class="text-xs text-forge-text-muted">Confidence:</span>
                <span :class="['text-xs font-medium', confidenceColor(item.confidence ?? item.confidenceScore)]">
                  {{ confidenceLabel(item.confidence ?? item.confidenceScore) }}
                  ({{ Math.round((item.confidence ?? item.confidenceScore) * 100) }}%)
                </span>
              </div>
              <div class="h-1.5 bg-forge-bg rounded-full overflow-hidden">
                <div
                  :class="[confidenceBarColor(item.confidence ?? item.confidenceScore), 'h-full rounded-full transition-all duration-300']"
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
                  class="w-full bg-forge-bg border border-forge-border rounded-lg px-3 py-1.5 text-sm text-forge-text focus:outline-none focus:border-forge-accent"
                />
              </div>
              <div>
                <label class="block text-xs text-forge-text-muted mb-1">Creator</label>
                <input
                  v-model="item.editCreator"
                  type="text"
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
          </div>

          <!-- Right: Action buttons -->
          <div class="flex lg:flex-col gap-2 shrink-0">
            <button
              @click="confirmItem(item)"
              :disabled="item.confirming || !item.editName || !item.editCreator"
              :class="[
                'px-4 py-2 rounded-lg text-sm font-medium transition-colors',
                item.confirming
                  ? 'bg-forge-accent/50 text-forge-bg cursor-not-allowed'
                  : 'bg-forge-accent hover:bg-forge-accent-hover text-forge-bg',
              ]"
            >
              {{ item.confirming ? '...' : '✓ Confirm' }}
            </button>
            <button
              @click="rejectItem(item)"
              :disabled="item.rejecting"
              class="px-4 py-2 rounded-lg text-sm font-medium bg-forge-bg border border-forge-border text-forge-text-muted hover:text-forge-danger hover:border-forge-danger transition-colors"
            >
              {{ item.rejecting ? '...' : '✕ Reject' }}
            </button>
          </div>
        </div>
      </div>
    </div>

    <!-- Error toast -->
    <div
      v-if="api.error.value"
      class="fixed bottom-4 right-4 bg-forge-danger/90 text-white px-4 py-2 rounded-lg shadow-lg text-sm"
    >
      {{ api.error.value }}
    </div>
  </div>
</template>

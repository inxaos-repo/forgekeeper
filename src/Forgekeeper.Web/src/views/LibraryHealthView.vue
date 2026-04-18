<!--
  LibraryHealthView.vue — Library Health & Reconciliation
  Shows download completion, missing files, orphan dirs, and bulk reorganize controls.
-->
<script setup>
import { ref, computed, onMounted, watch } from 'vue'
import { useApi } from '../composables/useApi.js'

const api = useApi()

// --- Health data ---
const health = ref(null)
const orphans = ref([])
const lastChecked = ref(null)
const loadingHealth = ref(false)
const loadingOrphans = ref(false)

// --- Action states ---
const scanning = ref(false)
const syncing = ref(false)
const verifying = ref(false)
const verifyResult = ref(null)
const actionMessage = ref('')
const actionError = ref('')

// --- Reorganize state ---
const reorgTemplate = ref('{source}/{creator}/{name}')
const previewResults = ref(null)
const previewLoading = ref(false)
const reorgLoading = ref(false)
const reorgResult = ref(null)
const showReorgConfirm = ref(false)

// --- Delete orphan state ---
const deletingOrphan = ref(null)

// --- Parse Filename state ---
const parseTemplate = ref('{creator} - {name}')
const parsePresets = [
  { label: '{creator} - {name}  (MMF default)', value: '{creator} - {name}' },
  { label: '{id} - {creator} - {name}  (with ID prefix)', value: '{id} - {creator} - {name}' },
  { label: '{creator}/{name}  (folder-based)', value: '{creator}/{name}' },
  { label: '{name}  (name only)', value: '{name}' },
  { label: 'Custom…', value: '__custom__' },
]
const parsePreviewData = ref(null)
const parsePreviewLoading = ref(false)
const parseApplyLoading = ref(false)
const parseApplyResult = ref(null)
const parseActionMessage = ref('')
const parseActionError = ref('')
const showApplyConfirm = ref(false)

const parseMatchPct = computed(() => {
  if (!parsePreviewData.value) return 0
  const { total } = parsePreviewData.value
  if (!total) return 0
  return Math.round((parsePreviewData.value.matched / total) * 100)
})

function onPresetChange(e) {
  const val = e.target.value
  if (val !== '__custom__') parseTemplate.value = val
}

async function runParsePreview() {
  parsePreviewLoading.value = true
  parsePreviewData.value = null
  parseActionError.value = ''
  parseActionMessage.value = ''
  try {
    parsePreviewData.value = await api.parseFilenamePreview(parseTemplate.value, null, 50)
  } catch (e) {
    parseActionError.value = `Preview failed: ${e.message}`
  } finally {
    parsePreviewLoading.value = false
  }
}

function confirmParseApply() {
  showApplyConfirm.value = true
}

async function applyParsedMetadata() {
  showApplyConfirm.value = false
  parseApplyLoading.value = true
  parseApplyResult.value = null
  parseActionError.value = ''
  parseActionMessage.value = ''
  try {
    parseApplyResult.value = await api.parseFilenameApply(parseTemplate.value, null)
    parseActionMessage.value = `Done! Updated ${parseApplyResult.value.updated?.toLocaleString()} models, skipped ${parseApplyResult.value.skipped?.toLocaleString()} (no match), failed ${parseApplyResult.value.failed ?? 0}.`
    await loadHealth()
  } catch (e) {
    parseActionError.value = `Apply failed: ${e.message}`
  } finally {
    parseApplyLoading.value = false
  }
}

// Auto-preview when template changes (debounced)
let parseDebounce = null
watch(parseTemplate, () => {
  clearTimeout(parseDebounce)
  parsePreviewData.value = null
  parseDebounce = setTimeout(runParsePreview, 600)
})

function formatSize(bytes) {
  if (!bytes) return '0 B'
  const units = ['B', 'KB', 'MB', 'GB', 'TB']
  let i = 0
  let size = bytes
  while (size >= 1024 && i < units.length - 1) { size /= 1024; i++ }
  return `${size.toFixed(i > 1 ? 2 : 0)} ${units[i]}`
}

function formatTime(dt) {
  if (!dt) return '—'
  return new Date(dt).toLocaleTimeString()
}

const completionPercent = computed(() => health.value?.downloadCompletionPercent ?? 0)
const modelsWithFiles = computed(() => health.value?.modelsWithFiles ?? 0)
const zeroFileModels = computed(() => health.value?.zeroFileModels ?? 0)
const totalModels = computed(() => health.value?.totalModels ?? 0)
const unknownCreatorCount = computed(() => health.value?.unknownCreatorModels ?? 0)

async function loadHealth() {
  loadingHealth.value = true
  actionError.value = ''
  try {
    health.value = await api.getLibraryHealth()
    lastChecked.value = new Date()
  } catch (e) {
    actionError.value = `Failed to load health data: ${e.message}`
  } finally {
    loadingHealth.value = false
  }
}

async function loadOrphans() {
  loadingOrphans.value = true
  try {
    orphans.value = await api.getUntrackedFiles() ?? []
  } catch {
    orphans.value = []
  } finally {
    loadingOrphans.value = false
  }
}

async function triggerRescan() {
  scanning.value = true
  actionMessage.value = ''
  actionError.value = ''
  try {
    await api.triggerScan()
    actionMessage.value = 'Full scan started. Library data will update as it progresses.'
  } catch (e) {
    actionError.value = `Scan failed: ${e.message}`
  } finally {
    scanning.value = false
  }
}

async function triggerSync() {
  syncing.value = true
  actionMessage.value = ''
  actionError.value = ''
  try {
    await api.triggerPluginSync('mmf')
    actionMessage.value = 'MMF sync started.'
  } catch (e) {
    actionError.value = `Sync failed: ${e.message}`
  } finally {
    syncing.value = false
  }
}

async function triggerVerify() {
  verifying.value = true
  verifyResult.value = null
  actionMessage.value = ''
  actionError.value = ''
  try {
    verifyResult.value = await api.verifyIntegrity()
    actionMessage.value = `Integrity check complete: ${verifyResult.value.verifiedModels}/${verifyResult.value.totalModels} dirs OK, ${verifyResult.value.missingModels} missing.`
  } catch (e) {
    actionError.value = `Verify failed: ${e.message}`
  } finally {
    verifying.value = false
  }
}

async function runPreview() {
  previewLoading.value = true
  previewResults.value = null
  actionError.value = ''
  try {
    previewResults.value = await api.reorganizePreview(reorgTemplate.value, null, 50)
  } catch (e) {
    actionError.value = `Preview failed: ${e.message}`
  } finally {
    previewLoading.value = false
  }
}

function confirmReorg() {
  showReorgConfirm.value = true
}

async function applyReorg() {
  showReorgConfirm.value = false
  reorgLoading.value = true
  reorgResult.value = null
  actionError.value = ''
  try {
    reorgResult.value = await api.reorganize(reorgTemplate.value, null)
    actionMessage.value = `Reorganize complete: ${reorgResult.value.moved} moved, ${reorgResult.value.skipped} skipped, ${reorgResult.value.failed} failed.`
    // Refresh after reorganize
    await loadHealth()
  } catch (e) {
    actionError.value = `Reorganize failed: ${e.message}`
  } finally {
    reorgLoading.value = false
  }
}

async function deleteOrphan(path) {
  if (!confirm(`Delete this directory and all its contents?\n\n${path}`)) return
  deletingOrphan.value = path
  try {
    // No dedicated endpoint yet — show a message
    actionMessage.value = `Delete of orphan dirs is not yet implemented server-side. Path: ${path}`
  } finally {
    deletingOrphan.value = null
  }
}

onMounted(() => {
  loadHealth()
  loadOrphans()
})
</script>

<template>
  <div class="min-h-screen bg-forge-bg text-forge-text">
    <div class="max-w-5xl mx-auto px-4 py-8 space-y-8">

      <!-- Header -->
      <div class="flex items-center justify-between">
        <div>
          <h1 class="text-2xl font-bold text-forge-text">🏥 Library Health</h1>
          <p v-if="lastChecked" class="text-sm text-forge-text-muted mt-1">
            Last checked: {{ formatTime(lastChecked) }}
          </p>
        </div>
        <button
          @click="loadHealth(); loadOrphans()"
          :disabled="loadingHealth"
          class="px-4 py-2 bg-forge-card border border-forge-border rounded-lg text-sm text-forge-text hover:bg-forge-surface transition-colors disabled:opacity-50"
        >
          {{ loadingHealth ? '⏳ Refreshing…' : '🔄 Refresh' }}
        </button>
      </div>

      <!-- Action messages -->
      <div v-if="actionMessage" class="px-4 py-3 bg-green-900/30 border border-green-700 rounded-lg text-green-300 text-sm">
        ✅ {{ actionMessage }}
      </div>
      <div v-if="actionError" class="px-4 py-3 bg-red-900/30 border border-red-700 rounded-lg text-red-300 text-sm">
        ❌ {{ actionError }}
      </div>

      <!-- Stats Row -->
      <div v-if="health" class="grid grid-cols-2 md:grid-cols-4 gap-4">
        <!-- Total Models -->
        <div class="bg-forge-card border border-forge-border rounded-xl p-4 space-y-2">
          <div class="text-xs text-forge-text-muted uppercase tracking-wide">Total Models</div>
          <div class="text-3xl font-bold text-forge-text">{{ totalModels.toLocaleString() }}</div>
          <!-- Download completion gauge -->
          <div class="space-y-1">
            <div class="flex justify-between text-xs text-forge-text-muted">
              <span>Downloaded</span>
              <span>{{ completionPercent }}%</span>
            </div>
            <div class="h-1.5 bg-forge-bg rounded-full overflow-hidden">
              <div
                class="h-full bg-forge-accent rounded-full transition-all"
                :style="{ width: completionPercent + '%' }"
              ></div>
            </div>
          </div>
        </div>

        <!-- Models with Files -->
        <div class="bg-forge-card border border-forge-border rounded-xl p-4 space-y-1">
          <div class="text-xs text-forge-text-muted uppercase tracking-wide">With Files</div>
          <div class="text-3xl font-bold text-green-400">{{ modelsWithFiles.toLocaleString() }}</div>
          <div class="text-xs text-forge-text-muted">✅ Downloaded</div>
        </div>

        <!-- Missing Files -->
        <div class="bg-forge-card border border-forge-border rounded-xl p-4 space-y-1">
          <div class="text-xs text-forge-text-muted uppercase tracking-wide">Missing Files</div>
          <div class="text-3xl font-bold" :class="zeroFileModels > 0 ? 'text-yellow-400' : 'text-forge-text'">
            {{ zeroFileModels.toLocaleString() }}
          </div>
          <div class="text-xs text-forge-text-muted">⚠️ Not downloaded</div>
        </div>

        <!-- Orphan Dirs -->
        <div class="bg-forge-card border border-forge-border rounded-xl p-4 space-y-1">
          <div class="text-xs text-forge-text-muted uppercase tracking-wide">Orphan Dirs</div>
          <div class="text-3xl font-bold" :class="orphans.length > 0 ? 'text-orange-400' : 'text-forge-text'">
            {{ orphans.length }}
          </div>
          <div class="text-xs text-forge-text-muted">🗂️ On disk, not in DB</div>
        </div>
      </div>

      <!-- Loading skeleton -->
      <div v-else-if="loadingHealth" class="grid grid-cols-2 md:grid-cols-4 gap-4">
        <div v-for="i in 4" :key="i" class="bg-forge-card border border-forge-border rounded-xl p-4 h-28 animate-pulse"></div>
      </div>

      <!-- Download Status Section -->
      <div v-if="health" class="bg-forge-card border border-forge-border rounded-xl p-6 space-y-4">
        <h2 class="text-lg font-semibold text-forge-text">📥 Download Status</h2>

        <!-- Progress bar -->
        <div class="space-y-2">
          <div class="flex justify-between text-sm text-forge-text-muted">
            <span>{{ modelsWithFiles.toLocaleString() }} of {{ totalModels.toLocaleString() }} models downloaded</span>
            <span class="font-semibold text-forge-text">{{ completionPercent }}%</span>
          </div>
          <div class="h-3 bg-forge-bg rounded-full overflow-hidden">
            <div
              class="h-full bg-forge-accent rounded-full transition-all duration-500"
              :style="{ width: completionPercent + '%' }"
            ></div>
          </div>
        </div>

        <!-- Unknown creator callout -->
        <div class="flex items-start gap-3 px-4 py-3 bg-forge-bg rounded-lg border border-forge-border">
          <span class="text-2xl">👤</span>
          <div class="flex-1 min-w-0">
            <div class="text-sm font-medium text-forge-text">
              {{ unknownCreatorCount.toLocaleString() }} models with unknown creator
            </div>
            <div class="text-xs text-forge-text-muted mt-0.5">
              These models were imported before creator metadata was available. Use "Re-sync" to fetch updated metadata from MMF.
            </div>
          </div>
          <button
            @click="triggerSync"
            :disabled="syncing"
            class="shrink-0 px-3 py-1.5 bg-forge-accent text-forge-bg text-xs font-semibold rounded-lg hover:opacity-90 disabled:opacity-50 transition-opacity"
          >
            {{ syncing ? '⏳ Syncing…' : '🔄 Re-sync' }}
          </button>
        </div>

        <!-- Library size -->
        <div class="text-sm text-forge-text-muted">
          Total stored: <span class="text-forge-text font-medium">{{ formatSize(health.totalSizeBytes) }}</span>
        </div>
      </div>

      <!-- Reconciliation Actions -->
      <div class="bg-forge-card border border-forge-border rounded-xl p-6 space-y-4">
        <h2 class="text-lg font-semibold text-forge-text">🔧 Reconciliation Actions</h2>
        <div class="grid grid-cols-1 sm:grid-cols-3 gap-3">

          <button
            @click="triggerRescan"
            :disabled="scanning"
            class="flex flex-col items-center gap-2 px-4 py-4 bg-forge-bg border border-forge-border rounded-lg hover:border-forge-accent hover:text-forge-accent transition-colors disabled:opacity-50 text-center"
          >
            <span class="text-2xl">🔍</span>
            <span class="text-sm font-medium">{{ scanning ? 'Starting…' : 'Re-scan Library' }}</span>
            <span class="text-xs text-forge-text-muted">Full filesystem scan</span>
          </button>

          <button
            @click="triggerVerify"
            :disabled="verifying"
            class="flex flex-col items-center gap-2 px-4 py-4 bg-forge-bg border border-forge-border rounded-lg hover:border-forge-accent hover:text-forge-accent transition-colors disabled:opacity-50 text-center"
          >
            <span class="text-2xl">🛡️</span>
            <span class="text-sm font-medium">{{ verifying ? 'Checking…' : 'Check Disk Integrity' }}</span>
            <span class="text-xs text-forge-text-muted">Verify all files exist on disk</span>
          </button>

          <button
            @click="triggerSync"
            :disabled="syncing"
            class="flex flex-col items-center gap-2 px-4 py-4 bg-forge-bg border border-forge-border rounded-lg hover:border-forge-accent hover:text-forge-accent transition-colors disabled:opacity-50 text-center"
          >
            <span class="text-2xl">☁️</span>
            <span class="text-sm font-medium">{{ syncing ? 'Syncing…' : 'Sync from MMF' }}</span>
            <span class="text-xs text-forge-text-muted">Fetch latest metadata</span>
          </button>

        </div>

        <!-- Verify results -->
        <div v-if="verifyResult" class="mt-2 px-4 py-3 bg-forge-bg rounded-lg border border-forge-border text-sm space-y-1">
          <div class="font-medium text-forge-text">Integrity Check Results</div>
          <div class="text-forge-text-muted grid grid-cols-2 gap-x-4 gap-y-1 mt-2">
            <span>Models checked:</span><span class="text-forge-text">{{ verifyResult.totalModels?.toLocaleString() }}</span>
            <span>Dirs OK:</span><span class="text-green-400">{{ verifyResult.verifiedModels?.toLocaleString() }}</span>
            <span>Dirs missing:</span><span :class="verifyResult.missingModels > 0 ? 'text-red-400' : 'text-forge-text'">{{ verifyResult.missingModels?.toLocaleString() }}</span>
            <span>Files checked:</span><span class="text-forge-text">{{ verifyResult.totalFiles?.toLocaleString() }}</span>
            <span>Files OK:</span><span class="text-green-400">{{ verifyResult.verifiedFiles?.toLocaleString() }}</span>
            <span>Files missing:</span><span :class="verifyResult.missingFiles > 0 ? 'text-red-400' : 'text-forge-text'">{{ verifyResult.missingFiles?.toLocaleString() }}</span>
          </div>
        </div>
      </div>

      <!-- Bulk Reorganize -->
      <div class="bg-forge-card border border-forge-border rounded-xl p-6 space-y-4">
        <h2 class="text-lg font-semibold text-forge-text">📁 Bulk Reorganize</h2>
        <p class="text-sm text-forge-text-muted">
          Restructure your library directories using a template. Variables:
          <code class="text-forge-accent">{source}</code>,
          <code class="text-forge-accent">{creator}</code>,
          <code class="text-forge-accent">{name}</code>,
          <code class="text-forge-accent">{category}</code>,
          <code class="text-forge-accent">{gameSystem}</code>,
          <code class="text-forge-accent">{externalId}</code>
        </p>

        <!-- Template input -->
        <div class="flex gap-3">
          <input
            v-model="reorgTemplate"
            type="text"
            placeholder="{source}/{creator}/{name}"
            class="flex-1 bg-forge-bg border border-forge-border rounded-lg px-4 py-2 text-sm text-forge-text font-mono placeholder-forge-text-muted focus:outline-none focus:border-forge-accent focus:ring-1 focus:ring-forge-accent"
          />
          <button
            @click="runPreview"
            :disabled="previewLoading || !reorgTemplate"
            class="px-4 py-2 bg-forge-surface border border-forge-border rounded-lg text-sm font-medium text-forge-text hover:border-forge-accent hover:text-forge-accent transition-colors disabled:opacity-50"
          >
            {{ previewLoading ? '⏳ Loading…' : '👁️ Preview' }}
          </button>
        </div>

        <!-- Preview results -->
        <div v-if="previewResults" class="space-y-3">
          <div class="flex items-center justify-between">
            <div class="text-sm text-forge-text-muted">
              Showing {{ previewResults.items?.length }} sample models —
              <span class="text-yellow-400 font-medium">{{ previewResults.wouldMove }}</span> would move
              out of <span class="text-forge-text font-medium">{{ totalModels.toLocaleString() }}</span> total
            </div>
            <button
              v-if="previewResults.wouldMove > 0"
              @click="confirmReorg"
              :disabled="reorgLoading"
              class="px-4 py-2 bg-red-700 hover:bg-red-600 text-white text-sm font-semibold rounded-lg disabled:opacity-50 transition-colors"
            >
              {{ reorgLoading ? '⏳ Moving…' : `⚠️ Apply to All ${totalModels.toLocaleString()}` }}
            </button>
          </div>

          <div class="space-y-1 max-h-72 overflow-y-auto rounded-lg border border-forge-border">
            <div
              v-for="item in previewResults.items"
              :key="item.id"
              class="px-3 py-2 border-b border-forge-border last:border-0"
              :class="item.wouldMove ? 'bg-forge-bg' : 'bg-forge-surface/50'"
            >
              <div class="flex items-start gap-2 text-xs font-mono">
                <span v-if="item.wouldMove" class="text-yellow-400 shrink-0 mt-0.5">→</span>
                <span v-else class="text-forge-text-muted shrink-0 mt-0.5">=</span>
                <div class="min-w-0">
                  <div v-if="item.wouldMove" class="text-forge-text-muted truncate">{{ item.currentPath }}</div>
                  <div class="text-forge-text truncate">{{ item.newPath }}</div>
                </div>
              </div>
            </div>
          </div>
        </div>

        <!-- Apply result -->
        <div v-if="reorgResult" class="px-4 py-3 bg-forge-bg rounded-lg border border-forge-border text-sm space-y-1">
          <div class="font-medium text-forge-text">Reorganize Complete</div>
          <div class="text-forge-text-muted grid grid-cols-2 gap-x-4 gap-y-1 mt-2">
            <span>Moved:</span><span class="text-green-400">{{ reorgResult.moved?.toLocaleString() }}</span>
            <span>Skipped:</span><span class="text-forge-text">{{ reorgResult.skipped?.toLocaleString() }}</span>
            <span>Failed:</span><span :class="reorgResult.failed > 0 ? 'text-red-400' : 'text-forge-text'">{{ reorgResult.failed?.toLocaleString() }}</span>
          </div>
          <div v-if="reorgResult.errors?.length" class="mt-2 space-y-0.5">
            <div v-for="err in reorgResult.errors" :key="err" class="text-red-400 text-xs">{{ err }}</div>
          </div>
        </div>
      </div>

      <!-- Parse Filename Section -->
      <div class="bg-forge-card border border-forge-border rounded-xl p-6 space-y-4">
        <div>
          <h2 class="text-lg font-semibold text-forge-text">🔍 Guess from Filename</h2>
          <p class="text-sm text-forge-text-muted mt-1">
            Extract metadata from directory names using a template — like Mp3tag's &quot;Filename → Tag&quot; feature.
            Only updates the database; files are not moved.
          </p>
        </div>

        <!-- Parse action messages -->
        <div v-if="parseActionMessage" class="px-4 py-3 bg-green-900/30 border border-green-700 rounded-lg text-green-300 text-sm">
          ✅ {{ parseActionMessage }}
        </div>
        <div v-if="parseActionError" class="px-4 py-3 bg-red-900/30 border border-red-700 rounded-lg text-red-300 text-sm">
          ❌ {{ parseActionError }}
        </div>

        <!-- Template Builder -->
        <div class="space-y-2">
          <label class="text-xs font-semibold uppercase tracking-wide text-forge-text-muted">Template</label>
          <div class="flex gap-2">
            <!-- Preset picker -->
            <select
              @change="onPresetChange"
              class="bg-forge-bg border border-forge-border rounded-lg px-3 py-2 text-sm text-forge-text focus:outline-none focus:border-forge-accent shrink-0"
            >
              <option v-for="p in parsePresets" :key="p.value" :value="p.value">{{ p.label }}</option>
            </select>
            <!-- Free-form input -->
            <input
              v-model="parseTemplate"
              type="text"
              placeholder="{creator} - {name}"
              class="flex-1 bg-forge-bg border border-forge-border rounded-lg px-4 py-2 text-sm text-forge-text font-mono placeholder-forge-text-muted focus:outline-none focus:border-forge-accent focus:ring-1 focus:ring-forge-accent"
            />
            <button
              @click="runParsePreview"
              :disabled="parsePreviewLoading || !parseTemplate"
              class="px-4 py-2 bg-forge-surface border border-forge-border rounded-lg text-sm font-medium text-forge-text hover:border-forge-accent hover:text-forge-accent transition-colors disabled:opacity-50 shrink-0"
            >
              {{ parsePreviewLoading ? '⏳ Loading…' : '👁️ Preview' }}
            </button>
          </div>
          <!-- Variable reference -->
          <div class="text-xs text-forge-text-muted">
            Variables:
            <code class="text-forge-accent">{name}</code>,
            <code class="text-forge-accent">{creator}</code>,
            <code class="text-forge-accent">{id}</code>,
            <code class="text-forge-accent">{category}</code>,
            <code class="text-forge-accent">{gameSystem}</code>,
            <code class="text-forge-accent">{scale}</code>,
            <code class="text-forge-accent">{source}</code>,
            <code class="text-forge-accent">{ignore}</code>
            <span class="ml-1">(matches but discards)</span>
          </div>
        </div>

        <!-- Live Preview -->
        <div v-if="parsePreviewLoading" class="text-sm text-forge-text-muted animate-pulse">Previewing…</div>

        <div v-else-if="parsePreviewData" class="space-y-3">
          <!-- Match summary -->
          <div class="flex items-center justify-between flex-wrap gap-2">
            <div class="text-sm">
              <span
                class="font-semibold"
                :class="parseMatchPct >= 80 ? 'text-green-400' : parseMatchPct >= 40 ? 'text-yellow-400' : 'text-red-400'"
              >
                {{ parseMatchPct }}% matched
              </span>
              <span class="text-forge-text-muted ml-1">
                ({{ parsePreviewData.matched?.toLocaleString() }} of {{ parsePreviewData.total?.toLocaleString() }} preview rows)
              </span>
            </div>
            <button
              v-if="parsePreviewData.matched > 0"
              @click="confirmParseApply"
              :disabled="parseApplyLoading"
              class="px-4 py-2 bg-forge-accent text-forge-bg text-sm font-semibold rounded-lg hover:opacity-90 disabled:opacity-50 transition-opacity shrink-0"
            >
              {{ parseApplyLoading ? '⏳ Applying…' : '✅ Apply to All Matched' }}
            </button>
          </div>

          <!-- Preview table -->
          <div class="overflow-x-auto rounded-lg border border-forge-border">
            <table class="w-full text-xs">
              <thead>
                <tr class="bg-forge-surface border-b border-forge-border">
                  <th class="px-3 py-2 text-left text-forge-text-muted font-medium">Directory Name</th>
                  <th class="px-3 py-2 text-left text-forge-text-muted font-medium">Matched?</th>
                  <th class="px-3 py-2 text-left text-forge-text-muted font-medium">Extracted Creator</th>
                  <th class="px-3 py-2 text-left text-forge-text-muted font-medium">Extracted Name</th>
                  <th class="px-3 py-2 text-left text-forge-text-muted font-medium">Extracted Category</th>
                </tr>
              </thead>
              <tbody>
                <tr
                  v-for="item in parsePreviewData.items"
                  :key="item.id"
                  class="border-b border-forge-border last:border-0 transition-colors"
                  :class="item.success ? 'bg-green-950/20 hover:bg-green-950/30' : 'bg-red-950/20 hover:bg-red-950/30'"
                >
                  <td class="px-3 py-2 font-mono text-forge-text truncate max-w-xs">{{ item.directoryName }}</td>
                  <td class="px-3 py-2">
                    <span v-if="item.success" class="text-green-400">✅</span>
                    <span v-else class="text-red-400">✗</span>
                  </td>
                  <td class="px-3 py-2 text-forge-text">
                    <span v-if="item.changes?.creator" class="text-green-300">{{ item.changes.creator }}</span>
                    <span v-else class="text-forge-text-muted">—</span>
                  </td>
                  <td class="px-3 py-2 text-forge-text">
                    <span v-if="item.changes?.name" class="text-green-300">{{ item.changes.name }}</span>
                    <span v-else class="text-forge-text-muted">—</span>
                  </td>
                  <td class="px-3 py-2 text-forge-text">
                    <span v-if="item.changes?.category" class="text-green-300">{{ item.changes.category }}</span>
                    <span v-else class="text-forge-text-muted">—</span>
                  </td>
                </tr>
              </tbody>
            </table>
          </div>
          <p class="text-xs text-forge-text-muted">Showing first {{ parsePreviewData.items?.length }} models. Apply runs against all {{ totalModels.toLocaleString() }} models.</p>
        </div>

        <!-- Apply result -->
        <div v-if="parseApplyResult" class="px-4 py-3 bg-forge-bg rounded-lg border border-forge-border text-sm space-y-1">
          <div class="font-medium text-forge-text">Apply Complete</div>
          <div class="text-forge-text-muted grid grid-cols-2 gap-x-4 gap-y-1 mt-2">
            <span>Updated:</span><span class="text-green-400">{{ parseApplyResult.updated?.toLocaleString() }}</span>
            <span>Skipped (no match):</span><span class="text-forge-text">{{ parseApplyResult.skipped?.toLocaleString() }}</span>
            <span>Failed:</span><span :class="parseApplyResult.failed > 0 ? 'text-red-400' : 'text-forge-text'">{{ parseApplyResult.failed ?? 0 }}</span>
          </div>
        </div>
      </div>

      <!-- Orphan Files Section -->
      <div class="bg-forge-card border border-forge-border rounded-xl p-6 space-y-4">
        <div class="flex items-center justify-between">
          <h2 class="text-lg font-semibold text-forge-text">🗂️ Orphan Directories</h2>
          <button
            @click="loadOrphans"
            :disabled="loadingOrphans"
            class="text-xs px-3 py-1.5 bg-forge-bg border border-forge-border rounded-lg text-forge-text-muted hover:text-forge-text transition-colors disabled:opacity-50"
          >
            {{ loadingOrphans ? '⏳' : '🔄 Refresh' }}
          </button>
        </div>
        <p class="text-sm text-forge-text-muted">
          Directories found on disk that have no matching model in the database.
        </p>

        <div v-if="loadingOrphans" class="text-sm text-forge-text-muted">Loading…</div>

        <div v-else-if="!orphans.length" class="text-sm text-green-400">
          ✅ No orphan directories found.
        </div>

        <div v-else class="space-y-2">
          <div
            v-for="orphan in orphans"
            :key="typeof orphan === 'string' ? orphan : orphan.path ?? orphan"
            class="flex items-center gap-3 px-4 py-3 bg-forge-bg border border-forge-border rounded-lg"
          >
            <span class="text-xl shrink-0">📁</span>
            <span class="flex-1 text-sm font-mono text-forge-text-muted truncate">
              {{ typeof orphan === 'string' ? orphan : (orphan.path ?? JSON.stringify(orphan)) }}
            </span>
            <div class="flex gap-2 shrink-0">
              <button
                class="px-3 py-1 bg-forge-accent text-forge-bg text-xs font-semibold rounded-lg hover:opacity-90 transition-opacity"
                title="Import this directory (opens import wizard)"
                @click="actionMessage = 'Import wizard for orphan dirs coming soon.'"
              >
                Import
              </button>
              <button
                class="px-3 py-1 bg-red-800 hover:bg-red-700 text-white text-xs font-semibold rounded-lg transition-colors"
                :disabled="deletingOrphan === (typeof orphan === 'string' ? orphan : orphan.path)"
                @click="deleteOrphan(typeof orphan === 'string' ? orphan : orphan.path)"
              >
                Delete
              </button>
            </div>
          </div>
        </div>
      </div>

    </div>

    <!-- Parse-filename apply confirmation modal -->
    <Transition name="fade">
      <div
        v-if="showApplyConfirm"
        class="fixed inset-0 z-50 flex items-center justify-center bg-black/60 p-4"
        @click.self="showApplyConfirm = false"
      >
        <div class="bg-forge-surface border border-forge-border rounded-2xl p-6 max-w-md w-full space-y-4 shadow-2xl">
          <h3 class="text-lg font-bold text-forge-text">⚠️ Confirm: Apply Parsed Metadata</h3>
          <p class="text-sm text-forge-text-muted">
            This will update metadata (name, creator, category, gameSystem, scale) for all models whose
            directory name matches the template:
          </p>
          <code class="block px-3 py-2 bg-forge-bg rounded-lg text-sm text-forge-accent font-mono">{{ parseTemplate }}</code>
          <p class="text-xs text-yellow-400">
            ⚠️ Files on disk are not moved. Only database metadata is changed.
            This updates <strong>all {{ totalModels.toLocaleString() }}</strong> models, not just the preview sample.
          </p>
          <div class="flex gap-3 justify-end">
            <button
              @click="showApplyConfirm = false"
              class="px-4 py-2 bg-forge-card border border-forge-border rounded-lg text-sm text-forge-text hover:bg-forge-bg transition-colors"
            >
              Cancel
            </button>
            <button
              @click="applyParsedMetadata"
              class="px-4 py-2 bg-forge-accent hover:opacity-90 text-forge-bg text-sm font-semibold rounded-lg transition-opacity"
            >
              Yes, Apply Metadata
            </button>
          </div>
        </div>
      </div>
    </Transition>

    <!-- Reorganize confirmation modal -->
    <Transition name="fade">
      <div
        v-if="showReorgConfirm"
        class="fixed inset-0 z-50 flex items-center justify-center bg-black/60 p-4"
        @click.self="showReorgConfirm = false"
      >
        <div class="bg-forge-surface border border-forge-border rounded-2xl p-6 max-w-md w-full space-y-4 shadow-2xl">
          <h3 class="text-lg font-bold text-forge-text">⚠️ Confirm Reorganize</h3>
          <p class="text-sm text-forge-text-muted">
            This will move <strong class="text-forge-text">{{ totalModels.toLocaleString() }}</strong> model directories
            on disk according to the template:
          </p>
          <code class="block px-3 py-2 bg-forge-bg rounded-lg text-sm text-forge-accent font-mono">{{ reorgTemplate }}</code>
          <p class="text-xs text-yellow-400">
            ⚠️ This operation cannot be easily undone. Make sure you have a backup before proceeding.
          </p>
          <div class="flex gap-3 justify-end">
            <button
              @click="showReorgConfirm = false"
              class="px-4 py-2 bg-forge-card border border-forge-border rounded-lg text-sm text-forge-text hover:bg-forge-bg transition-colors"
            >
              Cancel
            </button>
            <button
              @click="applyReorg"
              class="px-4 py-2 bg-red-700 hover:bg-red-600 text-white text-sm font-semibold rounded-lg transition-colors"
            >
              Yes, Move Everything
            </button>
          </div>
        </div>
      </div>
    </Transition>

  </div>
</template>

<style scoped>
.fade-enter-active,
.fade-leave-active {
  transition: opacity 0.15s ease;
}
.fade-enter-from,
.fade-leave-to {
  opacity: 0;
}
</style>

<!--
  WatchDirectoriesPanel.vue — Watch directory management + auto-import status
  Shows configured watch dirs (read-only from appsettings), scan-now buttons,
  auto-import toggle display, and interval config display.

  Note: Watch directories are stored in appsettings.json / env vars (Import:WatchDirectories).
  Runtime add/remove is not yet supported via API. The "Scan Now" and process-all buttons
  ARE fully functional.
-->
<script setup>
import { ref, onMounted } from 'vue'
import { useApi } from '../composables/useApi.js'

const emit = defineEmits(['scan-complete'])
const api = useApi()

const watchData = ref(null)
const loadError = ref(null)
const scanningPath = ref(null)   // path currently scanning
const scanningAll = ref(false)
const scanResult = ref(null)     // brief feedback message

async function loadWatchDirectories() {
  loadError.value = null
  try {
    watchData.value = await api.getWatchDirectories()
  } catch (e) {
    loadError.value = e.message
    watchData.value = null
  }
}

async function scanDirectory(path) {
  scanningPath.value = path
  scanResult.value = null
  try {
    const result = await api.scanDirectory(path)
    scanResult.value = `✓ Scan complete${result?.newItems != null ? ` — ${result.newItems} new item(s)` : ''}`
    emit('scan-complete')
    setTimeout(() => { scanResult.value = null }, 4000)
  } catch (e) {
    scanResult.value = `✗ Scan failed: ${e.message}`
  } finally {
    scanningPath.value = null
  }
}

async function scanAll() {
  scanningAll.value = true
  scanResult.value = null
  try {
    const result = await api.processAllImports()
    scanResult.value = `✓ All directories scanned${result?.newItems != null ? ` — ${result.newItems} new item(s)` : ''}`
    emit('scan-complete')
    setTimeout(() => { scanResult.value = null }, 5000)
  } catch (e) {
    scanResult.value = `✗ Scan failed: ${e.message}`
  } finally {
    scanningAll.value = false
  }
}

function formatPath(p) {
  if (!p) return ''
  // Truncate long paths nicely
  if (p.length <= 50) return p
  const parts = p.replace(/\\/g, '/').split('/')
  if (parts.length <= 3) return p
  return '…/' + parts.slice(-2).join('/')
}

function formatInterval(minutes) {
  if (!minutes) return '—'
  if (minutes < 60) return `${minutes}m`
  return `${minutes / 60}h`
}

function formatTimestamp(ts) {
  if (!ts) return 'Never'
  const d = new Date(ts)
  return d.toLocaleString()
}

onMounted(loadWatchDirectories)
</script>

<template>
  <div class="bg-forge-card border border-forge-border rounded-xl overflow-hidden mb-6">
    <!-- Panel header -->
    <div class="flex items-center justify-between px-5 py-3 border-b border-forge-border bg-forge-surface/50">
      <div class="flex items-center gap-2">
        <span class="text-forge-accent font-bold text-sm uppercase tracking-wide">📂 Watch Directories</span>
      </div>
      <div class="flex items-center gap-2">
        <span
          v-if="scanResult"
          :class="['text-xs font-medium transition-opacity', scanResult.startsWith('✓') ? 'text-forge-accent' : 'text-forge-danger']"
        >
          {{ scanResult }}
        </span>
        <button
          @click="scanAll"
          :disabled="scanningAll || api.loading.value"
          :class="[
            'px-3 py-1.5 rounded-lg text-xs font-medium transition-colors',
            (scanningAll || api.loading.value)
              ? 'bg-forge-bg text-forge-text-muted cursor-not-allowed'
              : 'bg-forge-accent hover:bg-forge-accent-hover text-forge-bg',
          ]"
        >
          {{ scanningAll ? '🔄 Scanning…' : '🔍 Scan All' }}
        </button>
        <button
          @click="loadWatchDirectories"
          class="px-2 py-1.5 rounded-lg text-xs text-forge-text-muted hover:text-forge-text border border-forge-border hover:border-forge-accent transition-colors"
          title="Refresh"
        >
          ↻
        </button>
      </div>
    </div>

    <!-- Loading -->
    <div v-if="api.loading.value && !watchData" class="flex justify-center py-8">
      <div class="w-6 h-6 border-2 border-forge-accent border-t-transparent rounded-full animate-spin"></div>
    </div>

    <!-- Error -->
    <div v-else-if="loadError" class="px-5 py-4 text-sm text-forge-danger">
      ⚠ Could not load watch directories: {{ loadError }}
    </div>

    <div v-else-if="watchData" class="p-5 space-y-5">
      <!-- Auto-import status row -->
      <div class="flex flex-wrap items-center gap-4 p-3 bg-forge-bg rounded-lg border border-forge-border">
        <div class="flex items-center gap-2">
          <span :class="['w-2 h-2 rounded-full', watchData.autoImportEnabled ? 'bg-forge-accent animate-pulse' : 'bg-forge-text-muted']"></span>
          <span class="text-sm font-medium text-forge-text">
            Auto-import {{ watchData.autoImportEnabled ? 'enabled' : 'disabled' }}
          </span>
        </div>
        <div v-if="watchData.autoImportEnabled" class="flex items-center gap-3 text-xs text-forge-text-muted">
          <span>Every {{ formatInterval(watchData.intervalMinutes) }}</span>
          <span v-if="watchData.lastScanAt">Last scan: {{ formatTimestamp(watchData.lastScanAt) }}</span>
        </div>
        <div class="ml-auto">
          <p class="text-xs text-forge-text-muted">
            ⚙ Configured in <code class="text-forge-accent">appsettings.json</code> or env vars
          </p>
        </div>
      </div>

      <!-- Watch directory list -->
      <div>
        <p class="text-xs font-medium text-forge-text-muted uppercase tracking-wide mb-2">Configured Directories</p>
        <div
          v-if="!watchData.watchDirectories?.length && !watchData.unsortedDirectories?.length"
          class="text-sm text-forge-text-muted py-2"
        >
          No watch directories configured. Add paths to <code class="text-forge-accent">Import:WatchDirectories</code> in appsettings.json.
        </div>
        <div class="space-y-2">
          <!-- Main watch dirs -->
          <div
            v-for="dir in (watchData.watchDirectories || [])"
            :key="dir.path || dir"
            class="flex items-center gap-3 px-3 py-2.5 rounded-lg border border-forge-border bg-forge-bg group"
          >
            <!-- Exists indicator -->
            <span
              :class="['w-2 h-2 rounded-full shrink-0', dir.exists !== false ? 'bg-forge-accent' : 'bg-forge-danger']"
              :title="dir.exists !== false ? 'Directory exists' : 'Directory not found'"
            ></span>
            <!-- Path -->
            <span class="flex-1 min-w-0">
              <span class="text-sm font-mono text-forge-text truncate block" :title="dir.path || dir">
                {{ dir.path || dir }}
              </span>
              <span v-if="dir.itemCount != null" class="text-xs text-forge-text-muted">
                {{ dir.itemCount }} item{{ dir.itemCount !== 1 ? 's' : '' }}
              </span>
            </span>
            <!-- Type badge -->
            <span class="shrink-0 text-xs px-1.5 py-0.5 rounded bg-forge-surface text-forge-text-muted border border-forge-border">
              watch
            </span>
            <!-- Actions -->
            <div class="flex items-center gap-1 opacity-0 group-hover:opacity-100 transition-opacity">
              <button
                @click="scanDirectory(dir.path || dir)"
                :disabled="scanningPath === (dir.path || dir)"
                :class="[
                  'px-2.5 py-1 rounded text-xs font-medium transition-colors',
                  scanningPath === (dir.path || dir)
                    ? 'bg-forge-card text-forge-text-muted cursor-not-allowed'
                    : 'bg-forge-card border border-forge-border text-forge-text hover:border-forge-accent hover:text-forge-accent',
                ]"
              >
                {{ scanningPath === (dir.path || dir) ? '🔄' : '🔍 Scan' }}
              </button>
            </div>
          </div>

          <!-- Unsorted dirs -->
          <div
            v-for="dir in (watchData.unsortedDirectories || [])"
            :key="dir.path || dir"
            class="flex items-center gap-3 px-3 py-2.5 rounded-lg border border-forge-border bg-forge-bg group"
          >
            <span
              :class="['w-2 h-2 rounded-full shrink-0', dir.exists !== false ? 'bg-yellow-400' : 'bg-forge-danger']"
              :title="dir.exists !== false ? 'Directory exists' : 'Directory not found'"
            ></span>
            <span class="flex-1 min-w-0">
              <span class="text-sm font-mono text-forge-text truncate block" :title="dir.path || dir">
                {{ dir.path || dir }}
              </span>
              <span v-if="dir.itemCount != null" class="text-xs text-forge-text-muted">
                {{ dir.itemCount }} item{{ dir.itemCount !== 1 ? 's' : '' }}
              </span>
            </span>
            <span class="shrink-0 text-xs px-1.5 py-0.5 rounded bg-yellow-400/10 text-yellow-400 border border-yellow-400/30">
              unsorted
            </span>
            <div class="flex items-center gap-1 opacity-0 group-hover:opacity-100 transition-opacity">
              <button
                @click="scanDirectory(dir.path || dir)"
                :disabled="scanningPath === (dir.path || dir)"
                :class="[
                  'px-2.5 py-1 rounded text-xs font-medium transition-colors',
                  scanningPath === (dir.path || dir)
                    ? 'bg-forge-card text-forge-text-muted cursor-not-allowed'
                    : 'bg-forge-card border border-forge-border text-forge-text hover:border-forge-accent hover:text-forge-accent',
                ]"
              >
                {{ scanningPath === (dir.path || dir) ? '🔄' : '🔍 Scan' }}
              </button>
            </div>
          </div>
        </div>
      </div>
    </div>
  </div>
</template>

<!--
  FileBrowser.vue — Server-side directory/file picker modal
  
  Lets users browse the Forgekeeper server's filesystem to select a directory
  or file, rather than typing a path manually.
  
  Props:
    initialPath  — starting directory (default: server's first configured base path)
    mode         — "directory" | "file"  (default: "directory")
    fileFilter   — array of extensions to show, e.g. ['.zip', '.stl'] (default: show all)
    modelValue   — current selected path (v-model)
  
  Events:
    @select(path)  — user confirmed a selection
    @cancel        — user dismissed without selecting
    @update:modelValue  — v-model support
-->
<script setup>
import { ref, computed, watch, onMounted } from 'vue'

const props = defineProps({
  initialPath: {
    type: String,
    default: null,
  },
  mode: {
    type: String,
    default: 'directory',  // 'directory' | 'file'
    validator: v => ['directory', 'file'].includes(v),
  },
  fileFilter: {
    type: Array,
    default: () => [],  // [] = show all
  },
  modelValue: {
    type: String,
    default: '',
  },
})

const emit = defineEmits(['select', 'cancel', 'update:modelValue'])

// ── State ──────────────────────────────────────────────────────────────────────
const currentPath   = ref(props.initialPath || props.modelValue || '')
const entries       = ref([])
const breadcrumbs   = ref([])
const loading       = ref(false)
const error         = ref(null)
const searchFilter  = ref('')
const selectedEntry = ref(null)   // highlighted item (single-click)

// ── Computed ───────────────────────────────────────────────────────────────────
const filteredEntries = computed(() => {
  let list = entries.value

  // Apply extension filter for files (never filter directories)
  if (props.fileFilter.length > 0) {
    list = list.filter(e => {
      if (e.type === 'directory') return true
      const ext = '.' + e.name.split('.').pop().toLowerCase()
      return props.fileFilter.map(f => f.toLowerCase()).includes(ext)
    })
  }

  // Apply search/name filter
  const q = searchFilter.value.trim().toLowerCase()
  if (q) {
    list = list.filter(e => e.name === '..' || e.name.toLowerCase().includes(q))
  }

  return list
})

// The path that "Select" would confirm
const selectionPath = computed(() => {
  if (props.mode === 'directory') {
    // If the user clicked a directory in the list, that's the selection;
    // otherwise the current browsed directory
    if (selectedEntry.value && selectedEntry.value.type === 'directory' && selectedEntry.value.name !== '..')
      return selectedEntry.value.path
    return currentPath.value
  } else {
    // file mode: must have a file selected
    return selectedEntry.value?.type !== 'directory' ? selectedEntry.value?.path : null
  }
})

const canSelect = computed(() => {
  if (props.mode === 'directory') return !!currentPath.value
  return !!selectionPath.value
})

// ── Methods ────────────────────────────────────────────────────────────────────
async function browse(path) {
  loading.value = true
  error.value = null
  selectedEntry.value = null
  searchFilter.value = ''

  try {
    const url = path
      ? `/api/v1/files/browse?path=${encodeURIComponent(path)}`
      : '/api/v1/files/browse'
    const res = await fetch(url)

    if (!res.ok) {
      const body = await res.json().catch(() => ({}))
      throw new Error(body.message || `HTTP ${res.status}`)
    }

    const data = await res.json()
    currentPath.value = data.currentPath
    entries.value = data.entries || []
    breadcrumbs.value = data.breadcrumbs || []
  } catch (e) {
    error.value = e.message
  } finally {
    loading.value = false
  }
}

function handleEntryClick(entry) {
  if (entry.type === 'directory') {
    browse(entry.path)
  } else {
    // File click: select/deselect
    selectedEntry.value = selectedEntry.value?.path === entry.path ? null : entry
  }
}

function handleEntryDblClick(entry) {
  if (entry.type === 'directory') {
    browse(entry.path)
  } else {
    // Double-click file = immediate select
    emit('select', entry.path)
    emit('update:modelValue', entry.path)
  }
}

function confirmSelection() {
  const path = selectionPath.value
  if (!path) return
  emit('select', path)
  emit('update:modelValue', path)
}

function cancel() {
  emit('cancel')
}

function fmtSize(bytes) {
  if (!bytes || bytes === 0) return '—'
  if (bytes < 1024)           return `${bytes} B`
  if (bytes < 1024 * 1024)   return `${(bytes / 1024).toFixed(1)} KB`
  if (bytes < 1024 ** 3)     return `${(bytes / 1024 / 1024).toFixed(1)} MB`
  return `${(bytes / 1024 ** 3).toFixed(2)} GB`
}

function entryIcon(entry) {
  if (entry.name === '..') return '↩️'
  if (entry.type === 'directory') return '📁'
  if (entry.type === 'stl')       return '🧊'
  if (entry.type === 'archive')   return '📦'
  if (entry.type === 'image')     return '🖼️'
  if (entry.type === 'document')  return '📄'
  return '📎'
}

// ── Lifecycle ──────────────────────────────────────────────────────────────────
onMounted(() => {
  browse(props.initialPath || props.modelValue || undefined)
})
</script>

<template>
  <!-- Backdrop -->
  <div
    class="fixed inset-0 z-[60] flex items-center justify-center p-4 bg-black/70 backdrop-blur-sm"
    @click.self="cancel"
  >
    <!-- Modal -->
    <div class="w-full max-w-2xl max-h-[80vh] bg-forge-card border border-forge-border rounded-xl shadow-2xl flex flex-col overflow-hidden">

      <!-- ── Header ────────────────────────────────────────────────────── -->
      <div class="flex items-center justify-between px-4 py-3 border-b border-forge-border shrink-0">
        <h3 class="text-sm font-semibold text-forge-text">
          {{ mode === 'directory' ? '📁 Select Folder' : '📎 Select File' }}
        </h3>
        <button
          @click="cancel"
          class="text-forge-text-muted hover:text-forge-text transition-colors p-1 rounded"
          title="Close"
        >
          <svg class="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24">
            <path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M6 18L18 6M6 6l12 12"/>
          </svg>
        </button>
      </div>

      <!-- ── Breadcrumbs ───────────────────────────────────────────────── -->
      <div class="flex items-center gap-1 px-4 py-2 border-b border-forge-border bg-forge-bg shrink-0 overflow-x-auto">
        <button
          v-for="(crumb, idx) in breadcrumbs"
          :key="crumb.path"
          @click="browse(crumb.path)"
          :class="[
            'text-xs px-2 py-1 rounded transition-colors whitespace-nowrap',
            idx === breadcrumbs.length - 1
              ? 'text-forge-accent font-medium bg-forge-accent/10'
              : 'text-forge-text-muted hover:text-forge-text hover:bg-forge-card',
          ]"
        >{{ crumb.name }}</button>
        <span v-if="!breadcrumbs.length" class="text-xs text-forge-text-muted px-1">/</span>
      </div>

      <!-- ── Search bar ────────────────────────────────────────────────── -->
      <div class="px-4 py-2 border-b border-forge-border shrink-0">
        <input
          v-model="searchFilter"
          type="text"
          placeholder="Filter entries…"
          class="w-full bg-forge-bg border border-forge-border rounded-lg px-3 py-1.5 text-sm text-forge-text placeholder:text-forge-text-muted focus:outline-none focus:border-forge-accent"
        />
      </div>

      <!-- ── Directory listing ─────────────────────────────────────────── -->
      <div class="flex-1 overflow-y-auto">
        <!-- Loading -->
        <div v-if="loading" class="flex items-center justify-center py-12 gap-3 text-sm text-forge-text-muted">
          <div class="w-5 h-5 border-2 border-forge-accent border-t-transparent rounded-full animate-spin shrink-0"></div>
          Loading…
        </div>

        <!-- Error -->
        <div v-else-if="error" class="m-4 p-3 bg-forge-danger/10 border border-forge-danger/30 rounded-lg text-sm text-forge-danger">
          {{ error }}
        </div>

        <!-- Empty -->
        <div v-else-if="!filteredEntries.length" class="flex items-center justify-center py-12 text-sm text-forge-text-muted">
          {{ searchFilter ? 'No entries match your filter.' : 'Empty directory.' }}
        </div>

        <!-- Entries table -->
        <table v-else class="w-full text-sm">
          <tbody>
            <tr
              v-for="entry in filteredEntries"
              :key="entry.path + entry.name"
              @click="handleEntryClick(entry)"
              @dblclick="handleEntryDblClick(entry)"
              :class="[
                'cursor-pointer transition-colors border-b border-forge-border/50 last:border-0',
                selectedEntry?.path === entry.path
                  ? 'bg-forge-accent/10 text-forge-accent'
                  : entry.name === '..'
                    ? 'text-forge-text-muted hover:bg-forge-bg/60'
                    : 'text-forge-text hover:bg-forge-bg/60',
              ]"
            >
              <!-- Icon + Name -->
              <td class="px-4 py-2 flex items-center gap-2 min-w-0">
                <span class="shrink-0 text-base leading-none" aria-hidden="true">{{ entryIcon(entry) }}</span>
                <span class="truncate font-mono text-xs">{{ entry.name }}</span>
                <!-- Item count badge for directories -->
                <span
                  v-if="entry.type === 'directory' && entry.name !== '..' && entry.itemCount != null"
                  class="ml-1 shrink-0 text-xs px-1.5 py-0.5 rounded-full bg-forge-border text-forge-text-muted"
                >
                  {{ entry.itemCount }}
                </span>
              </td>
              <!-- Size -->
              <td class="px-4 py-2 text-xs text-forge-text-muted whitespace-nowrap text-right w-20">
                {{ entry.type !== 'directory' ? fmtSize(entry.size) : '' }}
              </td>
            </tr>
          </tbody>
        </table>
      </div>

      <!-- ── Current path display ──────────────────────────────────────── -->
      <div class="px-4 py-2 border-t border-forge-border bg-forge-bg/60 shrink-0">
        <p class="text-xs text-forge-text-muted truncate font-mono">
          <span class="text-forge-text-muted">Selected: </span>
          <span :class="selectionPath ? 'text-forge-accent' : 'text-forge-text-muted'">
            {{ selectionPath || currentPath || '—' }}
          </span>
        </p>
      </div>

      <!-- ── Footer ────────────────────────────────────────────────────── -->
      <div class="flex items-center justify-between px-4 py-3 border-t border-forge-border shrink-0">
        <p class="text-xs text-forge-text-muted">
          <template v-if="mode === 'directory'">
            Click a folder to navigate, or select it as destination
          </template>
          <template v-else>
            Double-click a file to select, or click once then hit Select
          </template>
        </p>
        <div class="flex items-center gap-2">
          <button
            @click="cancel"
            class="px-3 py-1.5 rounded-lg text-sm text-forge-text-muted hover:text-forge-text transition-colors"
          >
            Cancel
          </button>
          <button
            @click="confirmSelection"
            :disabled="!canSelect"
            class="px-4 py-1.5 rounded-lg text-sm font-medium bg-forge-accent hover:bg-forge-accent-hover text-forge-bg disabled:opacity-40 disabled:cursor-not-allowed transition-colors"
          >
            {{ mode === 'directory' ? '📁 Select Folder' : '📎 Select File' }}
          </button>
        </div>
      </div>

    </div>
  </div>
</template>

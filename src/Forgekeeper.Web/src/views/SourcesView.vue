<!--
  SourcesView.vue — Manage library sources
  List, add, edit, and remove source directories
-->
<script setup>
import { ref, onMounted } from 'vue'

const sources = ref([])
const loading = ref(true)
const showAdd = ref(false)
const newSource = ref({ slug: '', name: '', basePath: '', adapterType: 'GenericSourceAdapter', autoScan: true })
const error = ref('')
const success = ref('')

async function fetchSources() {
  loading.value = true
  try {
    const res = await fetch('/api/v1/sources')
    sources.value = await res.json()
  } catch (e) {
    error.value = 'Failed to load sources'
  } finally {
    loading.value = false
  }
}

async function addSource() {
  error.value = ''
  success.value = ''
  try {
    const res = await fetch('/api/v1/sources', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(newSource.value),
    })
    if (res.ok) {
      success.value = `Source "${newSource.value.name}" added successfully`
      newSource.value = { slug: '', name: '', basePath: '', adapterType: 'GenericSourceAdapter', autoScan: true }
      showAdd.value = false
      await fetchSources()
    } else {
      const data = await res.json()
      error.value = data.message || 'Failed to add source'
    }
  } catch (e) {
    error.value = 'Failed to add source'
  }
}

async function deleteSource(slug) {
  if (!confirm(`Delete source "${slug}"? Models will NOT be deleted.`)) return
  try {
    await fetch(`/api/v1/sources/${slug}`, { method: 'DELETE' })
    await fetchSources()
  } catch (e) {
    error.value = 'Failed to delete source'
  }
}

async function triggerScan() {
  try {
    const res = await fetch('/api/v1/scan', { method: 'POST' })
    const data = await res.json()
    success.value = data.message || 'Scan started'
  } catch (e) {
    error.value = 'Failed to trigger scan'
  }
}

onMounted(fetchSources)
</script>

<template>
  <div>
    <div class="flex justify-between items-center mb-6">
      <h1 class="text-2xl font-bold">Library Sources</h1>
      <div class="flex gap-3">
        <button
          @click="triggerScan"
          class="px-4 py-2 bg-forge-accent text-white rounded-lg hover:bg-forge-accent/80 transition"
        >
          🔄 Scan Now
        </button>
        <button
          @click="showAdd = !showAdd"
          class="px-4 py-2 bg-forge-primary text-white rounded-lg hover:bg-forge-primary/80 transition"
        >
          {{ showAdd ? '✕ Cancel' : '+ Add Source' }}
        </button>
      </div>
    </div>

    <!-- Alerts -->
    <div v-if="error" class="mb-4 p-3 bg-red-500/20 border border-red-500 rounded-lg text-red-400">
      {{ error }}
      <button @click="error = ''" class="ml-2 text-red-300 hover:text-white">✕</button>
    </div>
    <div v-if="success" class="mb-4 p-3 bg-green-500/20 border border-green-500 rounded-lg text-green-400">
      {{ success }}
      <button @click="success = ''" class="ml-2 text-green-300 hover:text-white">✕</button>
    </div>

    <!-- Add Source Form -->
    <div v-if="showAdd" class="mb-6 p-4 bg-forge-surface rounded-lg border border-forge-border">
      <h2 class="text-lg font-semibold mb-4">Add New Source</h2>
      <div class="grid grid-cols-1 md:grid-cols-2 gap-4">
        <div>
          <label class="block text-sm text-forge-muted mb-1">Slug (unique ID)</label>
          <input v-model="newSource.slug" type="text" placeholder="e.g. mmf, thangs, other"
            class="w-full px-3 py-2 bg-forge-bg border border-forge-border rounded text-forge-text" />
        </div>
        <div>
          <label class="block text-sm text-forge-muted mb-1">Display Name</label>
          <input v-model="newSource.name" type="text" placeholder="e.g. MyMiniFactory"
            class="w-full px-3 py-2 bg-forge-bg border border-forge-border rounded text-forge-text" />
        </div>
        <div class="md:col-span-2">
          <label class="block text-sm text-forge-muted mb-1">Base Path (directory containing creator folders)</label>
          <input v-model="newSource.basePath" type="text" placeholder="e.g. /library/MMFDownloader"
            class="w-full px-3 py-2 bg-forge-bg border border-forge-border rounded text-forge-text" />
        </div>
        <div>
          <label class="block text-sm text-forge-muted mb-1">Adapter Type</label>
          <select v-model="newSource.adapterType"
            class="w-full px-3 py-2 bg-forge-bg border border-forge-border rounded text-forge-text">
            <option value="GenericSourceAdapter">Generic</option>
            <option value="MmfSourceAdapter">MyMiniFactory</option>
            <option value="PatreonSourceAdapter">Patreon</option>
          </select>
        </div>
        <div class="flex items-end">
          <label class="flex items-center gap-2">
            <input v-model="newSource.autoScan" type="checkbox" class="rounded" />
            <span class="text-sm">Auto-scan on schedule</span>
          </label>
        </div>
      </div>
      <button @click="addSource" class="mt-4 px-6 py-2 bg-forge-primary text-white rounded-lg hover:bg-forge-primary/80 transition">
        Add Source
      </button>
    </div>

    <!-- Sources List -->
    <div v-if="loading" class="text-center text-forge-muted py-8">Loading sources...</div>
    <div v-else-if="sources.length === 0" class="text-center text-forge-muted py-8">
      <p class="text-lg mb-2">No sources configured</p>
      <p>Add a source directory to start cataloging your 3D models.</p>
    </div>
    <div v-else class="grid gap-4">
      <div v-for="source in sources" :key="source.id"
        class="p-4 bg-forge-surface rounded-lg border border-forge-border hover:border-forge-accent/30 transition">
        <div class="flex justify-between items-start">
          <div>
            <h3 class="text-lg font-semibold">{{ source.name }}</h3>
            <p class="text-sm text-forge-muted mt-1">
              <span class="font-mono text-xs bg-forge-bg px-2 py-0.5 rounded">{{ source.slug }}</span>
              · {{ source.adapterType }}
              <span v-if="source.autoScan" class="text-green-400 ml-2">● Auto-scan</span>
              <span v-else class="text-forge-muted ml-2">○ Manual scan</span>
            </p>
            <p class="text-sm text-forge-muted mt-1 font-mono">{{ source.basePath }}</p>
          </div>
          <div class="flex items-center gap-4">
            <div class="text-right">
              <p class="text-2xl font-bold">{{ source.modelCount }}</p>
              <p class="text-xs text-forge-muted">models</p>
            </div>
            <button @click="deleteSource(source.slug)"
              class="p-2 text-red-400 hover:text-red-300 hover:bg-red-500/10 rounded transition"
              title="Delete source">
              🗑️
            </button>
          </div>
        </div>
      </div>
    </div>
  </div>
</template>

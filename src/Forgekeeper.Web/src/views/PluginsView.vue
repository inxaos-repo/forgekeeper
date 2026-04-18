<!--
  PluginsView.vue — Plugin admin interface (WP-PL3/WP-PL4/WP-PL5)
  - Rich plugin cards with manifest metadata, trust badge, SDK compat
  - Enable/disable toggle
  - Sync controls, progress, history
  - Hot-reload button
  - Diagnostics expandable section
-->
<script setup>
import { ref, computed, onMounted, onBeforeUnmount } from 'vue'
import { useApi } from '../composables/useApi.js'
import PluginTrustBadge from '../components/PluginTrustBadge.vue'
import PluginSdkBadge from '../components/PluginSdkBadge.vue'

const api = useApi()

// Tab state
const activeTab = ref('installed') // 'installed' | 'available'

// ─── Available Plugins (registry browser) ────────────────
const availablePlugins = ref([])
const registrySearch = ref('')
const registryLoading = ref(false)
const registryError = ref(null)
const installingSlug = ref(null)
const installMessage = ref(null)
const availableUpdates = ref({})

async function fetchRegistry(forceRefresh = false) {
  registryLoading.value = true
  registryError.value = null
  try {
    let url = '/plugins/registry'
    const params = []
    if (registrySearch.value.trim()) params.push(`search=${encodeURIComponent(registrySearch.value.trim())}`)
    if (forceRefresh) params.push('forceRefresh=true')
    if (params.length) url += '?' + params.join('&')
    availablePlugins.value = await api.get(url) || []
  } catch (e) {
    registryError.value = e.message
    availablePlugins.value = []
  } finally {
    registryLoading.value = false
  }
}

async function fetchAvailableUpdates() {
  try {
    const result = await api.getPluginUpdates()
    availableUpdates.value = result?.updates || {}
  } catch {
    availableUpdates.value = {}
  }
}

function isInstalled(slug) {
  return plugins.value.some(p => (p.slug || p.sourceSlug) === slug)
}

function getInstalledVersion(slug) {
  const p = plugins.value.find(p => (p.slug || p.sourceSlug) === slug)
  return p?.version || null
}

function hasUpdate(slug) {
  return !!availableUpdates.value[slug]
}

function getUpdateInfo(slug) {
  return availableUpdates.value[slug] || null
}

async function installFromRegistry(entry) {
  installingSlug.value = entry.slug
  installMessage.value = null
  try {
    await api.installPlugin(entry.downloadUrl)
    installMessage.value = `✓ '${entry.name}' installed successfully`
    await fetchPlugins()
  } catch (e) {
    installMessage.value = `✗ Install failed: ${e.message}`
  } finally {
    installingSlug.value = null
    setTimeout(() => (installMessage.value = null), 6000)
  }
}

async function updateFromRegistry(entry) {
  installingSlug.value = entry.slug
  installMessage.value = null
  try {
    await api.updatePlugin(entry.slug)
    installMessage.value = `✓ '${entry.name}' updated`
    await fetchPlugins()
    await fetchAvailableUpdates()
  } catch (e) {
    installMessage.value = `✗ Update failed: ${e.message}`
  } finally {
    installingSlug.value = null
    setTimeout(() => (installMessage.value = null), 6000)
  }
}

const plugins = ref([])
const selectedSlug = ref(null)
const pluginConfig = ref(null)
const pluginStatus = ref(null)
const adminHtml = ref(null)
const configDirty = ref(false)

const savingConfig = ref(false)
const saveConfigSuccess = ref(false)
const syncingSlug = ref(null)
const statusPollTimer = ref(null)
const sseSource = ref(null)
const liveProgress = ref(null)

// Hot-reload state
const reloading = ref(false)
const reloadingSlug = ref(null)
const reloadMessage = ref(null)

// Sync history
const syncHistory = ref([])
const loadingHistory = ref(false)
const showHistory = ref(false)

// Diagnostics
const diagnostics = ref(null)
const loadingDiagnostics = ref(false)
const showDiagnostics = ref(false)

// Manifest warnings/errors panel
const showManifestDetails = ref(false)

// Editing config values (keyed by field key)
const configValues = ref({})

const selectedPlugin = computed(() =>
  plugins.value.find((p) => p.slug === selectedSlug.value || p.sourceSlug === selectedSlug.value)
)

function statusLabel(plugin) {
  if (!plugin) return 'unknown'
  if (plugin.syncRunning || plugin.isSyncing || plugin.syncStatus?.isRunning) return 'syncing'
  if (plugin.isAuthenticated || plugin.authenticated) return 'ready'
  if (plugin.requiresBrowserAuth) return 'needs auth'
  return 'installed'
}

function statusColor(plugin) {
  const s = statusLabel(plugin)
  if (s === 'syncing') return 'text-source-mmf'
  if (s === 'ready') return 'text-forge-accent'
  if (s === 'needs auth') return 'text-forge-danger'
  return 'text-forge-text-muted'
}

function statusDot(plugin) {
  const s = statusLabel(plugin)
  if (s === 'syncing') return 'bg-source-mmf animate-pulse'
  if (s === 'ready') return 'bg-forge-accent'
  if (s === 'needs auth') return 'bg-forge-danger'
  return 'bg-forge-text-muted'
}

function formatDate(dateStr) {
  if (!dateStr) return 'Never'
  try {
    const d = new Date(dateStr)
    return d.toLocaleDateString(undefined, { month: 'short', day: 'numeric', hour: '2-digit', minute: '2-digit' })
  } catch { return dateStr }
}

function formatDuration(seconds) {
  if (!seconds) return '—'
  if (seconds < 60) return `${Math.round(seconds)}s`
  if (seconds < 3600) return `${Math.round(seconds / 60)}m`
  return `${(seconds / 3600).toFixed(1)}h`
}

// ─── Fetch ───────────────────────────────────────────────
async function fetchPlugins() {
  try {
    const result = await api.getPlugins()
    plugins.value = result?.items || result || []
    // Auto-select first if none selected
    if (!selectedSlug.value && plugins.value.length) {
      selectPlugin(plugins.value[0].slug || plugins.value[0].sourceSlug)
    }
  } catch {
    plugins.value = []
  }
}

async function selectPlugin(slug) {
  selectedSlug.value = slug
  configDirty.value = false
  adminHtml.value = null
  syncHistory.value = []
  showHistory.value = false
  showDiagnostics.value = false
  diagnostics.value = null
  showManifestDetails.value = false
  await Promise.all([
    fetchPluginConfig(slug),
    fetchPluginStatus(slug),
    fetchAdminHtml(slug),
  ])
}

async function fetchPluginConfig(slug) {
  try {
    const result = await api.getPluginConfig(slug)
    pluginConfig.value = result
    // Initialize config values from current stored values
    configValues.value = {}
    if (result?.schema) {
      for (const field of result.schema) {
        configValues.value[field.key] = result.values?.[field.key] ?? field.defaultValue ?? ''
      }
    } else if (Array.isArray(result)) {
      // API returns array of field objects with .key and .value
      for (const field of result) {
        configValues.value[field.key] = field.value ?? field.defaultValue ?? ''
      }
    } else if (result?.values) {
      configValues.value = { ...result.values }
    }
  } catch {
    pluginConfig.value = null
    configValues.value = {}
  }
}

async function fetchPluginStatus(slug) {
  try {
    pluginStatus.value = await api.getPluginStatus(slug)
  } catch {
    pluginStatus.value = null
  }
}

async function fetchAdminHtml(slug) {
  try {
    const result = await api.getPluginAdminHtml(slug)
    adminHtml.value = result?.html || null
  } catch {
    adminHtml.value = null
  }
}

async function fetchSyncHistory(slug) {
  loadingHistory.value = true
  try {
    const result = await api.getPluginHistory(slug, 20)
    syncHistory.value = result || []
  } catch {
    syncHistory.value = []
  } finally {
    loadingHistory.value = false
  }
}

async function fetchDiagnostics(slug) {
  loadingDiagnostics.value = true
  try {
    diagnostics.value = await api.getPluginDiagnostics(slug)
  } catch {
    diagnostics.value = null
  } finally {
    loadingDiagnostics.value = false
  }
}

function toggleHistory() {
  showHistory.value = !showHistory.value
  if (showHistory.value && !syncHistory.value.length && selectedSlug.value) {
    fetchSyncHistory(selectedSlug.value)
  }
}

function toggleDiagnostics() {
  showDiagnostics.value = !showDiagnostics.value
  if (showDiagnostics.value && !diagnostics.value && selectedSlug.value) {
    fetchDiagnostics(selectedSlug.value)
  }
}

// ─── Actions ─────────────────────────────────────────────
async function saveConfig() {
  if (!selectedSlug.value) return
  savingConfig.value = true
  saveConfigSuccess.value = false
  try {
    await api.updatePluginConfig(selectedSlug.value, configValues.value)
    saveConfigSuccess.value = true
    configDirty.value = false
    setTimeout(() => (saveConfigSuccess.value = false), 3000)
    await fetchPluginStatus(selectedSlug.value)
  } catch { /* error shown by api.error */ }
  finally { savingConfig.value = false }
}

async function triggerSync(slug) {
  syncingSlug.value = slug
  liveProgress.value = null
  try {
    await api.triggerPluginSync(slug)
    startProgressStream(slug)
  } catch { syncingSlug.value = null }
}

async function hotReloadAll() {
  reloading.value = true
  reloadMessage.value = null
  try {
    const result = await api.reloadPlugins()
    reloadMessage.value = `Reloaded ${result?.loaded ?? 0} plugin(s)`
    await fetchPlugins()
    if (selectedSlug.value) await fetchPluginStatus(selectedSlug.value)
  } catch (e) {
    if (e.message?.includes('501')) {
      reloadMessage.value = 'Hot-reload not enabled (set Plugins:HotReloadEnabled=true)'
    } else {
      reloadMessage.value = `Reload failed: ${e.message}`
    }
  } finally {
    reloading.value = false
    setTimeout(() => (reloadMessage.value = null), 5000)
  }
}

async function hotReloadPlugin(slug) {
  reloadingSlug.value = slug
  reloadMessage.value = null
  try {
    await api.reloadPlugin(slug)
    reloadMessage.value = `Plugin '${slug}' reloaded`
    await fetchPlugins()
    await fetchPluginStatus(slug)
  } catch (e) {
    if (e.message?.includes('501')) {
      reloadMessage.value = 'Hot-reload not enabled (set Plugins:HotReloadEnabled=true)'
    } else {
      reloadMessage.value = `Reload failed: ${e.message}`
    }
  } finally {
    reloadingSlug.value = null
    setTimeout(() => (reloadMessage.value = null), 5000)
  }
}

function startProgressStream(slug) {
  stopProgressStream()
  try {
    const es = new EventSource(`/api/v1/plugins/${slug}/progress`)
    sseSource.value = es

    es.addEventListener('progress', (e) => {
      try { liveProgress.value = JSON.parse(e.data) } catch {}
    })

    es.addEventListener('complete', (e) => {
      try { liveProgress.value = { ...JSON.parse(e.data), complete: true } } catch {}
      stopProgressStream()
      syncingSlug.value = null
      fetchPlugins()
      fetchPluginStatus(slug)
    })

    es.addEventListener('error', () => {
      stopProgressStream()
      startStatusPoll(slug)
    })
  } catch {
    startStatusPoll(slug)
  }
}

function startStatusPoll(slug) {
  stopStatusPoll()
  statusPollTimer.value = setInterval(async () => {
    await fetchPluginStatus(slug)
    if (pluginStatus.value) {
      liveProgress.value = {
        scraped: pluginStatus.value.scrapedModels || 0,
        total: pluginStatus.value.totalModels || 0,
        failed: pluginStatus.value.failedModels || 0,
        currentItem: pluginStatus.value.currentProgress?.currentItem || '',
        status: pluginStatus.value.currentProgress?.status || 'syncing',
      }
    }
    await fetchPlugins()
    const plugin = plugins.value.find((p) => (p.slug || p.sourceSlug) === slug)
    if (!plugin?.syncRunning && !plugin?.isSyncing && !pluginStatus.value?.isRunning) {
      stopStatusPoll()
      syncingSlug.value = null
      liveProgress.value = null
    }
  }, 3000)
}

function stopProgressStream() {
  if (sseSource.value) {
    sseSource.value.close()
    sseSource.value = null
  }
  stopStatusPoll()
}

function stopStatusPoll() {
  if (statusPollTimer.value) {
    clearInterval(statusPollTimer.value)
    statusPollTimer.value = null
  }
}

async function authenticate(plugin, force = false) {
  const slug = plugin.slug || plugin.sourceSlug
  try {
    const res = await fetch(`/api/v1/plugins/${slug}/auth${force ? '?force=true' : ''}`)
    const data = await res.json()
    if (data.authenticated && !force && !data.authUrl) {
      await fetchPlugins()
      return
    }
    if (data.authUrl) {
      window.open(data.authUrl, '_blank', 'width=600,height=700')
    } else {
      return
    }
  } catch {
    window.open(`/api/v1/plugins/${slug}/auth`, '_blank', 'width=600,height=700')
  }
  const pollInterval = setInterval(async () => {
    try {
      const res = await fetch(`/api/v1/plugins/${slug}/auth`)
      const data = await res.json()
      if (data.authenticated) {
        clearInterval(pollInterval)
        await fetchPlugins()
      }
    } catch {}
  }, 3000)
  setTimeout(() => clearInterval(pollInterval), 300000)
}

function onConfigChange() {
  configDirty.value = true
}

// Config field schema — handles both array-of-fields and schema wrapper
const configSchema = computed(() => {
  if (!pluginConfig.value) return []
  if (Array.isArray(pluginConfig.value)) return pluginConfig.value
  if (pluginConfig.value.schema) return pluginConfig.value.schema
  return []
})

function estimateTimeRemaining(progress) {
  if (!progress?.scraped || !progress?.total) return ''
  const pct = progress.scraped / progress.total
  if (pct <= 0) return ''
  const elapsed = progress.elapsedMs || 60000
  const totalEstimate = elapsed / pct
  const remaining = totalEstimate - elapsed
  if (remaining < 60000) return 'less than a minute'
  if (remaining < 3600000) return `${Math.round(remaining / 60000)} minutes`
  return `${(remaining / 3600000).toFixed(1)} hours`
}

function syncRunStatusColor(status) {
  if (status === 'completed') return 'text-forge-accent'
  if (status === 'failed') return 'text-forge-danger'
  if (status === 'running') return 'text-yellow-400'
  return 'text-forge-text-muted'
}

onMounted(() => {
  fetchPlugins()
  fetchAvailableUpdates()
})
onBeforeUnmount(() => {
  stopProgressStream()
  stopStatusPoll()
})
</script>

<template>
  <div>
    <!-- Header: title + tabs + reload button -->
    <div class="flex items-center justify-between mb-6">
      <div class="flex items-center gap-6">
        <h1 class="text-2xl font-bold text-forge-text">Plugins</h1>
        <!-- Tab switcher -->
        <div class="flex gap-1 bg-forge-card border border-forge-border rounded-lg p-0.5">
          <button
            @click="activeTab = 'installed'"
            :class="[
              'px-3 py-1 rounded text-sm font-medium transition-colors',
              activeTab === 'installed'
                ? 'bg-forge-bg text-forge-text'
                : 'text-forge-text-muted hover:text-forge-text',
            ]"
          >
            Installed
          </button>
          <button
            @click="activeTab = 'available'; if (!availablePlugins.length) fetchRegistry()"
            :class="[
              'px-3 py-1 rounded text-sm font-medium transition-colors relative',
              activeTab === 'available'
                ? 'bg-forge-bg text-forge-text'
                : 'text-forge-text-muted hover:text-forge-text',
            ]"
          >
            Available
            <span
              v-if="Object.keys(availableUpdates).length > 0"
              class="absolute -top-1 -right-1 min-w-[1rem] h-4 flex items-center justify-center bg-yellow-500 text-forge-bg text-[9px] font-bold rounded-full px-0.5"
            >
              {{ Object.keys(availableUpdates).length }}
            </span>
          </button>
        </div>
      </div>
      <button
        @click="hotReloadAll"
        :disabled="reloading"
        :class="[
          'flex items-center gap-2 px-3 py-1.5 rounded-lg text-sm font-medium transition-colors border',
          reloading
            ? 'text-forge-text-muted border-forge-border cursor-not-allowed'
            : 'text-forge-text-muted border-forge-border hover:text-forge-text hover:border-forge-accent/50',
        ]"
        title="Hot-reload all plugins (requires Plugins:HotReloadEnabled=true)"
      >
        <span :class="reloading ? 'animate-spin' : ''">🔄</span>
        <span>{{ reloading ? 'Reloading...' : 'Reload All' }}</span>
      </button>
    </div>

    <!-- Reload message toast -->
    <div v-if="reloadMessage" class="mb-4 px-4 py-2 rounded-lg text-sm bg-forge-card border border-forge-border text-forge-text-muted">
      {{ reloadMessage }}
    </div>

    <!-- ─────────── Available Plugins Tab ────────────────────────────────────────────── -->
    <div v-if="activeTab === 'available'">
      <!-- Search bar + Refresh button -->
      <div class="flex gap-3 mb-4">
        <input
          v-model="registrySearch"
          @keyup.enter="fetchRegistry()"
          type="text"
          placeholder="Search plugins by name, tag, description…"
          class="flex-1 bg-forge-bg border border-forge-border rounded-lg px-4 py-2 text-sm text-forge-text placeholder-forge-text-muted focus:outline-none focus:border-forge-accent"
        />
        <button
          @click="fetchRegistry()"
          class="px-3 py-2 bg-forge-card border border-forge-border rounded-lg text-sm text-forge-text-muted hover:text-forge-text transition-colors"
        >Search</button>
        <button
          @click="fetchRegistry(true)"
          :disabled="registryLoading"
          class="px-3 py-2 bg-forge-card border border-forge-border rounded-lg text-sm text-forge-text-muted hover:text-forge-text transition-colors"
          title="Force-reload registry from server"
        >↻ Refresh</button>
      </div>

      <!-- Install message toast -->
      <div v-if="installMessage" class="mb-4 px-4 py-2 rounded-lg text-sm border"
        :class="installMessage.startsWith('✓') ? 'bg-forge-accent/10 border-forge-accent/30 text-forge-accent' : 'bg-forge-danger/10 border-forge-danger/30 text-forge-danger'">
        {{ installMessage }}
      </div>

      <!-- Loading state -->
      <div v-if="registryLoading" class="flex justify-center py-20">
        <div class="w-8 h-8 border-2 border-forge-accent border-t-transparent rounded-full animate-spin"></div>
      </div>

      <!-- Error state -->
      <div v-else-if="registryError" class="text-center py-20">
        <span class="text-4xl">⚠️</span>
        <p class="text-forge-danger mt-3">{{ registryError }}</p>
        <button @click="fetchRegistry()" class="mt-3 text-sm text-forge-accent hover:underline">Retry</button>
      </div>

      <!-- Empty state -->
      <div v-else-if="!availablePlugins.length" class="text-center py-20">
        <span class="text-5xl">🔍</span>
        <p class="text-forge-text-muted mt-4">No plugins found</p>
        <p class="text-sm text-forge-text-muted mt-1">Try a different search, or click Refresh to reload the registry.</p>
      </div>

      <!-- Plugin cards grid -->
      <div v-else class="grid grid-cols-1 md:grid-cols-2 xl:grid-cols-3 gap-4">
        <div
          v-for="entry in availablePlugins"
          :key="entry.slug"
          class="bg-forge-card border border-forge-border rounded-xl p-4 flex flex-col gap-3"
        >
          <!-- Card header -->
          <div class="flex items-start gap-3">
            <img
              v-if="entry.iconUrl"
              :src="entry.iconUrl"
              :alt="entry.name"
              class="w-10 h-10 rounded-lg object-cover shrink-0"
            />
            <div v-else class="w-10 h-10 rounded-lg bg-forge-bg flex items-center justify-center text-xl shrink-0">🔌</div>
            <div class="min-w-0 flex-1">
              <h3 class="font-semibold text-forge-text truncate">{{ entry.name }}</h3>
              <p class="text-xs text-forge-text-muted">v{{ entry.version }} · {{ entry.author }}</p>
            </div>
          </div>

          <!-- Description -->
          <p class="text-sm text-forge-text-muted line-clamp-2">{{ entry.description }}</p>

          <!-- Tags -->
          <div v-if="entry.tags?.length" class="flex flex-wrap gap-1">
            <span
              v-for="tag in entry.tags.slice(0, 4)"
              :key="tag"
              class="px-1.5 py-0.5 text-xs bg-forge-bg text-forge-text-muted rounded"
            >{{ tag }}</span>
          </div>

          <!-- Action button -->
          <div class="mt-auto">
            <!-- Installed + has update -->
            <button
              v-if="isInstalled(entry.slug) && hasUpdate(entry.slug)"
              @click="updateFromRegistry(entry)"
              :disabled="installingSlug === entry.slug"
              class="w-full py-1.5 rounded-lg text-sm font-medium bg-yellow-500/15 border border-yellow-500/40 text-yellow-400 hover:bg-yellow-500/25 transition-colors"
            >
              <span v-if="installingSlug === entry.slug">🔄 Updating…</span>
              <span v-else>Update Available ({{ getInstalledVersion(entry.slug) }} → {{ entry.version }})</span>
            </button>

            <!-- Installed, no update -->
            <div
              v-else-if="isInstalled(entry.slug)"
              class="w-full py-1.5 rounded-lg text-sm font-medium text-center bg-forge-accent/10 border border-forge-accent/30 text-forge-accent"
            >✓ Installed</div>

            <!-- Not installed -->
            <button
              v-else
              @click="installFromRegistry(entry)"
              :disabled="installingSlug === entry.slug"
              class="w-full py-1.5 rounded-lg text-sm font-medium bg-forge-accent hover:bg-forge-accent-hover text-forge-bg transition-colors disabled:opacity-50 disabled:cursor-not-allowed"
            >
              {{ installingSlug === entry.slug ? '🔄 Installing…' : 'Install' }}
            </button>
          </div>
        </div>
      </div>
    </div>

    <!-- ─────────── Installed Plugins Tab ────────────────────────────────────────────── -->
    <div v-if="activeTab === 'installed'">

    <!-- Loading -->
    <div v-if="api.loading.value && !plugins.length" class="flex justify-center py-20">
      <div class="w-8 h-8 border-2 border-forge-accent border-t-transparent rounded-full animate-spin"></div>
    </div>

    <!-- Empty state -->
    <div v-else-if="!plugins.length" class="text-center py-20">
      <span class="text-5xl">🔌</span>
      <p class="text-forge-text-muted mt-4">No plugins installed</p>
      <p class="text-sm text-forge-text-muted mt-1">
        Place plugin DLLs in the <code class="text-forge-accent">plugins/</code> directory
      </p>
    </div>

    <div v-else class="flex flex-col lg:flex-row gap-6">
      <!-- Plugin list (sidebar) -->
      <div class="lg:w-72 shrink-0">
        <div class="bg-forge-card border border-forge-border rounded-xl overflow-hidden">
          <div
            v-for="plugin in plugins"
            :key="plugin.slug || plugin.sourceSlug"
            @click="selectPlugin(plugin.slug || plugin.sourceSlug)"
            :class="[
              'px-4 py-3 cursor-pointer transition-colors border-b border-forge-border last:border-b-0',
              (plugin.slug || plugin.sourceSlug) === selectedSlug
                ? 'bg-forge-accent/10 border-l-2 border-l-forge-accent'
                : 'hover:bg-forge-bg',
            ]"
          >
            <div class="flex items-center justify-between">
              <div class="min-w-0">
                <h3 class="text-sm font-semibold text-forge-text truncate">
                  {{ plugin.name || plugin.sourceName || plugin.slug || plugin.sourceSlug }}
                </h3>
                <p class="text-xs text-forge-text-muted mt-0.5">
                  v{{ plugin.version || '?' }}
                  <span v-if="plugin.author" class="ml-1">· {{ plugin.author }}</span>
                </p>
              </div>
              <div class="flex items-center gap-2 shrink-0">
                <div :class="['w-2 h-2 rounded-full', statusDot(plugin)]"></div>
                <span :class="['text-xs capitalize', statusColor(plugin)]">{{ statusLabel(plugin) }}</span>
              </div>
            </div>
            <!-- Trust + SDK badges row -->
            <div class="flex items-center gap-1.5 mt-1.5 flex-wrap">
              <PluginTrustBadge :source="plugin.source" :manifest-valid="plugin.manifestValid" />
              <PluginSdkBadge :level="plugin.sdkCompatLevel" :reason="plugin.sdkCompatReason" />
            </div>
          </div>
        </div>
      </div>

      <!-- Plugin detail -->
      <div v-if="selectedPlugin" class="flex-1 space-y-6">

        <!-- ── Plugin header card ────────────────────────────── -->
        <div class="bg-forge-card border border-forge-border rounded-xl p-5">
          <div class="flex items-start justify-between gap-4">
            <div class="min-w-0 flex-1">
              <!-- Name + version -->
              <div class="flex items-center gap-3 flex-wrap">
                <h2 class="text-xl font-bold text-forge-text">
                  {{ selectedPlugin.name || selectedPlugin.sourceName || selectedSlug }}
                </h2>
                <span class="text-sm text-forge-text-muted font-mono">v{{ selectedPlugin.version || '?' }}</span>
              </div>

              <!-- Author + slug -->
              <div class="flex items-center gap-4 mt-1 text-sm text-forge-text-muted flex-wrap">
                <span v-if="selectedPlugin.author">by <strong class="text-forge-text">{{ selectedPlugin.author }}</strong></span>
                <span>slug: <code class="text-forge-accent">{{ selectedPlugin.slug || selectedPlugin.sourceSlug }}</code></span>
              </div>

              <!-- Description -->
              <p v-if="selectedPlugin.description" class="text-sm text-forge-text-muted mt-2">
                {{ selectedPlugin.description }}
              </p>

              <!-- Trust + SDK badges -->
              <div class="flex items-center gap-2 mt-3 flex-wrap">
                <PluginTrustBadge :source="selectedPlugin.source" :manifest-valid="selectedPlugin.manifestValid" />
                <PluginSdkBadge :level="selectedPlugin.sdkCompatLevel" :reason="selectedPlugin.sdkCompatReason" />
              </div>
            </div>

            <!-- Action buttons -->
            <div class="flex items-center gap-2 shrink-0 flex-wrap justify-end">
              <!-- Auth button -->
              <button
                v-if="selectedPlugin.requiresBrowserAuth && !(selectedPlugin.isAuthenticated || selectedPlugin.authenticated)"
                @click="authenticate(selectedPlugin)"
                class="px-3 py-1.5 bg-forge-danger/15 border border-forge-danger/30 text-forge-danger rounded-lg text-sm font-medium hover:bg-forge-danger/25 transition-colors"
              >
                🔑 Auth
              </button>

              <!-- Hot-reload single plugin -->
              <button
                @click="hotReloadPlugin(selectedSlug)"
                :disabled="reloadingSlug === selectedSlug"
                :class="[
                  'px-3 py-1.5 rounded-lg text-sm font-medium transition-colors border',
                  reloadingSlug === selectedSlug
                    ? 'text-forge-text-muted border-forge-border cursor-not-allowed'
                    : 'text-forge-text-muted border-forge-border hover:text-forge-text hover:border-forge-accent/50',
                ]"
                title="Hot-reload this plugin"
              >
                <span :class="reloadingSlug === selectedSlug ? 'animate-spin inline-block' : ''">♻️</span>
              </button>

              <!-- Sync Now -->
              <button
                @click="triggerSync(selectedSlug)"
                :disabled="syncingSlug === selectedSlug"
                :class="[
                  'px-4 py-1.5 rounded-lg text-sm font-medium transition-colors',
                  syncingSlug === selectedSlug
                    ? 'bg-forge-card text-forge-text-muted cursor-not-allowed'
                    : 'bg-forge-accent hover:bg-forge-accent-hover text-forge-bg',
                ]"
              >
                {{ syncingSlug === selectedSlug ? '🔄 Syncing...' : '🔄 Sync Now' }}
              </button>
            </div>
          </div>

          <!-- Manifest warnings/errors toggle -->
          <div
            v-if="(selectedPlugin.manifestErrors?.length || selectedPlugin.manifestWarnings?.length)"
            class="mt-4 border-t border-forge-border pt-3"
          >
            <button
              @click="showManifestDetails = !showManifestDetails"
              class="flex items-center gap-2 text-xs text-forge-text-muted hover:text-forge-text transition-colors"
            >
              <span>{{ showManifestDetails ? '▼' : '▶' }}</span>
              <span v-if="selectedPlugin.manifestErrors?.length" class="text-forge-danger">
                {{ selectedPlugin.manifestErrors.length }} manifest error(s)
              </span>
              <span v-if="selectedPlugin.manifestWarnings?.length" class="text-yellow-400 ml-2">
                {{ selectedPlugin.manifestWarnings.length }} warning(s)
              </span>
            </button>
            <div v-if="showManifestDetails" class="mt-2 space-y-1">
              <p
                v-for="err in selectedPlugin.manifestErrors"
                :key="err"
                class="text-xs text-forge-danger bg-forge-danger/10 rounded px-2 py-1"
              >⛔ {{ err }}</p>
              <p
                v-for="warn in selectedPlugin.manifestWarnings"
                :key="warn"
                class="text-xs text-yellow-400 bg-yellow-500/10 rounded px-2 py-1"
              >⚠️ {{ warn }}</p>
            </div>
          </div>
        </div>

        <!-- ── Sync progress ────────────────────────────────── -->
        <div
          v-if="liveProgress || syncingSlug === selectedSlug"
          class="bg-forge-card border border-forge-accent/30 rounded-xl p-4"
        >
          <div class="flex items-center gap-3 mb-3">
            <div v-if="!liveProgress?.complete" class="w-5 h-5 border-2 border-forge-accent border-t-transparent rounded-full animate-spin shrink-0"></div>
            <span v-else class="text-lg">✅</span>
            <div class="flex-1 min-w-0">
              <span class="text-sm font-medium text-forge-text">
                {{ liveProgress?.complete ? 'Sync complete!' : liveProgress?.status || 'Starting sync...' }}
              </span>
              <p v-if="liveProgress?.currentItem && !liveProgress?.complete" class="text-xs text-forge-text-muted truncate mt-0.5">
                {{ liveProgress.currentItem }}
              </p>
            </div>
          </div>
          <div v-if="liveProgress?.total" class="space-y-2">
            <div class="space-y-1">
              <div class="flex justify-between text-xs text-forge-text-muted">
                <span>{{ liveProgress.scraped || 0 }} / {{ liveProgress.total }}</span>
                <span>{{ Math.round(((liveProgress.scraped || 0) / liveProgress.total) * 100) }}%</span>
              </div>
              <div class="h-2.5 bg-forge-bg rounded-full overflow-hidden">
                <div
                  class="h-full rounded-full transition-all duration-500 ease-out"
                  :class="liveProgress.complete ? 'bg-forge-accent' : 'bg-forge-accent/80'"
                  :style="{ width: `${Math.min(((liveProgress.scraped || 0) / liveProgress.total) * 100, 100)}%` }"
                ></div>
              </div>
            </div>
            <div class="flex gap-4 text-xs">
              <span class="text-forge-accent">{{ liveProgress.scraped || 0 }} scraped</span>
              <span v-if="liveProgress.failed" class="text-forge-danger">{{ liveProgress.failed }} failed</span>
              <span v-if="liveProgress.skipped" class="text-forge-text-muted">{{ liveProgress.skipped }} skipped</span>
            </div>
            <p v-if="liveProgress.scraped > 0 && !liveProgress.complete && liveProgress.total > liveProgress.scraped" class="text-xs text-forge-text-muted">
              ~{{ estimateTimeRemaining(liveProgress) }} remaining
            </p>
          </div>
        </div>

        <!-- ── Sync status summary ──────────────────────────── -->
        <div v-if="pluginStatus && !liveProgress" class="bg-forge-card border border-forge-border rounded-xl p-5">
          <div class="flex items-center justify-between mb-3">
            <h3 class="text-sm font-semibold text-forge-text-muted uppercase">Sync Status</h3>
            <button
              @click="toggleHistory"
              class="text-xs text-forge-text-muted hover:text-forge-text flex items-center gap-1 transition-colors"
            >
              <span>{{ showHistory ? '▼' : '▶' }}</span>
              <span>History</span>
            </button>
          </div>
          <div class="grid grid-cols-2 sm:grid-cols-4 gap-4 text-sm">
            <div>
              <p class="text-xs text-forge-text-muted uppercase">Status</p>
              <p :class="['font-medium capitalize', statusColor(selectedPlugin)]">{{ statusLabel(selectedPlugin) }}</p>
            </div>
            <div>
              <p class="text-xs text-forge-text-muted uppercase">Last Sync</p>
              <p class="text-forge-text">{{ formatDate(pluginStatus.lastSyncAt || selectedPlugin.lastSyncAt || selectedPlugin.lastSync) }}</p>
            </div>
            <div>
              <p class="text-xs text-forge-text-muted uppercase">Next Sync</p>
              <p class="text-forge-text">{{ formatDate(pluginStatus.nextSyncAt || selectedPlugin.nextSyncAt || selectedPlugin.nextSync) }}</p>
            </div>
            <div v-if="pluginStatus.scrapedModels || pluginStatus.totalModels">
              <p class="text-xs text-forge-text-muted uppercase">Last Run</p>
              <p class="text-forge-text">{{ pluginStatus.scrapedModels || 0 }} / {{ pluginStatus.totalModels || 0 }} models</p>
            </div>
          </div>
          <p v-if="pluginStatus.error" class="mt-3 text-xs text-forge-danger bg-forge-danger/10 rounded-lg px-3 py-2">
            {{ pluginStatus.error }}
          </p>

          <!-- Sync history table -->
          <div v-if="showHistory" class="mt-4 border-t border-forge-border pt-3">
            <div v-if="loadingHistory" class="flex justify-center py-4">
              <div class="w-5 h-5 border-2 border-forge-accent border-t-transparent rounded-full animate-spin"></div>
            </div>
            <div v-else-if="!syncHistory.length" class="text-xs text-forge-text-muted text-center py-2">No sync history</div>
            <table v-else class="w-full text-xs">
              <thead>
                <tr class="text-forge-text-muted uppercase text-left">
                  <th class="pb-1 pr-3">Date</th>
                  <th class="pb-1 pr-3">Status</th>
                  <th class="pb-1 pr-3">Scraped</th>
                  <th class="pb-1 pr-3">Failed</th>
                  <th class="pb-1">Duration</th>
                </tr>
              </thead>
              <tbody>
                <tr v-for="run in syncHistory" :key="run.id" class="border-t border-forge-border/50">
                  <td class="py-1 pr-3 text-forge-text-muted">{{ formatDate(run.startedAt) }}</td>
                  <td class="py-1 pr-3" :class="syncRunStatusColor(run.status)">{{ run.status }}</td>
                  <td class="py-1 pr-3 text-forge-text">{{ run.scrapedModels ?? '—' }}</td>
                  <td class="py-1 pr-3" :class="run.failedModels ? 'text-forge-danger' : 'text-forge-text-muted'">
                    {{ run.failedModels ?? '—' }}
                  </td>
                  <td class="py-1 text-forge-text-muted">{{ formatDuration(run.durationSeconds) }}</td>
                </tr>
              </tbody>
            </table>
          </div>
        </div>

        <!-- ── Config form ──────────────────────────────────── -->
        <div v-if="configSchema.length" class="bg-forge-card border border-forge-border rounded-xl p-5">
          <h3 class="text-sm font-semibold text-forge-text-muted uppercase mb-4">Configuration</h3>
          <div class="space-y-4">
            <div v-for="field in configSchema" :key="field.key">
              <label class="block text-xs font-medium text-forge-text-muted mb-1 uppercase">
                {{ field.label || field.key }}
                <span v-if="field.required" class="text-forge-danger">*</span>
              </label>

              <!-- Boolean -->
              <label v-if="field.type === 'boolean'" class="flex items-center gap-2 cursor-pointer">
                <input
                  type="checkbox"
                  :checked="configValues[field.key]"
                  @change="configValues[field.key] = $event.target.checked; onConfigChange()"
                  class="rounded border-forge-border bg-forge-bg text-forge-accent focus:ring-forge-accent"
                />
                <span class="text-sm text-forge-text">{{ field.description || '' }}</span>
              </label>

              <!-- Number -->
              <input
                v-else-if="field.type === 'number' || field.type === 'integer'"
                type="number"
                :value="configValues[field.key]"
                @input="configValues[field.key] = parseFloat($event.target.value) || 0; onConfigChange()"
                :placeholder="field.placeholder || field.defaultValue?.toString() || ''"
                :min="field.min" :max="field.max"
                :step="field.step || (field.type === 'integer' ? 1 : 'any')"
                class="w-full bg-forge-bg border border-forge-border rounded-lg px-3 py-1.5 text-sm text-forge-text placeholder-forge-text-muted focus:outline-none focus:border-forge-accent"
              />

              <!-- Select -->
              <select
                v-else-if="field.options?.length"
                :value="configValues[field.key]"
                @change="configValues[field.key] = $event.target.value; onConfigChange()"
                class="w-full bg-forge-bg border border-forge-border rounded-lg px-3 py-1.5 text-sm text-forge-text focus:outline-none focus:border-forge-accent"
              >
                <option v-for="opt in field.options" :key="opt.value || opt" :value="opt.value || opt">
                  {{ opt.label || opt }}
                </option>
              </select>

              <!-- Secret -->
              <input
                v-else-if="field.isSecret || field.secret || field.type === 'secret'"
                type="password"
                :value="configValues[field.key]"
                @input="configValues[field.key] = $event.target.value; onConfigChange()"
                :placeholder="field.placeholder || '••••••••'"
                autocomplete="off"
                class="w-full bg-forge-bg border border-forge-border rounded-lg px-3 py-1.5 text-sm text-forge-text placeholder-forge-text-muted focus:outline-none focus:border-forge-accent"
              />

              <!-- Text (default) -->
              <input
                v-else
                type="text"
                :value="configValues[field.key]"
                @input="configValues[field.key] = $event.target.value; onConfigChange()"
                :placeholder="field.placeholder || field.defaultValue?.toString() || ''"
                class="w-full bg-forge-bg border border-forge-border rounded-lg px-3 py-1.5 text-sm text-forge-text placeholder-forge-text-muted focus:outline-none focus:border-forge-accent"
              />

              <p v-if="field.helpText || (field.description && field.type !== 'boolean')" class="text-xs text-forge-text-muted mt-1">
                {{ field.helpText || field.description }}
              </p>
            </div>
          </div>

          <button
            @click="saveConfig"
            :disabled="savingConfig || !configDirty"
            :class="[
              'mt-5 w-full py-2.5 rounded-lg font-medium text-sm transition-colors',
              saveConfigSuccess
                ? 'bg-forge-accent text-forge-bg'
                : configDirty
                  ? 'bg-forge-accent hover:bg-forge-accent-hover text-forge-bg'
                  : 'bg-forge-card text-forge-text-muted cursor-not-allowed border border-forge-border',
              savingConfig && 'opacity-50 cursor-not-allowed',
            ]"
          >
            {{ savingConfig ? 'Saving...' : saveConfigSuccess ? '✓ Saved!' : configDirty ? 'Save Configuration' : 'No Changes' }}
          </button>
        </div>

        <!-- ── Schedule config ──────────────────────────────── -->
        <div v-if="pluginConfig?.schedule != null || selectedPlugin?.syncInterval" class="bg-forge-card border border-forge-border rounded-xl p-5">
          <h3 class="text-sm font-semibold text-forge-text-muted uppercase mb-3">Sync Schedule</h3>
          <div class="text-sm text-forge-text">
            <span v-if="selectedPlugin?.syncInterval">Every {{ selectedPlugin.syncInterval }}</span>
            <span v-else-if="pluginConfig?.schedule">{{ pluginConfig.schedule }}</span>
            <span v-else class="text-forge-text-muted">Manual sync only</span>
          </div>
        </div>

        <!-- ── Plugin admin page embed ──────────────────────── -->
        <div v-if="adminHtml" class="bg-forge-card border border-forge-border rounded-xl p-5">
          <h3 class="text-sm font-semibold text-forge-text-muted uppercase mb-3">Plugin Admin</h3>
          <div class="prose prose-invert max-w-none text-sm" v-html="adminHtml"></div>
        </div>

        <!-- ── Diagnostics ──────────────────────────────────── -->
        <div class="bg-forge-card border border-forge-border rounded-xl overflow-hidden">
          <button
            @click="toggleDiagnostics"
            class="w-full px-5 py-3 flex items-center justify-between text-sm font-semibold text-forge-text-muted hover:text-forge-text transition-colors"
          >
            <span class="uppercase">🔬 Diagnostics</span>
            <span>{{ showDiagnostics ? '▼' : '▶' }}</span>
          </button>

          <div v-if="showDiagnostics" class="border-t border-forge-border p-5 space-y-3">
            <div v-if="loadingDiagnostics" class="flex justify-center py-4">
              <div class="w-5 h-5 border-2 border-forge-accent border-t-transparent rounded-full animate-spin"></div>
            </div>
            <div v-else-if="!diagnostics" class="text-xs text-forge-text-muted text-center py-2">
              Diagnostics unavailable
            </div>
            <template v-else>
              <div class="grid grid-cols-1 sm:grid-cols-2 gap-3 text-xs">
                <div>
                  <p class="text-forge-text-muted uppercase mb-0.5">Assembly</p>
                  <code class="text-forge-text break-all">{{ diagnostics.assemblyName }}</code>
                </div>
                <div>
                  <p class="text-forge-text-muted uppercase mb-0.5">DLL Path</p>
                  <code class="text-forge-text-muted break-all">{{ diagnostics.dllPath || '(in-memory)' }}</code>
                </div>
                <div>
                  <p class="text-forge-text-muted uppercase mb-0.5">Source Directory</p>
                  <code class="text-forge-text-muted break-all">{{ diagnostics.sourceDirectory || '—' }}</code>
                </div>
                <div>
                  <p class="text-forge-text-muted uppercase mb-0.5">Loaded At</p>
                  <span class="text-forge-text">{{ formatDate(diagnostics.loadedAt) }}</span>
                </div>
                <div>
                  <p class="text-forge-text-muted uppercase mb-0.5">Source</p>
                  <span class="text-forge-text capitalize">{{ diagnostics.source || '—' }}</span>
                </div>
              </div>

              <!-- Manifest validation detail -->
              <div v-if="diagnostics.validation" class="mt-2">
                <p class="text-xs text-forge-text-muted uppercase mb-1">Manifest Validation</p>
                <p class="text-xs" :class="diagnostics.validation.isValid ? 'text-forge-accent' : 'text-forge-danger'">
                  {{ diagnostics.validation.isValid ? '✓ Valid' : '✗ Invalid' }}
                </p>
                <p v-for="e in diagnostics.validation.errors" :key="e" class="text-xs text-forge-danger mt-0.5">⛔ {{ e }}</p>
                <p v-for="w in diagnostics.validation.warnings" :key="w" class="text-xs text-yellow-400 mt-0.5">⚠️ {{ w }}</p>
              </div>

              <!-- SDK compat detail -->
              <div v-if="diagnostics.sdkCompat" class="mt-2">
                <p class="text-xs text-forge-text-muted uppercase mb-1">SDK Compatibility</p>
                <p class="text-xs" :class="{
                  'text-forge-accent': diagnostics.sdkCompat.level === 'Compatible',
                  'text-yellow-400': diagnostics.sdkCompat.level === 'MinorMismatch',
                  'text-forge-danger': diagnostics.sdkCompat.level === 'MajorMismatch',
                  'text-forge-text-muted': diagnostics.sdkCompat.level === 'Unknown',
                }">
                  {{ diagnostics.sdkCompat.level }}
                  <span v-if="diagnostics.sdkCompat.reason" class="ml-2 text-forge-text-muted">— {{ diagnostics.sdkCompat.reason }}</span>
                </p>
              </div>
            </template>
          </div>
        </div>

        <!-- Error -->
        <p v-if="api.error.value" class="text-xs text-forge-danger bg-forge-danger/10 rounded-lg px-3 py-2">
          {{ api.error.value }}
        </p>
      </div>

      <!-- No plugin selected -->
      <div v-else class="flex-1 flex items-center justify-center py-20">
        <p class="text-forge-text-muted">Select a plugin from the list</p>
      </div>
    </div>

    </div> <!-- end activeTab === 'installed' -->
  </div>
</template>

<!--
  PluginsView.vue — Plugin admin interface (WP13)
  List installed plugins, per-plugin config editor (dynamic from ConfigSchema),
  sync controls, auth flow, admin page embed, sync progress
-->
<script setup>
import { ref, computed, onMounted, onBeforeUnmount } from 'vue'
import { useApi } from '../composables/useApi.js'

const api = useApi()

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

// Editing config values (keyed by field key)
const configValues = ref({})

const selectedPlugin = computed(() =>
  plugins.value.find((p) => p.slug === selectedSlug.value || p.sourceSlug === selectedSlug.value)
)

function statusLabel(plugin) {
  if (!plugin) return 'unknown'
  if (plugin.syncRunning || plugin.isSyncing) return 'syncing'
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
  try {
    await api.triggerPluginSync(slug)
    startStatusPoll(slug)
  } catch { syncingSlug.value = null }
}

function startStatusPoll(slug) {
  stopStatusPoll()
  statusPollTimer.value = setInterval(async () => {
    await fetchPluginStatus(slug)
    await fetchPlugins()
    const plugin = plugins.value.find((p) => (p.slug || p.sourceSlug) === slug)
    if (!plugin?.syncRunning && !plugin?.isSyncing) {
      stopStatusPoll()
      syncingSlug.value = null
    }
  }, 3000)
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
    // Call the auth endpoint to get the OAuth URL
    const res = await fetch(`/api/v1/plugins/${slug}/auth${force ? '?force=true' : ''}`)
    const data = await res.json()
    
    if (data.authenticated && !force && !data.authUrl) {
      // Already authenticated and no re-auth requested
      await fetchPlugins()
      return
    }
    
    if (data.authUrl) {
      // Open OAuth URL in popup
      window.open(data.authUrl, '_blank', 'width=600,height=700')
    } else {
      console.error('No auth URL returned:', data.message)
      return
    }
  } catch (err) {
    // Fallback to direct URL
    window.open(`/api/v1/plugins/${slug}/auth`, '_blank', 'width=600,height=700')
  }
  
  // Poll for auth completion
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
  // Stop polling after 5 minutes
  setTimeout(() => clearInterval(pollInterval), 300000)
}

function onConfigChange() {
  configDirty.value = true
}

// ─── Config field rendering helpers ──────────────────────
function fieldType(field) {
  if (field.isSecret || field.secret) return 'password'
  if (field.type === 'number' || field.type === 'integer') return 'number'
  if (field.type === 'boolean') return 'checkbox'
  return 'text'
}

onMounted(fetchPlugins)
onBeforeUnmount(stopStatusPoll)
</script>

<template>
  <div>
    <h1 class="text-2xl font-bold text-forge-text mb-6">Plugins</h1>

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
                  {{ plugin.sourceName || plugin.name || plugin.slug || plugin.sourceSlug }}
                </h3>
                <p class="text-xs text-forge-text-muted mt-0.5">v{{ plugin.version || '1.0' }}</p>
              </div>
              <div class="flex items-center gap-2 shrink-0">
                <div :class="['w-2 h-2 rounded-full', statusDot(plugin)]"></div>
                <span :class="['text-xs capitalize', statusColor(plugin)]">{{ statusLabel(plugin) }}</span>
              </div>
            </div>
            <div class="flex gap-4 mt-1 text-xs text-forge-text-muted">
              <span v-if="plugin.lastSyncAt || plugin.lastSync">
                Last: {{ formatDate(plugin.lastSyncAt || plugin.lastSync) }}
              </span>
              <span v-if="plugin.nextSyncAt || plugin.nextSync">
                Next: {{ formatDate(plugin.nextSyncAt || plugin.nextSync) }}
              </span>
            </div>
          </div>
        </div>
      </div>

      <!-- Plugin detail -->
      <div v-if="selectedPlugin" class="flex-1 space-y-6">
        <!-- Plugin header -->
        <div class="bg-forge-card border border-forge-border rounded-xl p-5">
          <div class="flex items-start justify-between">
            <div>
              <h2 class="text-xl font-bold text-forge-text">
                {{ selectedPlugin.sourceName || selectedPlugin.name || selectedSlug }}
              </h2>
              <p v-if="selectedPlugin.description" class="text-sm text-forge-text-muted mt-1">
                {{ selectedPlugin.description }}
              </p>
              <div class="flex items-center gap-4 mt-3 text-sm">
                <span class="text-forge-text-muted">Version: <strong class="text-forge-text">{{ selectedPlugin.version || '1.0' }}</strong></span>
                <span class="text-forge-text-muted">Source slug: <code class="text-forge-accent">{{ selectedPlugin.sourceSlug || selectedPlugin.slug }}</code></span>
              </div>
            </div>

            <div class="flex items-center gap-2">
              <!-- Auth button -->
              <button
                v-if="selectedPlugin.requiresBrowserAuth && !(selectedPlugin.isAuthenticated || selectedPlugin.authenticated)"
                @click="authenticate(selectedPlugin)"
                class="px-4 py-2 bg-forge-danger/15 border border-forge-danger/30 text-forge-danger rounded-lg text-sm font-medium hover:bg-forge-danger/25 transition-colors"
              >
                🔑 Authenticate
              </button>

              <!-- Sync Now button -->
              <button
                @click="triggerSync(selectedSlug)"
                :disabled="syncingSlug === selectedSlug"
                :class="[
                  'px-4 py-2 rounded-lg text-sm font-medium transition-colors',
                  syncingSlug === selectedSlug
                    ? 'bg-forge-card text-forge-text-muted cursor-not-allowed'
                    : 'bg-forge-accent hover:bg-forge-accent-hover text-forge-bg',
                ]"
              >
                {{ syncingSlug === selectedSlug ? '🔄 Syncing...' : '🔄 Sync Now' }}
              </button>
            </div>
          </div>
        </div>

        <!-- Sync status / progress -->
        <div
          v-if="pluginStatus?.syncProgress || syncingSlug === selectedSlug"
          class="bg-forge-card border border-forge-accent/30 rounded-xl p-4"
        >
          <div class="flex items-center gap-3 mb-3">
            <div class="w-5 h-5 border-2 border-forge-accent border-t-transparent rounded-full animate-spin shrink-0"></div>
            <span class="text-sm font-medium text-forge-text">
              {{ pluginStatus?.syncProgress?.status || 'Sync in progress...' }}
            </span>
          </div>
          <div v-if="pluginStatus?.syncProgress" class="space-y-2">
            <!-- Progress bar -->
            <div v-if="pluginStatus.syncProgress.totalItems" class="space-y-1">
              <div class="flex justify-between text-xs text-forge-text-muted">
                <span>{{ pluginStatus.syncProgress.processedItems || 0 }} / {{ pluginStatus.syncProgress.totalItems }}</span>
                <span>{{ Math.round(((pluginStatus.syncProgress.processedItems || 0) / pluginStatus.syncProgress.totalItems) * 100) }}%</span>
              </div>
              <div class="h-2 bg-forge-bg rounded-full overflow-hidden">
                <div
                  class="h-full bg-forge-accent rounded-full transition-all duration-300"
                  :style="{ width: `${((pluginStatus.syncProgress.processedItems || 0) / pluginStatus.syncProgress.totalItems) * 100}%` }"
                ></div>
              </div>
            </div>
            <!-- Stats -->
            <div class="flex gap-4 text-xs text-forge-text-muted">
              <span v-if="pluginStatus.syncProgress.newModels">{{ pluginStatus.syncProgress.newModels }} new</span>
              <span v-if="pluginStatus.syncProgress.updatedModels">{{ pluginStatus.syncProgress.updatedModels }} updated</span>
              <span v-if="pluginStatus.syncProgress.skippedModels">{{ pluginStatus.syncProgress.skippedModels }} skipped</span>
              <span v-if="pluginStatus.syncProgress.failedModels">{{ pluginStatus.syncProgress.failedModels }} failed</span>
            </div>
          </div>
        </div>

        <!-- Sync history summary -->
        <div v-if="pluginStatus && !pluginStatus.syncProgress" class="bg-forge-card border border-forge-border rounded-xl p-5">
          <h3 class="text-sm font-semibold text-forge-text-muted uppercase mb-3">Sync Status</h3>
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
            <div v-if="pluginStatus.lastSyncResult">
              <p class="text-xs text-forge-text-muted uppercase">Last Result</p>
              <p :class="pluginStatus.lastSyncResult === 'success' ? 'text-forge-accent' : 'text-forge-danger'" class="font-medium capitalize">
                {{ pluginStatus.lastSyncResult }}
              </p>
            </div>
          </div>
          <p v-if="pluginStatus.lastSyncError" class="mt-3 text-xs text-forge-danger bg-forge-danger/10 rounded-lg px-3 py-2">
            {{ pluginStatus.lastSyncError }}
          </p>
        </div>

        <!-- Config form (auto-generated from schema) -->
        <div v-if="pluginConfig?.schema?.length" class="bg-forge-card border border-forge-border rounded-xl p-5">
          <h3 class="text-sm font-semibold text-forge-text-muted uppercase mb-4">Configuration</h3>

          <div class="space-y-4">
            <div v-for="field in pluginConfig.schema" :key="field.key">
              <label class="block text-xs font-medium text-forge-text-muted mb-1 uppercase">
                {{ field.label || field.key }}
                <span v-if="field.required" class="text-forge-danger">*</span>
              </label>

              <!-- Boolean (checkbox) -->
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
                :min="field.min"
                :max="field.max"
                :step="field.step || (field.type === 'integer' ? 1 : 'any')"
                class="w-full bg-forge-bg border border-forge-border rounded-lg px-3 py-1.5 text-sm text-forge-text placeholder-forge-text-muted focus:outline-none focus:border-forge-accent"
              />

              <!-- Select (if options provided) -->
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

              <!-- Secret (password) -->
              <input
                v-else-if="field.isSecret || field.secret"
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

              <p v-if="field.description && field.type !== 'boolean'" class="text-xs text-forge-text-muted mt-1">
                {{ field.description }}
              </p>
            </div>
          </div>

          <!-- Save config button -->
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

        <!-- Schedule config -->
        <div v-if="pluginConfig?.schedule != null || selectedPlugin?.syncInterval" class="bg-forge-card border border-forge-border rounded-xl p-5">
          <h3 class="text-sm font-semibold text-forge-text-muted uppercase mb-3">Sync Schedule</h3>
          <div class="text-sm text-forge-text">
            <span v-if="selectedPlugin?.syncInterval">Every {{ selectedPlugin.syncInterval }}</span>
            <span v-else-if="pluginConfig?.schedule">{{ pluginConfig.schedule }}</span>
            <span v-else class="text-forge-text-muted">Manual sync only</span>
          </div>
        </div>

        <!-- Plugin admin page embed -->
        <div v-if="adminHtml" class="bg-forge-card border border-forge-border rounded-xl p-5">
          <h3 class="text-sm font-semibold text-forge-text-muted uppercase mb-3">Plugin Admin</h3>
          <div class="prose prose-invert max-w-none text-sm" v-html="adminHtml"></div>
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
  </div>
</template>

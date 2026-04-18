<!--
  SettingsView.vue — Centralized configuration viewer
  READ-ONLY: shows current config from appsettings.json / env vars.
  To change a setting, set the corresponding env var and restart.
-->
<script setup>
import { ref, onMounted } from 'vue'
import { useApi } from '../composables/useApi.js'

const api = useApi()
const settings = ref(null)
const scanTriggered = ref(false)
const importTriggered = ref(false)
const actionMessage = ref(null)
const actionError = ref(null)
const tooltipVisible = ref(null)

async function fetchSettings() {
  try {
    settings.value = await api.getSettings()
  } catch {
    settings.value = null
  }
}

async function triggerScan() {
  scanTriggered.value = true
  actionMessage.value = null
  actionError.value = null
  try {
    await api.triggerScan()
    actionMessage.value = '✓ Full scan triggered'
  } catch (e) {
    actionError.value = `Scan failed: ${e.message}`
  } finally {
    setTimeout(() => { scanTriggered.value = false; actionMessage.value = null }, 4000)
  }
}

async function triggerImport() {
  importTriggered.value = true
  actionMessage.value = null
  actionError.value = null
  try {
    await api.processAllImports()
    actionMessage.value = '✓ Import scan triggered'
  } catch (e) {
    actionError.value = `Import scan failed: ${e.message}`
  } finally {
    setTimeout(() => { importTriggered.value = false; actionMessage.value = null }, 4000)
  }
}

function formatUptime(seconds) {
  if (!seconds) return '—'
  const d = Math.floor(seconds / 86400)
  const h = Math.floor((seconds % 86400) / 3600)
  const m = Math.floor((seconds % 3600) / 60)
  const s = seconds % 60
  const parts = []
  if (d > 0) parts.push(`${d}d`)
  if (h > 0) parts.push(`${h}h`)
  if (m > 0) parts.push(`${m}m`)
  parts.push(`${s}s`)
  return parts.join(' ')
}

function thumbnailPct(s) {
  if (!s || !s.thumbnailsTotal) return 0
  return Math.round((s.thumbnailsGenerated / s.thumbnailsTotal) * 100)
}

function toggleTooltip(id) {
  tooltipVisible.value = tooltipVisible.value === id ? null : id
}

onMounted(fetchSettings)
</script>

<template>
  <div class="max-w-4xl mx-auto">

    <!-- Header -->
    <div class="flex items-center justify-between mb-6">
      <div>
        <h1 class="text-2xl font-bold text-forge-text flex items-center gap-2">
          ⚙️ Settings
        </h1>
        <p class="text-sm text-forge-text-muted mt-1">
          Current configuration (read-only). Set environment variables and restart to change values.
        </p>
      </div>
      <button
        @click="fetchSettings"
        class="px-3 py-1.5 text-sm rounded-md bg-forge-card border border-forge-border text-forge-text-muted hover:text-forge-text transition-colors"
      >
        🔄 Refresh
      </button>
    </div>

    <!-- Action feedback -->
    <div v-if="actionMessage" class="mb-4 px-4 py-2 bg-green-900/30 border border-green-600/50 rounded-lg text-green-400 text-sm">
      {{ actionMessage }}
    </div>
    <div v-if="actionError" class="mb-4 px-4 py-2 bg-red-900/30 border border-red-600/50 rounded-lg text-red-400 text-sm">
      {{ actionError }}
    </div>

    <!-- Loading -->
    <div v-if="api.loading.value && !settings" class="flex justify-center py-20">
      <div class="w-8 h-8 border-2 border-forge-accent border-t-transparent rounded-full animate-spin"></div>
    </div>

    <!-- Error / not loaded -->
    <div v-else-if="!settings" class="text-center py-20 text-forge-text-muted">
      <p class="text-4xl mb-4">⚙️</p>
      <p>Failed to load settings. Is the API running?</p>
    </div>

    <!-- Settings panels -->
    <div v-else class="space-y-6">

      <!-- ───────────────── Library ───────────────── -->
      <section class="bg-forge-card border border-forge-border rounded-xl overflow-hidden">
        <div class="px-5 py-3 border-b border-forge-border bg-forge-bg/40">
          <h2 class="text-sm font-semibold text-forge-text flex items-center gap-2">📂 Library</h2>
        </div>
        <div class="divide-y divide-forge-border/50">

          <div class="px-5 py-3 flex items-start justify-between gap-4">
            <div class="flex items-center gap-1.5 min-w-0">
              <span class="text-sm text-forge-text-muted whitespace-nowrap">Base Paths</span>
              <EnvTooltip id="basePaths" env="Storage__BasePaths__0" hint="Root directories for your 3D model library" :visible="tooltipVisible === 'basePaths'" @toggle="toggleTooltip('basePaths')" />
            </div>
            <div class="flex flex-wrap gap-1 justify-end">
              <span
                v-for="p in settings.basePaths"
                :key="p"
                class="font-mono text-xs bg-forge-bg px-2 py-0.5 rounded text-forge-text border border-forge-border"
              >{{ p }}</span>
              <span v-if="!settings.basePaths?.length" class="text-forge-text-muted text-sm">None</span>
            </div>
          </div>

          <div class="px-5 py-3 flex items-center justify-between gap-4">
            <div class="flex items-center gap-1.5">
              <span class="text-sm text-forge-text-muted">Thumbnail Directory</span>
              <EnvTooltip id="thumbDir" env="Storage__ThumbnailDir" hint="Relative path inside each base path where thumbnails are stored" :visible="tooltipVisible === 'thumbDir'" @toggle="toggleTooltip('thumbDir')" />
            </div>
            <span class="font-mono text-sm text-forge-text">{{ settings.thumbnailDir }}</span>
          </div>

        </div>
      </section>

      <!-- ───────────────── Scanner ───────────────── -->
      <section class="bg-forge-card border border-forge-border rounded-xl overflow-hidden">
        <div class="px-5 py-3 border-b border-forge-border bg-forge-bg/40 flex items-center justify-between">
          <h2 class="text-sm font-semibold text-forge-text flex items-center gap-2">🔍 Scanner</h2>
          <button
            @click="triggerScan"
            :disabled="scanTriggered"
            class="px-3 py-1 text-xs rounded bg-forge-accent/20 border border-forge-accent/40 text-forge-accent hover:bg-forge-accent/30 disabled:opacity-50 transition-colors"
          >
            {{ scanTriggered ? '⏳ Scanning…' : '▶ Scan Now' }}
          </button>
        </div>
        <div class="divide-y divide-forge-border/50">

          <div class="px-5 py-3 flex items-center justify-between gap-4">
            <div class="flex items-center gap-1.5">
              <span class="text-sm text-forge-text-muted">Scan Interval</span>
              <EnvTooltip id="scanInterval" env="Scanner__IntervalHours" hint="How often to automatically re-scan the library" :visible="tooltipVisible === 'scanInterval'" @toggle="toggleTooltip('scanInterval')" />
            </div>
            <span class="text-sm font-medium text-forge-text bg-forge-bg px-2 py-0.5 rounded border border-forge-border">{{ settings.scanIntervalHours }}h</span>
          </div>

          <div class="px-5 py-3 flex items-center justify-between gap-4">
            <div class="flex items-center gap-1.5">
              <span class="text-sm text-forge-text-muted">Scan on Startup</span>
              <EnvTooltip id="scanOnStart" env="Scanner__ScanOnStartup" hint="Run a full scan when the service starts" :visible="tooltipVisible === 'scanOnStart'" @toggle="toggleTooltip('scanOnStart')" />
            </div>
            <BoolBadge :value="settings.scanOnStartup" />
          </div>

          <div class="px-5 py-3 flex items-start justify-between gap-4">
            <div class="flex items-center gap-1.5 min-w-0">
              <span class="text-sm text-forge-text-muted whitespace-nowrap">File Types</span>
              <EnvTooltip id="fileTypes" env="Scanner__FileTypes" hint="File extensions recognized as 3D model files" :visible="tooltipVisible === 'fileTypes'" @toggle="toggleTooltip('fileTypes')" />
            </div>
            <div class="flex flex-wrap gap-1 justify-end">
              <span
                v-for="ext in settings.scanFileTypes"
                :key="ext"
                class="font-mono text-xs bg-forge-bg px-2 py-0.5 rounded text-forge-accent border border-forge-border"
              >.{{ ext }}</span>
            </div>
          </div>

        </div>
      </section>

      <!-- ───────────────── Import ───────────────── -->
      <section class="bg-forge-card border border-forge-border rounded-xl overflow-hidden">
        <div class="px-5 py-3 border-b border-forge-border bg-forge-bg/40 flex items-center justify-between">
          <h2 class="text-sm font-semibold text-forge-text flex items-center gap-2">📥 Import</h2>
          <button
            @click="triggerImport"
            :disabled="importTriggered"
            class="px-3 py-1 text-xs rounded bg-forge-accent/20 border border-forge-accent/40 text-forge-accent hover:bg-forge-accent/30 disabled:opacity-50 transition-colors"
          >
            {{ importTriggered ? '⏳ Scanning…' : '▶ Scan All Now' }}
          </button>
        </div>
        <div class="divide-y divide-forge-border/50">

          <div class="px-5 py-3 flex items-start justify-between gap-4">
            <div class="flex items-center gap-1.5 min-w-0">
              <span class="text-sm text-forge-text-muted whitespace-nowrap">Watch Directories</span>
              <EnvTooltip id="watchDirs" env="Import__WatchDirectories__0" hint="Directories monitored for new files to import" :visible="tooltipVisible === 'watchDirs'" @toggle="toggleTooltip('watchDirs')" />
            </div>
            <div v-if="settings.watchDirectories?.length" class="flex flex-wrap gap-1 justify-end">
              <span
                v-for="d in settings.watchDirectories"
                :key="d"
                class="font-mono text-xs bg-forge-bg px-2 py-0.5 rounded text-forge-text border border-forge-border"
              >{{ d }}</span>
            </div>
            <span v-else class="text-forge-text-muted text-sm italic">None configured</span>
          </div>

          <div class="px-5 py-3 flex items-center justify-between gap-4">
            <div class="flex items-center gap-1.5">
              <span class="text-sm text-forge-text-muted">Auto-Import</span>
              <EnvTooltip id="autoImport" env="Import__AutoImportEnabled" hint="Automatically process files from watch directories on a schedule" :visible="tooltipVisible === 'autoImport'" @toggle="toggleTooltip('autoImport')" />
            </div>
            <BoolBadge :value="settings.autoImportEnabled" />
          </div>

          <div class="px-5 py-3 flex items-center justify-between gap-4">
            <div class="flex items-center gap-1.5">
              <span class="text-sm text-forge-text-muted">Import Interval</span>
              <EnvTooltip id="importInterval" env="Import__IntervalMinutes" hint="How often to run auto-import (if enabled)" :visible="tooltipVisible === 'importInterval'" @toggle="toggleTooltip('importInterval')" />
            </div>
            <span class="text-sm font-medium text-forge-text bg-forge-bg px-2 py-0.5 rounded border border-forge-border">{{ settings.importIntervalMinutes }} min</span>
          </div>

        </div>
      </section>

      <!-- ───────────────── Thumbnails ───────────────── -->
      <section class="bg-forge-card border border-forge-border rounded-xl overflow-hidden">
        <div class="px-5 py-3 border-b border-forge-border bg-forge-bg/40">
          <h2 class="text-sm font-semibold text-forge-text flex items-center gap-2">🖼️ Thumbnails</h2>
        </div>
        <div class="divide-y divide-forge-border/50">

          <div class="px-5 py-3 flex items-center justify-between gap-4">
            <div class="flex items-center gap-1.5">
              <span class="text-sm text-forge-text-muted">Enabled</span>
              <EnvTooltip id="thumbEnabled" env="Thumbnails__Enabled" hint="Whether thumbnail generation is active" :visible="tooltipVisible === 'thumbEnabled'" @toggle="toggleTooltip('thumbEnabled')" />
            </div>
            <BoolBadge :value="settings.thumbnailsEnabled" />
          </div>

          <div class="px-5 py-3 flex items-center justify-between gap-4">
            <div class="flex items-center gap-1.5">
              <span class="text-sm text-forge-text-muted">Size</span>
              <EnvTooltip id="thumbSize" env="Thumbnails__Size" hint="Thumbnail resolution (e.g. 256x256)" :visible="tooltipVisible === 'thumbSize'" @toggle="toggleTooltip('thumbSize')" />
            </div>
            <span class="font-mono text-sm text-forge-text bg-forge-bg px-2 py-0.5 rounded border border-forge-border">{{ settings.thumbnailSize }}</span>
          </div>

          <div class="px-5 py-3 flex items-center justify-between gap-4">
            <div class="flex items-center gap-1.5">
              <span class="text-sm text-forge-text-muted">Format</span>
              <EnvTooltip id="thumbFormat" env="Thumbnails__Format" hint="Output image format" :visible="tooltipVisible === 'thumbFormat'" @toggle="toggleTooltip('thumbFormat')" />
            </div>
            <span class="font-mono text-sm text-forge-text bg-forge-bg px-2 py-0.5 rounded border border-forge-border">{{ settings.thumbnailFormat }}</span>
          </div>

          <div class="px-5 py-3 flex items-center justify-between gap-4">
            <div class="flex items-center gap-1.5">
              <span class="text-sm text-forge-text-muted">Renderer</span>
              <EnvTooltip id="thumbRenderer" env="Thumbnails__Renderer" hint="CLI tool used to generate 3D thumbnails" :visible="tooltipVisible === 'thumbRenderer'" @toggle="toggleTooltip('thumbRenderer')" />
            </div>
            <span class="font-mono text-sm text-forge-text bg-forge-bg px-2 py-0.5 rounded border border-forge-border">{{ settings.thumbnailRenderer }}</span>
          </div>

          <div class="px-5 py-3 flex items-center justify-between gap-4">
            <span class="text-sm text-forge-text-muted">Generation Progress</span>
            <div class="flex items-center gap-3">
              <span class="text-sm text-forge-text">
                {{ settings.thumbnailsGenerated.toLocaleString() }} / {{ settings.thumbnailsTotal.toLocaleString() }}
                ({{ thumbnailPct(settings) }}%)
              </span>
              <div class="w-24 h-1.5 bg-forge-bg rounded-full overflow-hidden">
                <div
                  class="h-full bg-forge-accent rounded-full transition-all"
                  :style="{ width: `${thumbnailPct(settings)}%` }"
                />
              </div>
            </div>
          </div>

        </div>
      </section>

      <!-- ───────────────── Search ───────────────── -->
      <section class="bg-forge-card border border-forge-border rounded-xl overflow-hidden">
        <div class="px-5 py-3 border-b border-forge-border bg-forge-bg/40">
          <h2 class="text-sm font-semibold text-forge-text flex items-center gap-2">🔍 Search</h2>
        </div>
        <div class="divide-y divide-forge-border/50">

          <div class="px-5 py-3 flex items-center justify-between gap-4">
            <div class="flex items-center gap-1.5">
              <span class="text-sm text-forge-text-muted">Min Trigram Similarity</span>
              <EnvTooltip id="trigramSim" env="Search__MinTrigramSimilarity" hint="Minimum fuzzy-match score (0–1). Lower = broader results." :visible="tooltipVisible === 'trigramSim'" @toggle="toggleTooltip('trigramSim')" />
            </div>
            <span class="font-mono text-sm text-forge-text bg-forge-bg px-2 py-0.5 rounded border border-forge-border">{{ settings.minTrigramSimilarity }}</span>
          </div>

          <div class="px-5 py-3 flex items-center justify-between gap-4">
            <div class="flex items-center gap-1.5">
              <span class="text-sm text-forge-text-muted">Results Per Page</span>
              <EnvTooltip id="resultsPerPage" env="Search__ResultsPerPage" hint="Default number of models returned per search page" :visible="tooltipVisible === 'resultsPerPage'" @toggle="toggleTooltip('resultsPerPage')" />
            </div>
            <span class="font-mono text-sm text-forge-text bg-forge-bg px-2 py-0.5 rounded border border-forge-border">{{ settings.resultsPerPage }}</span>
          </div>

        </div>
      </section>

      <!-- ───────────────── Plugins ───────────────── -->
      <section class="bg-forge-card border border-forge-border rounded-xl overflow-hidden">
        <div class="px-5 py-3 border-b border-forge-border bg-forge-bg/40">
          <h2 class="text-sm font-semibold text-forge-text flex items-center gap-2">🔌 Plugins</h2>
        </div>
        <div class="divide-y divide-forge-border/50">

          <div class="px-5 py-3 flex items-center justify-between gap-4">
            <div class="flex items-center gap-1.5">
              <span class="text-sm text-forge-text-muted">Plugins Directory</span>
              <EnvTooltip id="pluginsDir" env="Forgekeeper__PluginsDirectory" hint="Where user-installed plugins are stored" :visible="tooltipVisible === 'pluginsDir'" @toggle="toggleTooltip('pluginsDir')" />
            </div>
            <span class="font-mono text-sm text-forge-text">{{ settings.pluginsDirectory }}</span>
          </div>

          <div class="px-5 py-3 flex items-center justify-between gap-4">
            <div class="flex items-center gap-1.5">
              <span class="text-sm text-forge-text-muted">Sources Directory</span>
              <EnvTooltip id="sourcesDir" env="Forgekeeper__SourcesDirectory" hint="Root directory for plugin source adapters" :visible="tooltipVisible === 'sourcesDir'" @toggle="toggleTooltip('sourcesDir')" />
            </div>
            <span class="font-mono text-sm text-forge-text">{{ settings.sourcesDirectory }}</span>
          </div>

          <div class="px-5 py-3 flex items-center justify-between gap-4">
            <div class="flex items-center gap-1.5">
              <span class="text-sm text-forge-text-muted">Hot Reload</span>
              <EnvTooltip id="hotReload" env="Plugins__HotReloadEnabled" hint="Watch plugin directory and reload on changes (dev mode)" :visible="tooltipVisible === 'hotReload'" @toggle="toggleTooltip('hotReload')" />
            </div>
            <BoolBadge :value="settings.hotReloadEnabled" />
          </div>

          <div class="px-5 py-3 flex items-center justify-between gap-4">
            <span class="text-sm text-forge-text-muted">Plugins Loaded</span>
            <span class="text-sm font-medium text-forge-text bg-forge-bg px-2 py-0.5 rounded border border-forge-border">{{ settings.pluginsLoaded ?? 0 }}</span>
          </div>

          <div class="px-5 py-3 flex items-start justify-between gap-4">
            <div class="flex items-center gap-1.5 min-w-0">
              <span class="text-sm text-forge-text-muted whitespace-nowrap">Registry URL</span>
              <EnvTooltip id="registryUrl" env="Plugins__RegistryUrl" hint="URL of the plugin registry JSON" :visible="tooltipVisible === 'registryUrl'" @toggle="toggleTooltip('registryUrl')" />
            </div>
            <span class="font-mono text-xs text-forge-text break-all text-right max-w-xs">{{ settings.registryUrl }}</span>
          </div>

          <div class="px-5 py-3 flex items-center justify-between gap-4">
            <div class="flex items-center gap-1.5">
              <span class="text-sm text-forge-text-muted">Registry Cache</span>
              <EnvTooltip id="registryCache" env="Plugins__RegistryCacheHours" hint="How long to cache the plugin registry" :visible="tooltipVisible === 'registryCache'" @toggle="toggleTooltip('registryCache')" />
            </div>
            <span class="font-mono text-sm text-forge-text bg-forge-bg px-2 py-0.5 rounded border border-forge-border">{{ settings.registryCacheHours }}h</span>
          </div>

        </div>
      </section>

      <!-- ───────────────── Plugin Auto-Update ───────────────── -->
      <section class="bg-forge-card border border-forge-border rounded-xl overflow-hidden">
        <div class="px-5 py-3 border-b border-forge-border bg-forge-bg/40">
          <h2 class="text-sm font-semibold text-forge-text flex items-center gap-2">🔄 Plugin Auto-Update</h2>
        </div>
        <div class="divide-y divide-forge-border/50">

          <div class="px-5 py-3 flex items-center justify-between gap-4">
            <div class="flex items-center gap-1.5">
              <span class="text-sm text-forge-text-muted">Enabled</span>
              <EnvTooltip id="autoUpdateEnabled" env="Plugins__AutoUpdate__Enabled" hint="Periodically check for and install plugin updates" :visible="tooltipVisible === 'autoUpdateEnabled'" @toggle="toggleTooltip('autoUpdateEnabled')" />
            </div>
            <BoolBadge :value="settings.autoUpdateEnabled" />
          </div>

          <div class="px-5 py-3 flex items-center justify-between gap-4">
            <div class="flex items-center gap-1.5">
              <span class="text-sm text-forge-text-muted">Update Mode</span>
              <EnvTooltip id="autoUpdateMode" env="Plugins__AutoUpdate__Mode" hint='"notify" flags updates in the UI; "auto" installs automatically' :visible="tooltipVisible === 'autoUpdateMode'" @toggle="toggleTooltip('autoUpdateMode')" />
            </div>
            <span class="font-mono text-sm text-forge-text bg-forge-bg px-2 py-0.5 rounded border border-forge-border">{{ settings.autoUpdateMode }}</span>
          </div>

          <div class="px-5 py-3 flex items-center justify-between gap-4">
            <div class="flex items-center gap-1.5">
              <span class="text-sm text-forge-text-muted">Check Interval</span>
              <EnvTooltip id="autoUpdateInterval" env="Plugins__AutoUpdate__IntervalHours" hint="How often to check for plugin updates" :visible="tooltipVisible === 'autoUpdateInterval'" @toggle="toggleTooltip('autoUpdateInterval')" />
            </div>
            <span class="font-mono text-sm text-forge-text bg-forge-bg px-2 py-0.5 rounded border border-forge-border">{{ settings.autoUpdateIntervalHours }}h</span>
          </div>

        </div>
      </section>

      <!-- ───────────────── Security ───────────────── -->
      <section class="bg-forge-card border border-forge-border rounded-xl overflow-hidden">
        <div class="px-5 py-3 border-b border-forge-border bg-forge-bg/40">
          <h2 class="text-sm font-semibold text-forge-text flex items-center gap-2">🔒 Security</h2>
        </div>
        <div class="divide-y divide-forge-border/50">

          <div class="px-5 py-3 flex items-center justify-between gap-4">
            <div class="flex items-center gap-1.5">
              <span class="text-sm text-forge-text-muted">API Key</span>
              <EnvTooltip id="apiKey" env="Security__ApiKey" hint="If set, all API requests must include X-Api-Key header" :visible="tooltipVisible === 'apiKey'" @toggle="toggleTooltip('apiKey')" />
            </div>
            <div class="flex items-center gap-2">
              <BoolBadge
                :value="settings.apiKeyConfigured"
                true-label="Configured ✓"
                false-label="Not set"
              />
              <span v-if="!settings.apiKeyConfigured" class="text-xs text-yellow-500">⚠️ Open access</span>
            </div>
          </div>

        </div>
      </section>

      <!-- ───────────────── System Info ───────────────── -->
      <section class="bg-forge-card border border-forge-border rounded-xl overflow-hidden">
        <div class="px-5 py-3 border-b border-forge-border bg-forge-bg/40">
          <h2 class="text-sm font-semibold text-forge-text flex items-center gap-2">ℹ️ System</h2>
        </div>
        <div class="divide-y divide-forge-border/50">

          <div class="px-5 py-3 flex items-center justify-between gap-4">
            <span class="text-sm text-forge-text-muted">Version</span>
            <span class="font-mono text-sm text-forge-text bg-forge-bg px-2 py-0.5 rounded border border-forge-border">{{ settings.version }}</span>
          </div>

          <div class="px-5 py-3 flex items-center justify-between gap-4">
            <span class="text-sm text-forge-text-muted">.NET Version</span>
            <span class="font-mono text-sm text-forge-text bg-forge-bg px-2 py-0.5 rounded border border-forge-border">{{ settings.dotNetVersion }}</span>
          </div>

          <div class="px-5 py-3 flex items-center justify-between gap-4">
            <div class="flex items-center gap-1.5">
              <span class="text-sm text-forge-text-muted">Environment</span>
              <EnvTooltip id="aspnetEnv" env="ASPNETCORE_ENVIRONMENT" hint="ASP.NET Core hosting environment" :visible="tooltipVisible === 'aspnetEnv'" @toggle="toggleTooltip('aspnetEnv')" />
            </div>
            <span
              class="font-mono text-sm px-2 py-0.5 rounded border"
              :class="settings.environment === 'Production'
                ? 'text-forge-text bg-forge-bg border-forge-border'
                : 'text-forge-accent bg-forge-accent/10 border-forge-accent/40'"
            >{{ settings.environment }}</span>
          </div>

          <div class="px-5 py-3 flex items-center justify-between gap-4">
            <span class="text-sm text-forge-text-muted">Uptime</span>
            <span class="text-sm text-forge-text">{{ formatUptime(settings.uptimeSeconds) }}</span>
          </div>

          <div class="px-5 py-3 flex items-center justify-between gap-4">
            <span class="text-sm text-forge-text-muted">Started At</span>
            <span class="text-sm text-forge-text">
              {{ settings.startedAt ? new Date(settings.startedAt).toLocaleString() : '—' }}
            </span>
          </div>

        </div>
      </section>

      <!-- Config help footer -->
      <div class="bg-forge-card/50 border border-forge-border rounded-xl p-4 text-sm text-forge-text-muted">
        <p class="font-medium text-forge-text mb-1">💡 How to change settings</p>
        <p>
          Settings come from <code class="bg-forge-bg px-1 rounded text-xs font-mono">appsettings.json</code> or environment variables.
          Click the <span class="text-forge-accent font-bold">?</span> next to any row to see the env var name.
          Use double underscore (<code class="bg-forge-bg px-1 rounded text-xs font-mono">__</code>) as separator,
          e.g. <code class="bg-forge-bg px-1 rounded text-xs font-mono">Scanner__IntervalHours=12</code>.
          Restart the service after any change.
        </p>
      </div>

    </div>
  </div>
</template>

<!-- ─── Inline helper components ───────────────────────────── -->

<script>
import { defineComponent, h } from 'vue'

// EnvTooltip — click the "?" to show the env var name + hint
export const EnvTooltip = defineComponent({
  props: {
    id: String,
    env: String,
    hint: String,
    visible: Boolean,
  },
  emits: ['toggle'],
  setup(props, { emit }) {
    return () => {
      return h('div', { class: 'relative inline-block' }, [
        h('button', {
          type: 'button',
          onClick: (e) => { e.stopPropagation(); emit('toggle') },
          class: 'w-4 h-4 rounded-full text-[10px] font-bold bg-forge-bg border border-forge-border text-forge-text-muted hover:text-forge-accent hover:border-forge-accent transition-colors flex items-center justify-center',
        }, '?'),
        props.visible && props.env
          ? h('div', {
              class: 'absolute left-0 top-5 z-50 w-64 bg-forge-surface border border-forge-border rounded-lg shadow-lg p-3 text-left',
            }, [
              h('p', { class: 'text-xs text-forge-text-muted mb-1' }, props.hint),
              h('div', { class: 'flex items-center gap-1 mt-2' }, [
                h('span', { class: 'text-xs text-forge-text-muted' }, 'Env var:'),
                h('code', { class: 'text-xs font-mono bg-forge-bg px-1.5 py-0.5 rounded text-forge-accent border border-forge-border' }, props.env),
              ]),
            ])
          : null,
      ])
    }
  },
})

// BoolBadge — green/red pill for boolean values
export const BoolBadge = defineComponent({
  props: {
    value: Boolean,
    trueLabel: { type: String, default: 'Enabled' },
    falseLabel: { type: String, default: 'Disabled' },
  },
  setup(props) {
    return () => h('span', {
      class: props.value
        ? 'text-xs font-medium px-2 py-0.5 rounded-full bg-green-900/30 text-green-400 border border-green-700/50'
        : 'text-xs font-medium px-2 py-0.5 rounded-full bg-forge-bg text-forge-text-muted border border-forge-border',
    }, props.value ? props.trueLabel : props.falseLabel)
  },
})
</script>

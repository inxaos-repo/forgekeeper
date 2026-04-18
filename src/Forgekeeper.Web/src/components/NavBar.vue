<!--
  NavBar.vue — Top navigation bar
  Logo, page links, global search, mobile hamburger menu
-->
<script setup>
import { ref, onMounted, onBeforeUnmount } from 'vue'
import { RouterLink, useRouter } from 'vue-router'
import { useApi } from '../composables/useApi.js'

const router = useRouter()
const api = useApi()
const globalSearch = ref('')
const mobileMenuOpen = ref(false)
const importPendingCount = ref(0)
const pluginUpdateCount = ref(0)
let importPollTimer = null
let pluginUpdatePollTimer = null

function onGlobalSearch() {
  if (globalSearch.value.trim()) {
    router.push({ name: 'Models', query: { search: globalSearch.value.trim() } })
    mobileMenuOpen.value = false
  }
}

async function pollImportCount() {
  try {
    const result = await api.getImportQueue({ status: 'AwaitingReview' })
    const items = result?.items || result || []
    // Count only pending/awaiting-review items
    importPendingCount.value = Array.isArray(items)
      ? items.filter(i => {
          const s = (i.status || '').toLowerCase()
          return s !== 'autosorted' && s !== 'confirmed' && s !== 'failed'
        }).length
      : (result?.totalCount ?? 0)
  } catch {
    // Silently ignore — nav badge is non-critical
  }
}

async function pollPluginUpdates() {
  try {
    const result = await api.getPluginUpdates()
    pluginUpdateCount.value = result?.count ?? 0
  } catch {
    // Silently ignore — nav badge is non-critical
  }
}

const navLinks = [
  { to: '/', label: 'Models', name: 'Models' },
  { to: '/creators', label: 'Creators', name: 'Creators' },
  { to: '/stats', label: 'Stats', name: 'Stats' },
  { to: '/health', label: 'Health', name: 'Health' },
  { to: '/import', label: 'Import', name: 'Import', badge: 'import' },
  { to: '/sources', label: 'Sources', name: 'Sources' },
  { to: '/plugins', label: 'Plugins', name: 'Plugins', badge: 'plugins' },
]

onMounted(() => {
  pollImportCount()
  importPollTimer = setInterval(pollImportCount, 60_000)  // refresh every 60s
  pollPluginUpdates()
  pluginUpdatePollTimer = setInterval(pollPluginUpdates, 5 * 60_000)  // refresh every 5 min
})

onBeforeUnmount(() => {
  clearInterval(importPollTimer)
  clearInterval(pluginUpdatePollTimer)
})
</script>

<template>
  <nav class="bg-forge-surface border-b border-forge-border sticky top-0 z-50">
    <div class="max-w-7xl mx-auto px-4 sm:px-6 lg:px-8">
      <div class="flex items-center justify-between h-16">
        <!-- Logo + Desktop Links -->
        <div class="flex items-center gap-8">
          <RouterLink to="/" class="text-xl font-bold flex items-center gap-2">
            <span class="text-2xl">⚒️</span>
            <span class="hidden sm:inline">
              <span class="text-forge-accent">Forge</span><span class="text-forge-text">keeper</span>
            </span>
          </RouterLink>

          <div class="hidden md:flex items-center gap-1">
            <RouterLink
              v-for="link in navLinks"
              :key="link.to"
              :to="link.to"
              class="relative px-3 py-2 rounded-md text-sm font-medium text-forge-text-muted hover:text-forge-text hover:bg-forge-card transition-colors"
              active-class="!bg-forge-card !text-forge-accent"
              :exact="link.to === '/'"
            >
              {{ link.label }}
              <span
                v-if="link.badge === 'import' && importPendingCount > 0"
                class="absolute -top-0.5 -right-1 min-w-[1.1rem] h-[1.1rem] flex items-center justify-center bg-forge-accent text-forge-bg text-[10px] font-bold rounded-full px-0.5"
              >
                {{ importPendingCount > 99 ? '99+' : importPendingCount }}
              </span>
              <span
                v-if="link.badge === 'plugins' && pluginUpdateCount > 0"
                class="absolute -top-0.5 -right-1 min-w-[1.1rem] h-[1.1rem] flex items-center justify-center bg-orange-600 text-white text-[10px] font-bold rounded-full px-0.5"
              >
                {{ pluginUpdateCount > 99 ? '99+' : pluginUpdateCount }}
              </span>
            </RouterLink>
          </div>
        </div>

        <!-- Global Search (desktop) -->
        <div class="hidden md:flex items-center">
          <form @submit.prevent="onGlobalSearch" class="relative">
            <input
              v-model="globalSearch"
              type="text"
              placeholder="Search models..."
              class="w-64 bg-forge-bg border border-forge-border rounded-lg px-4 py-2 text-sm text-forge-text placeholder-forge-text-muted focus:outline-none focus:border-forge-accent focus:ring-1 focus:ring-forge-accent transition-colors"
            />
            <button
              type="submit"
              class="absolute right-2 top-1/2 -translate-y-1/2 text-forge-text-muted hover:text-forge-accent"
            >
              🔍
            </button>
          </form>
        </div>

        <!-- Mobile menu button -->
        <button
          @click="mobileMenuOpen = !mobileMenuOpen"
          class="md:hidden p-2 rounded-md text-forge-text-muted hover:text-forge-text hover:bg-forge-card"
        >
          <span v-if="!mobileMenuOpen" class="text-xl">☰</span>
          <span v-else class="text-xl">✕</span>
        </button>
      </div>
    </div>

    <!-- Mobile menu -->
    <div v-if="mobileMenuOpen" class="md:hidden border-t border-forge-border bg-forge-surface">
      <div class="px-4 py-3 space-y-2">
        <form @submit.prevent="onGlobalSearch" class="mb-3">
          <input
            v-model="globalSearch"
            type="text"
            placeholder="Search models..."
            class="w-full bg-forge-bg border border-forge-border rounded-lg px-4 py-2 text-sm text-forge-text placeholder-forge-text-muted focus:outline-none focus:border-forge-accent"
          />
        </form>
        <RouterLink
          v-for="link in navLinks"
          :key="link.to"
          :to="link.to"
          class="relative block px-3 py-2 rounded-md text-sm font-medium text-forge-text-muted hover:text-forge-text hover:bg-forge-card"
          active-class="!bg-forge-card !text-forge-accent"
          :exact="link.to === '/'"
          @click="mobileMenuOpen = false"
        >
          {{ link.label }}
          <span
            v-if="link.badge === 'import' && importPendingCount > 0"
            class="ml-1.5 inline-flex items-center justify-center min-w-[1.2rem] h-5 bg-forge-accent text-forge-bg text-[10px] font-bold rounded-full px-1"
          >
            {{ importPendingCount > 99 ? '99+' : importPendingCount }}
          </span>
          <span
            v-if="link.badge === 'plugins' && pluginUpdateCount > 0"
            class="ml-1.5 inline-flex items-center justify-center min-w-[1.2rem] h-5 bg-orange-600 text-white text-[10px] font-bold rounded-full px-1"
          >
            {{ pluginUpdateCount > 99 ? '99+' : pluginUpdateCount }}
          </span>
        </RouterLink>
      </div>
    </div>
  </nav>
</template>

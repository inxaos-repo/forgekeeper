<!--
  NavBar.vue — Top navigation bar
  Logo, page links, global search, mobile hamburger menu
-->
<script setup>
import { ref } from 'vue'
import { RouterLink, useRouter } from 'vue-router'

const router = useRouter()
const globalSearch = ref('')
const mobileMenuOpen = ref(false)

function onGlobalSearch() {
  if (globalSearch.value.trim()) {
    router.push({ name: 'Models', query: { search: globalSearch.value.trim() } })
    mobileMenuOpen.value = false
  }
}

const navLinks = [
  { to: '/', label: 'Models', name: 'Models' },
  { to: '/creators', label: 'Creators', name: 'Creators' },
  { to: '/import', label: 'Import', name: 'Import' },
  { to: '/stats', label: 'Stats', name: 'Stats' },
]
</script>

<template>
  <nav class="bg-forge-surface border-b border-forge-border sticky top-0 z-50">
    <div class="max-w-7xl mx-auto px-4 sm:px-6 lg:px-8">
      <div class="flex items-center justify-between h-16">
        <!-- Logo + Desktop Links -->
        <div class="flex items-center gap-8">
          <RouterLink to="/" class="text-xl font-bold text-forge-accent flex items-center gap-2">
            <span class="text-2xl">⚒️</span>
            <span class="hidden sm:inline">Forgekeeper</span>
          </RouterLink>

          <div class="hidden md:flex items-center gap-1">
            <RouterLink
              v-for="link in navLinks"
              :key="link.to"
              :to="link.to"
              class="px-3 py-2 rounded-md text-sm font-medium text-forge-text-muted hover:text-forge-text hover:bg-forge-card transition-colors"
              active-class="!bg-forge-card !text-forge-accent"
              :exact="link.to === '/'"
            >
              {{ link.label }}
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
          class="block px-3 py-2 rounded-md text-sm font-medium text-forge-text-muted hover:text-forge-text hover:bg-forge-card"
          active-class="!bg-forge-card !text-forge-accent"
          :exact="link.to === '/'"
          @click="mobileMenuOpen = false"
        >
          {{ link.label }}
        </RouterLink>
      </div>
    </div>
  </nav>
</template>

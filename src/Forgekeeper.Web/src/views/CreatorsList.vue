<!--
  CreatorsList.vue — Creator directory page
  Server-side search/sort, avatars, paginated grid
-->
<script setup>
import { ref, computed, onMounted } from 'vue'
import { useRouter } from 'vue-router'
import { useApi } from '../composables/useApi.js'
import SourceBadge from '../components/SourceBadge.vue'

const router = useRouter()
const api = useApi()

const creators = ref([])
const searchQuery = ref('')
const sortBy = ref('name')
const sortDir = ref('asc')
const page = ref(1)
const pageSize = ref(120)
const totalCount = ref(0)
const totalPages = ref(0)

const sortOptions = [
  { value: 'name', label: 'Name' },
  { value: 'modelCount', label: 'Model Count' },
]

const pageRange = computed(() => {
  const total = totalPages.value
  const current = page.value
  const range = []
  const delta = 2
  for (let i = Math.max(1, current - delta); i <= Math.min(total, current + delta); i++) {
    range.push(i)
  }
  return range
})

async function fetchCreators() {
  try {
    const result = await api.getCreators({
      q: searchQuery.value || undefined,
      sortBy: sortBy.value,
      sortDescending: sortDir.value === 'desc' ? true : undefined,
      page: page.value,
      pageSize: pageSize.value,
    })
    creators.value = result?.items || result || []
    totalCount.value = result?.totalCount ?? creators.value.length
    totalPages.value = result?.totalPages ?? Math.ceil(totalCount.value / pageSize.value)
  } catch {
    creators.value = []
  }
}

function onSearch() {
  page.value = 1
  fetchCreators()
}

function onSort(field) {
  if (sortBy.value === field) {
    sortDir.value = sortDir.value === 'asc' ? 'desc' : 'asc'
  } else {
    sortBy.value = field
    sortDir.value = field === 'modelCount' ? 'desc' : 'asc'
  }
  page.value = 1
  fetchCreators()
}

function goToPage(p) {
  page.value = p
  fetchCreators()
  window.scrollTo(0, 0)
}

function goToCreator(creator) {
  router.push({ name: 'CreatorDetail', params: { id: creator.id } })
}

onMounted(fetchCreators)
</script>

<template>
  <div>
    <h1 class="text-2xl font-bold text-forge-text mb-6">Creators</h1>

    <!-- Search + Sort -->
    <div class="flex flex-col sm:flex-row gap-3 mb-6">
      <form @submit.prevent="onSearch" class="flex-1 relative">
        <input
          v-model="searchQuery"
          type="text"
          placeholder="Search creators..."
          class="w-full bg-forge-card border border-forge-border rounded-lg px-4 py-2.5 text-sm text-forge-text placeholder-forge-text-muted focus:outline-none focus:border-forge-accent focus:ring-1 focus:ring-forge-accent"
        />
        <button type="submit" class="absolute right-3 top-1/2 -translate-y-1/2 text-forge-text-muted hover:text-forge-accent">🔍</button>
      </form>
      <div class="flex items-center gap-2">
        <select
          :value="sortBy"
          @change="onSort($event.target.value)"
          class="bg-forge-card border border-forge-border rounded-lg px-3 py-2.5 text-sm text-forge-text focus:outline-none focus:border-forge-accent"
        >
          <option v-for="opt in sortOptions" :key="opt.value" :value="opt.value">{{ opt.label }}</option>
        </select>
        <button
          @click="sortDir = sortDir === 'asc' ? 'desc' : 'asc'; fetchCreators()"
          class="p-2.5 bg-forge-card border border-forge-border rounded-lg text-forge-text-muted hover:text-forge-accent transition-colors"
        >
          {{ sortDir === 'asc' ? '↑' : '↓' }}
        </button>
      </div>
    </div>

    <p class="text-sm text-forge-text-muted mb-4">
      {{ totalCount.toLocaleString() }} creators
    </p>

    <!-- Loading -->
    <div v-if="api.loading.value && !creators.length" class="flex justify-center py-20">
      <div class="w-8 h-8 border-2 border-forge-accent border-t-transparent rounded-full animate-spin"></div>
    </div>

    <!-- Grid -->
    <div
      v-else-if="creators.length"
      class="grid grid-cols-2 sm:grid-cols-3 md:grid-cols-4 lg:grid-cols-5 xl:grid-cols-6 gap-4"
    >
      <div
        v-for="creator in creators"
        :key="creator.id"
        @click="goToCreator(creator)"
        class="bg-forge-card border border-forge-border rounded-xl p-4 cursor-pointer hover:border-forge-accent/50 hover:shadow-lg hover:shadow-forge-accent/5 transition-all duration-200 group"
      >
        <!-- Avatar -->
        <div class="w-16 h-16 mx-auto mb-3 rounded-full bg-forge-bg flex items-center justify-center overflow-hidden">
          <img
            v-if="creator.avatarUrl"
            :src="creator.avatarUrl"
            :alt="creator.name"
            class="w-full h-full object-cover"
            loading="lazy"
          />
          <span v-else class="text-2xl text-forge-text-muted/30">👤</span>
        </div>

        <!-- Name -->
        <h3 class="text-sm font-semibold text-forge-text text-center truncate group-hover:text-forge-accent transition-colors">
          {{ creator.name }}
        </h3>

        <!-- Model count -->
        <p class="text-xs text-forge-text-muted text-center mt-1">
          {{ creator.modelCount || 0 }} models
        </p>

        <!-- Source badge -->
        <div class="flex justify-center mt-2">
          <SourceBadge :source="creator.source" size="sm" />
        </div>
      </div>
    </div>

    <!-- Empty state -->
    <div v-else class="text-center py-20">
      <span class="text-5xl">👤</span>
      <p class="text-forge-text-muted mt-4">No creators found</p>
    </div>

    <!-- Pagination -->
    <div v-if="totalPages > 1" class="flex items-center justify-center gap-2 mt-8">
      <button
        @click="goToPage(1)"
        :disabled="page === 1"
        class="px-3 py-1.5 rounded-lg text-sm bg-forge-card border border-forge-border text-forge-text-muted hover:text-forge-accent disabled:opacity-30 disabled:cursor-not-allowed"
      >«</button>
      <button
        @click="goToPage(page - 1)"
        :disabled="page === 1"
        class="px-3 py-1.5 rounded-lg text-sm bg-forge-card border border-forge-border text-forge-text-muted hover:text-forge-accent disabled:opacity-30 disabled:cursor-not-allowed"
      >‹</button>
      <button
        v-for="p in pageRange"
        :key="p"
        @click="goToPage(p)"
        :class="[
          'px-3 py-1.5 rounded-lg text-sm border transition-colors',
          p === page
            ? 'bg-forge-accent text-forge-bg border-forge-accent font-medium'
            : 'bg-forge-card border-forge-border text-forge-text-muted hover:text-forge-accent',
        ]"
      >{{ p }}</button>
      <button
        @click="goToPage(page + 1)"
        :disabled="page >= totalPages"
        class="px-3 py-1.5 rounded-lg text-sm bg-forge-card border border-forge-border text-forge-text-muted hover:text-forge-accent disabled:opacity-30 disabled:cursor-not-allowed"
      >›</button>
      <button
        @click="goToPage(totalPages)"
        :disabled="page >= totalPages"
        class="px-3 py-1.5 rounded-lg text-sm bg-forge-card border border-forge-border text-forge-text-muted hover:text-forge-accent disabled:opacity-30 disabled:cursor-not-allowed"
      >»</button>
    </div>
  </div>
</template>

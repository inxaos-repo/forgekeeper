<!--
  ModelsList.vue — Main browse/search page
  Search bar, filter sidebar, sort options, paginated grid of model cards
-->
<script setup>
import { ref, reactive, computed, watch, onMounted } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { useApi } from '../composables/useApi.js'
import ModelCard from '../components/ModelCard.vue'
import FilterSidebar from '../components/FilterSidebar.vue'

const route = useRoute()
const router = useRouter()
const api = useApi()

const models = ref([])
const totalCount = ref(0)
const totalPages = ref(0)
const searchQuery = ref(route.query.search || '')
const showFilters = ref(false) // mobile filter toggle

const filters = reactive({
  source: route.query.source || undefined,
  creatorId: route.query.creatorId || undefined,
  category: route.query.category || undefined,
  gameSystem: route.query.gameSystem || undefined,
  scale: route.query.scale || undefined,
  tag: route.query.tag || undefined,
  printed: route.query.printed || undefined,
})

const sortBy = ref(route.query.sortBy || 'name')
const sortDir = ref(route.query.sortDir || 'asc')
const page = ref(parseInt(route.query.page) || 1)
const pageSize = ref(parseInt(route.query.pageSize) || 48)

const sortOptions = [
  { value: 'name', label: 'Name' },
  { value: 'createdAt', label: 'Date Added' },
  { value: 'fileCount', label: 'File Count' },
  { value: 'totalSizeBytes', label: 'Size' },
  { value: 'rating', label: 'Rating' },
]

async function fetchModels() {
  try {
    const params = {
      search: searchQuery.value || undefined,
      ...filters,
      sortBy: sortBy.value,
      sortDir: sortDir.value,
      page: page.value,
      pageSize: pageSize.value,
    }
    const result = await api.getModels(params)
    models.value = result?.items || result || []
    totalCount.value = result?.totalCount ?? models.value.length
    totalPages.value = result?.totalPages ?? Math.ceil(totalCount.value / pageSize.value)
  } catch {
    models.value = []
  }
}

// Update URL query params
function syncQueryParams() {
  const query = {}
  if (searchQuery.value) query.search = searchQuery.value
  if (filters.source) query.source = filters.source
  if (filters.creatorId) query.creatorId = filters.creatorId
  if (filters.category) query.category = filters.category
  if (filters.gameSystem) query.gameSystem = filters.gameSystem
  if (filters.scale) query.scale = filters.scale
  if (filters.tag) query.tag = filters.tag
  if (filters.printed) query.printed = filters.printed
  if (sortBy.value !== 'name') query.sortBy = sortBy.value
  if (sortDir.value !== 'asc') query.sortDir = sortDir.value
  if (page.value > 1) query.page = page.value
  router.replace({ query })
}

function onSearch() {
  page.value = 1
  syncQueryParams()
  fetchModels()
}

function onFiltersUpdate(newFilters) {
  Object.assign(filters, newFilters)
  page.value = 1
  syncQueryParams()
  fetchModels()
}

function onSort(field) {
  if (sortBy.value === field) {
    sortDir.value = sortDir.value === 'asc' ? 'desc' : 'asc'
  } else {
    sortBy.value = field
    sortDir.value = 'asc'
  }
  page.value = 1
  syncQueryParams()
  fetchModels()
}

function goToPage(p) {
  page.value = p
  syncQueryParams()
  fetchModels()
  window.scrollTo(0, 0)
}

// Pagination range
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

// Watch route query for external navigation (e.g., global search)
watch(() => route.query.search, (val) => {
  if (val !== searchQuery.value) {
    searchQuery.value = val || ''
    page.value = 1
    fetchModels()
  }
})

// Watch for creator filter from /creators/:id route
watch(() => route.query.creatorId, (val) => {
  if (val !== filters.creatorId) {
    filters.creatorId = val || undefined
    page.value = 1
    fetchModels()
  }
})

onMounted(fetchModels)
</script>

<template>
  <div>
    <!-- Search + Sort bar -->
    <div class="flex flex-col sm:flex-row items-stretch sm:items-center gap-3 mb-6">
      <!-- Search input -->
      <form @submit.prevent="onSearch" class="flex-1 relative">
        <input
          v-model="searchQuery"
          type="text"
          placeholder="Search models, creators, tags..."
          class="w-full bg-forge-card border border-forge-border rounded-lg px-4 py-2.5 text-sm text-forge-text placeholder-forge-text-muted focus:outline-none focus:border-forge-accent focus:ring-1 focus:ring-forge-accent transition-colors"
        />
        <button
          type="submit"
          class="absolute right-3 top-1/2 -translate-y-1/2 text-forge-text-muted hover:text-forge-accent"
        >🔍</button>
      </form>

      <!-- Sort dropdown -->
      <div class="flex items-center gap-2">
        <select
          :value="sortBy"
          @change="onSort($event.target.value)"
          class="bg-forge-card border border-forge-border rounded-lg px-3 py-2.5 text-sm text-forge-text focus:outline-none focus:border-forge-accent"
        >
          <option v-for="opt in sortOptions" :key="opt.value" :value="opt.value">
            {{ opt.label }}
          </option>
        </select>
        <button
          @click="sortDir = sortDir === 'asc' ? 'desc' : 'asc'; onSearch()"
          class="p-2.5 bg-forge-card border border-forge-border rounded-lg text-forge-text-muted hover:text-forge-accent transition-colors"
          :title="sortDir === 'asc' ? 'Ascending' : 'Descending'"
        >
          {{ sortDir === 'asc' ? '↑' : '↓' }}
        </button>

        <!-- Mobile filter toggle -->
        <button
          @click="showFilters = !showFilters"
          class="lg:hidden p-2.5 bg-forge-card border border-forge-border rounded-lg text-forge-text-muted hover:text-forge-accent"
        >
          🔧
        </button>
      </div>
    </div>

    <!-- Result count -->
    <p class="text-sm text-forge-text-muted mb-4">
      {{ totalCount.toLocaleString() }} models found
    </p>

    <div class="flex gap-6">
      <!-- Filter sidebar (desktop always visible, mobile toggleable) -->
      <div
        :class="[
          'w-64 shrink-0',
          showFilters ? 'block' : 'hidden lg:block',
        ]"
      >
        <div class="bg-forge-card border border-forge-border rounded-xl p-4 sticky top-20">
          <FilterSidebar :filters="filters" @update:filters="onFiltersUpdate" />
        </div>
      </div>

      <!-- Model grid -->
      <div class="flex-1 min-w-0">
        <!-- Loading -->
        <div v-if="api.loading.value && !models.length" class="flex justify-center py-20">
          <div class="w-8 h-8 border-2 border-forge-accent border-t-transparent rounded-full animate-spin"></div>
        </div>

        <!-- Empty state -->
        <div v-else-if="!models.length" class="text-center py-20">
          <span class="text-5xl">🗿</span>
          <p class="text-forge-text-muted mt-4">No models found</p>
          <p class="text-sm text-forge-text-muted mt-1">Try adjusting your search or filters</p>
        </div>

        <!-- Grid -->
        <div
          v-else
          class="grid grid-cols-2 sm:grid-cols-3 md:grid-cols-3 lg:grid-cols-4 xl:grid-cols-4 gap-4"
        >
          <ModelCard v-for="model in models" :key="model.id" :model="model" />
        </div>

        <!-- Pagination -->
        <div v-if="totalPages > 1" class="flex items-center justify-center gap-2 mt-8">
          <button
            @click="goToPage(1)"
            :disabled="page === 1"
            class="px-3 py-1.5 rounded-lg text-sm bg-forge-card border border-forge-border text-forge-text-muted hover:text-forge-accent disabled:opacity-30 disabled:cursor-not-allowed"
          >
            «
          </button>
          <button
            @click="goToPage(page - 1)"
            :disabled="page === 1"
            class="px-3 py-1.5 rounded-lg text-sm bg-forge-card border border-forge-border text-forge-text-muted hover:text-forge-accent disabled:opacity-30 disabled:cursor-not-allowed"
          >
            ‹
          </button>

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
          >
            {{ p }}
          </button>

          <button
            @click="goToPage(page + 1)"
            :disabled="page >= totalPages"
            class="px-3 py-1.5 rounded-lg text-sm bg-forge-card border border-forge-border text-forge-text-muted hover:text-forge-accent disabled:opacity-30 disabled:cursor-not-allowed"
          >
            ›
          </button>
          <button
            @click="goToPage(totalPages)"
            :disabled="page >= totalPages"
            class="px-3 py-1.5 rounded-lg text-sm bg-forge-card border border-forge-border text-forge-text-muted hover:text-forge-accent disabled:opacity-30 disabled:cursor-not-allowed"
          >
            »
          </button>
        </div>
      </div>
    </div>

    <!-- Error toast -->
    <div
      v-if="api.error.value"
      class="fixed bottom-4 right-4 bg-forge-danger/90 text-white px-4 py-2 rounded-lg shadow-lg text-sm"
    >
      {{ api.error.value }}
    </div>
  </div>
</template>

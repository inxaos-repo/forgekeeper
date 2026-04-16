<!--
  ModelsList.vue — Main browse/search page
  Search bar, filter sidebar, sort options, paginated grid of model cards
  Supports bulk selection + bulk actions (tag, categorize, rate)
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
const showFilters = ref(false)

const filters = reactive({
  source: route.query.source || undefined,
  creatorId: route.query.creatorId || undefined,
  category: route.query.category || undefined,
  gameSystem: route.query.gameSystem || undefined,
  scale: route.query.scale || undefined,
  tag: route.query.tag || undefined,
  printed: route.query.printed || undefined,
  minRating: route.query.minRating || undefined,
  licenseType: route.query.licenseType || undefined,
  collectionName: route.query.collectionName || undefined,
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
  { value: 'creatorName', label: 'Creator' },
]

// ─── Bulk Selection ──────────────────────────────────────
const selectedIds = ref(new Set())
const bulkMode = ref(false)
const bulkAction = ref('')
const bulkTag = ref('')
const bulkCategory = ref('')
const bulkGameSystem = ref('')
const bulkRating = ref(0)
const bulkProcessing = ref(false)

const allSelected = computed(() =>
  models.value.length > 0 && models.value.every((m) => selectedIds.value.has(m.id))
)

function toggleSelectAll() {
  if (allSelected.value) {
    selectedIds.value = new Set()
  } else {
    selectedIds.value = new Set(models.value.map((m) => m.id))
  }
}

function toggleSelect(id) {
  const s = new Set(selectedIds.value)
  if (s.has(id)) s.delete(id)
  else s.add(id)
  selectedIds.value = s
}

function cancelBulk() {
  bulkMode.value = false
  selectedIds.value = new Set()
  bulkAction.value = ''
  bulkTag.value = ''
  bulkCategory.value = ''
  bulkGameSystem.value = ''
  bulkRating.value = 0
}

const categories = [
  'Miniature', 'Terrain', 'Vehicle', 'Prop', 'Bust', 'Base', 'Scenery', 'Accessory', 'Other',
]
const gameSystems = [
  'Warhammer 40K', 'Age of Sigmar', 'D&D', 'Pathfinder', 'Star Wars Legion',
  'Bolt Action', 'Malifaux', 'Kill Team', 'Necromunda', 'Other',
]

async function executeBulkAction() {
  if (!selectedIds.value.size) return
  bulkProcessing.value = true
  try {
    const ids = [...selectedIds.value]
    const payload = { modelIds: ids }

    if (bulkAction.value === 'tag' && bulkTag.value.trim()) {
      payload.addTags = [bulkTag.value.trim().toLowerCase()]
    } else if (bulkAction.value === 'category' && bulkCategory.value) {
      payload.category = bulkCategory.value
    } else if (bulkAction.value === 'gameSystem' && bulkGameSystem.value) {
      payload.gameSystem = bulkGameSystem.value
    } else if (bulkAction.value === 'rating' && bulkRating.value > 0) {
      payload.rating = bulkRating.value
    } else {
      return
    }

    await api.bulkUpdateModels(payload)
    cancelBulk()
    await fetchModels()
  } catch {
    // error shown by api.error
  } finally {
    bulkProcessing.value = false
  }
}

// ─── Fetch + Query Sync ──────────────────────────────────
async function fetchModels() {
  try {
    const params = {
      q: searchQuery.value || undefined,
      ...filters,
      sortBy: sortBy.value,
      sortDescending: sortDir.value === 'desc' ? true : undefined,
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
  if (filters.minRating) query.minRating = filters.minRating
  if (filters.licenseType) query.licenseType = filters.licenseType
  if (filters.collectionName) query.collectionName = filters.collectionName
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

        <!-- Bulk mode toggle -->
        <button
          @click="bulkMode = !bulkMode; if (!bulkMode) cancelBulk()"
          :class="[
            'p-2.5 border rounded-lg text-sm font-medium transition-colors',
            bulkMode
              ? 'bg-forge-accent/15 border-forge-accent text-forge-accent'
              : 'bg-forge-card border-forge-border text-forge-text-muted hover:text-forge-accent',
          ]"
          title="Bulk select"
        >
          ☑
        </button>

        <button
          @click="showFilters = !showFilters"
          class="lg:hidden p-2.5 bg-forge-card border border-forge-border rounded-lg text-forge-text-muted hover:text-forge-accent"
        >
          🔧
        </button>
      </div>
    </div>

    <!-- Bulk action bar -->
    <div
      v-if="bulkMode && selectedIds.size > 0"
      class="bg-forge-card border border-forge-accent/30 rounded-xl p-4 mb-6 flex flex-wrap items-center gap-3"
    >
      <span class="text-sm font-medium text-forge-accent">
        {{ selectedIds.size }} selected
      </span>

      <select
        v-model="bulkAction"
        class="bg-forge-bg border border-forge-border rounded-lg px-3 py-1.5 text-sm text-forge-text focus:outline-none focus:border-forge-accent"
      >
        <option value="">Choose action...</option>
        <option value="tag">Add Tag</option>
        <option value="category">Set Category</option>
        <option value="gameSystem">Set Game System</option>
        <option value="rating">Set Rating</option>
      </select>

      <!-- Tag input -->
      <input
        v-if="bulkAction === 'tag'"
        v-model="bulkTag"
        type="text"
        placeholder="Tag name..."
        class="bg-forge-bg border border-forge-border rounded-lg px-3 py-1.5 text-sm text-forge-text placeholder-forge-text-muted focus:outline-none focus:border-forge-accent"
      />

      <!-- Category select -->
      <select
        v-if="bulkAction === 'category'"
        v-model="bulkCategory"
        class="bg-forge-bg border border-forge-border rounded-lg px-3 py-1.5 text-sm text-forge-text focus:outline-none focus:border-forge-accent"
      >
        <option value="">Choose...</option>
        <option v-for="cat in categories" :key="cat" :value="cat">{{ cat }}</option>
      </select>

      <!-- Game system select -->
      <select
        v-if="bulkAction === 'gameSystem'"
        v-model="bulkGameSystem"
        class="bg-forge-bg border border-forge-border rounded-lg px-3 py-1.5 text-sm text-forge-text focus:outline-none focus:border-forge-accent"
      >
        <option value="">Choose...</option>
        <option v-for="gs in gameSystems" :key="gs" :value="gs">{{ gs }}</option>
      </select>

      <!-- Rating select -->
      <select
        v-if="bulkAction === 'rating'"
        v-model.number="bulkRating"
        class="bg-forge-bg border border-forge-border rounded-lg px-3 py-1.5 text-sm text-forge-text focus:outline-none focus:border-forge-accent"
      >
        <option :value="0">Choose...</option>
        <option v-for="r in 5" :key="r" :value="r">{{ '★'.repeat(r) }}</option>
      </select>

      <button
        @click="executeBulkAction"
        :disabled="bulkProcessing || !bulkAction"
        class="px-4 py-1.5 bg-forge-accent hover:bg-forge-accent-hover text-forge-bg rounded-lg text-sm font-medium transition-colors disabled:opacity-50"
      >
        {{ bulkProcessing ? 'Applying...' : 'Apply' }}
      </button>

      <button
        @click="cancelBulk"
        class="px-3 py-1.5 text-sm text-forge-text-muted hover:text-forge-danger transition-colors"
      >
        Cancel
      </button>
    </div>

    <!-- Result count + select all -->
    <div class="flex items-center gap-3 mb-4">
      <label
        v-if="bulkMode"
        class="flex items-center gap-2 cursor-pointer text-sm text-forge-text-muted hover:text-forge-text"
      >
        <input
          type="checkbox"
          :checked="allSelected"
          @change="toggleSelectAll"
          class="rounded border-forge-border bg-forge-bg text-forge-accent focus:ring-forge-accent"
        />
        Select all on page
      </label>
      <p class="text-sm text-forge-text-muted">
        {{ totalCount.toLocaleString() }} models found
      </p>
    </div>

    <div class="flex gap-6">
      <!-- Filter sidebar -->
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
          <div v-for="model in models" :key="model.id" class="relative">
            <!-- Bulk checkbox overlay -->
            <label
              v-if="bulkMode"
              class="absolute top-2 left-2 z-10 cursor-pointer"
              @click.stop
            >
              <input
                type="checkbox"
                :checked="selectedIds.has(model.id)"
                @change="toggleSelect(model.id)"
                class="w-5 h-5 rounded border-forge-border bg-forge-bg/80 text-forge-accent focus:ring-forge-accent"
              />
            </label>
            <ModelCard
              :model="model"
              :class="{ 'ring-2 ring-forge-accent/50': selectedIds.has(model.id) }"
            />
          </div>
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

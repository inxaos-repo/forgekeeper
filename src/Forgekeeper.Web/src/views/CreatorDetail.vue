<!--
  CreatorDetail.vue — Creator detail page
  Shows creator info, stats, and a paginated grid of their models
-->
<script setup>
import { ref, computed, onMounted, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { useApi } from '../composables/useApi.js'
import ModelCard from '../components/ModelCard.vue'
import SourceBadge from '../components/SourceBadge.vue'
import Breadcrumbs from '../components/Breadcrumbs.vue'

const route = useRoute()
const router = useRouter()
const api = useApi()

const creator = ref(null)
const models = ref([])
const totalCount = ref(0)
const totalPages = ref(0)
const page = ref(1)
const pageSize = ref(48)
const sortBy = ref('name')
const sortDir = ref('asc')

const sortOptions = [
  { value: 'name', label: 'Name' },
  { value: 'createdAt', label: 'Date Added' },
  { value: 'fileCount', label: 'File Count' },
  { value: 'totalSizeBytes', label: 'Size' },
  { value: 'rating', label: 'Rating' },
]

function formatSize(bytes) {
  if (!bytes) return '0 B'
  const units = ['B', 'KB', 'MB', 'GB', 'TB']
  let i = 0
  let size = bytes
  while (size >= 1024 && i < units.length - 1) { size /= 1024; i++ }
  return `${size.toFixed(i > 1 ? 2 : 0)} ${units[i]}`
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

async function fetchCreator() {
  const id = route.params.id
  try {
    creator.value = await api.getCreator(id)
  } catch {
    creator.value = null
  }
}

async function fetchModels() {
  const id = route.params.id
  try {
    const result = await api.getCreatorModels(id, {
      sortBy: sortBy.value,
      sortDescending: sortDir.value === 'desc' ? true : undefined,
      page: page.value,
      pageSize: pageSize.value,
    })
    models.value = result?.items || result || []
    totalCount.value = result?.totalCount ?? models.value.length
    totalPages.value = result?.totalPages ?? Math.ceil(totalCount.value / pageSize.value)
  } catch {
    models.value = []
  }
}

function onSort(field) {
  if (sortBy.value === field) {
    sortDir.value = sortDir.value === 'asc' ? 'desc' : 'asc'
  } else {
    sortBy.value = field
    sortDir.value = 'asc'
  }
  page.value = 1
  fetchModels()
}

function goToPage(p) {
  page.value = p
  fetchModels()
  window.scrollTo(0, 0)
}

onMounted(() => {
  fetchCreator()
  fetchModels()
})

watch(() => route.params.id, () => {
  fetchCreator()
  page.value = 1
  fetchModels()
})
</script>

<template>
  <div>
    <!-- Loading -->
    <div v-if="api.loading.value && !creator" class="flex justify-center py-20">
      <div class="w-8 h-8 border-2 border-forge-accent border-t-transparent rounded-full animate-spin"></div>
    </div>

    <!-- Not found -->
    <div v-else-if="!creator" class="text-center py-20">
      <span class="text-5xl">👤</span>
      <p class="text-forge-text-muted mt-4">Creator not found</p>
      <button @click="router.push('/creators')" class="mt-4 text-forge-accent hover:underline text-sm">
        ← Back to creators
      </button>
    </div>

    <div v-else>
      <!-- Breadcrumb -->
      <Breadcrumbs
        :crumbs="[
          { label: 'Creators', to: '/creators' },
          { label: creator.name },
        ]"
      />

      <!-- Creator header -->
      <div class="bg-forge-card border border-forge-border rounded-xl p-6 mb-6">
        <div class="flex items-start gap-5">
          <!-- Avatar -->
          <div class="w-20 h-20 rounded-full bg-forge-bg flex items-center justify-center overflow-hidden shrink-0">
            <img
              v-if="creator.avatarUrl"
              :src="creator.avatarUrl"
              :alt="creator.name"
              class="w-full h-full object-cover"
            />
            <span v-else class="text-3xl text-forge-text-muted/30">👤</span>
          </div>

          <div class="flex-1 min-w-0">
            <div class="flex items-center gap-3">
              <h1 class="text-2xl font-bold text-forge-text">{{ creator.name }}</h1>
              <SourceBadge :source="creator.source" />
            </div>

            <!-- Stats -->
            <div class="flex flex-wrap gap-6 mt-3">
              <div>
                <p class="text-xl font-bold text-forge-accent">{{ (creator.modelCount || 0).toLocaleString() }}</p>
                <p class="text-xs text-forge-text-muted uppercase">Models</p>
              </div>
              <div v-if="creator.totalFiles">
                <p class="text-xl font-bold text-forge-text">{{ creator.totalFiles.toLocaleString() }}</p>
                <p class="text-xs text-forge-text-muted uppercase">Files</p>
              </div>
              <div v-if="creator.totalSizeBytes">
                <p class="text-xl font-bold text-forge-text">{{ formatSize(creator.totalSizeBytes) }}</p>
                <p class="text-xs text-forge-text-muted uppercase">Storage</p>
              </div>
            </div>

            <!-- Source link -->
            <a
              v-if="creator.sourceUrl"
              :href="creator.sourceUrl"
              target="_blank"
              class="inline-flex items-center gap-1 text-sm text-forge-accent hover:text-forge-accent-hover mt-3"
            >
              🔗 View profile
            </a>
          </div>
        </div>
      </div>

      <!-- Sort controls -->
      <div class="flex items-center justify-between mb-4">
        <p class="text-sm text-forge-text-muted">
          {{ totalCount.toLocaleString() }} models
        </p>
        <div class="flex items-center gap-2">
          <select
            :value="sortBy"
            @change="onSort($event.target.value)"
            class="bg-forge-card border border-forge-border rounded-lg px-3 py-2 text-sm text-forge-text focus:outline-none focus:border-forge-accent"
          >
            <option v-for="opt in sortOptions" :key="opt.value" :value="opt.value">{{ opt.label }}</option>
          </select>
          <button
            @click="sortDir = sortDir === 'asc' ? 'desc' : 'asc'; fetchModels()"
            class="p-2 bg-forge-card border border-forge-border rounded-lg text-forge-text-muted hover:text-forge-accent transition-colors"
          >
            {{ sortDir === 'asc' ? '↑' : '↓' }}
          </button>
        </div>
      </div>

      <!-- Model grid -->
      <div v-if="!models.length && !api.loading.value" class="text-center py-16">
        <span class="text-5xl">🗿</span>
        <p class="text-forge-text-muted mt-4">No models found for this creator</p>
      </div>

      <div
        v-else
        class="grid grid-cols-2 sm:grid-cols-3 md:grid-cols-3 lg:grid-cols-4 xl:grid-cols-5 gap-4"
      >
        <ModelCard v-for="m in models" :key="m.id" :model="m" />
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
  </div>
</template>

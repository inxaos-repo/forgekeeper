<!--
  CreatorsList.vue — Creator list page
  Search/filter creators, grid of creator cards, click to see their models
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

const filteredCreators = computed(() => {
  let list = [...creators.value]
  if (searchQuery.value) {
    const q = searchQuery.value.toLowerCase()
    list = list.filter((c) => c.name.toLowerCase().includes(q))
  }
  list.sort((a, b) => {
    if (sortBy.value === 'modelCount') return (b.modelCount || 0) - (a.modelCount || 0)
    return a.name.localeCompare(b.name)
  })
  return list
})

function goToCreator(creator) {
  // Navigate to models page filtered by this creator
  router.push({ path: '/', query: { creatorId: creator.id } })
}

async function fetchCreators() {
  try {
    const result = await api.getCreators({ pageSize: 10000 })
    creators.value = result?.items || result || []
  } catch {
    creators.value = []
  }
}

onMounted(fetchCreators)
</script>

<template>
  <div>
    <h1 class="text-2xl font-bold text-forge-text mb-6">Creators</h1>

    <!-- Search + Sort -->
    <div class="flex flex-col sm:flex-row gap-3 mb-6">
      <input
        v-model="searchQuery"
        type="text"
        placeholder="Search creators..."
        class="flex-1 bg-forge-card border border-forge-border rounded-lg px-4 py-2.5 text-sm text-forge-text placeholder-forge-text-muted focus:outline-none focus:border-forge-accent"
      />
      <select
        v-model="sortBy"
        class="bg-forge-card border border-forge-border rounded-lg px-3 py-2.5 text-sm text-forge-text focus:outline-none focus:border-forge-accent"
      >
        <option value="name">Sort by Name</option>
        <option value="modelCount">Sort by Model Count</option>
      </select>
    </div>

    <p class="text-sm text-forge-text-muted mb-4">
      {{ filteredCreators.length }} creators
    </p>

    <!-- Loading -->
    <div v-if="api.loading.value && !creators.length" class="flex justify-center py-20">
      <div class="w-8 h-8 border-2 border-forge-accent border-t-transparent rounded-full animate-spin"></div>
    </div>

    <!-- Grid -->
    <div
      v-else
      class="grid grid-cols-2 sm:grid-cols-3 md:grid-cols-4 lg:grid-cols-5 xl:grid-cols-6 gap-4"
    >
      <div
        v-for="creator in filteredCreators"
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
    <div v-if="!api.loading.value && !filteredCreators.length" class="text-center py-20">
      <span class="text-5xl">👤</span>
      <p class="text-forge-text-muted mt-4">No creators found</p>
    </div>
  </div>
</template>

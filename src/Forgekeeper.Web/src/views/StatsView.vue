<!--
  StatsView.vue — Dashboard with collection statistics
  Total counts, charts (CSS-based, no chart library needed), recent additions
-->
<script setup>
import { ref, computed, onMounted } from 'vue'
import { useApi } from '../composables/useApi.js'

const api = useApi()
const stats = ref(null)

function formatSize(bytes) {
  if (!bytes) return '0 B'
  const units = ['B', 'KB', 'MB', 'GB', 'TB']
  let i = 0
  let size = bytes
  while (size >= 1024 && i < units.length - 1) { size /= 1024; i++ }
  return `${size.toFixed(i > 1 ? 2 : 0)} ${units[i]}`
}

// Source colors for bar charts
const sourceColors = {
  mmf: 'bg-source-mmf',
  thangs: 'bg-source-thangs',
  patreon: 'bg-source-patreon',
  cults3d: 'bg-source-cults3d',
  thingiverse: 'bg-source-thingiverse',
  manual: 'bg-source-manual',
}

// Compute max for bar scaling
function maxOf(items) {
  return Math.max(...(items || []).map((i) => i.count || i.value || 0), 1)
}

async function fetchStats() {
  try {
    stats.value = await api.getStats()
  } catch {
    stats.value = null
  }
}

onMounted(fetchStats)
</script>

<template>
  <div>
    <h1 class="text-2xl font-bold text-forge-text mb-6">Dashboard</h1>

    <!-- Loading -->
    <div v-if="api.loading.value && !stats" class="flex justify-center py-20">
      <div class="w-8 h-8 border-2 border-forge-accent border-t-transparent rounded-full animate-spin"></div>
    </div>

    <!-- Error -->
    <div v-else-if="!stats" class="text-center py-20">
      <span class="text-5xl">📊</span>
      <p class="text-forge-text-muted mt-4">Unable to load statistics</p>
    </div>

    <div v-else class="space-y-6">
      <!-- Top-level stats cards -->
      <div class="grid grid-cols-2 sm:grid-cols-3 lg:grid-cols-6 gap-4">
        <div class="bg-forge-card border border-forge-border rounded-xl p-4 text-center">
          <p class="text-2xl font-bold text-forge-accent">{{ (stats.totalModels || 0).toLocaleString() }}</p>
          <p class="text-xs text-forge-text-muted mt-1 uppercase">Models</p>
        </div>
        <div class="bg-forge-card border border-forge-border rounded-xl p-4 text-center">
          <p class="text-2xl font-bold text-forge-accent">{{ (stats.totalCreators || 0).toLocaleString() }}</p>
          <p class="text-xs text-forge-text-muted mt-1 uppercase">Creators</p>
        </div>
        <div class="bg-forge-card border border-forge-border rounded-xl p-4 text-center">
          <p class="text-2xl font-bold text-forge-accent">{{ (stats.totalFiles || 0).toLocaleString() }}</p>
          <p class="text-xs text-forge-text-muted mt-1 uppercase">Files</p>
        </div>
        <div class="bg-forge-card border border-forge-border rounded-xl p-4 text-center">
          <p class="text-2xl font-bold text-forge-accent">{{ formatSize(stats.totalSizeBytes) }}</p>
          <p class="text-xs text-forge-text-muted mt-1 uppercase">Storage</p>
        </div>
        <div class="bg-forge-card border border-forge-border rounded-xl p-4 text-center">
          <p class="text-2xl font-bold text-forge-accent">{{ stats.printedCount || 0 }}</p>
          <p class="text-xs text-forge-text-muted mt-1 uppercase">Printed</p>
        </div>
        <div class="bg-forge-card border border-forge-border rounded-xl p-4 text-center">
          <p class="text-2xl font-bold text-forge-text-muted">{{ stats.unprintedCount || 0 }}</p>
          <p class="text-xs text-forge-text-muted mt-1 uppercase">Unprinted</p>
        </div>
      </div>

      <div class="grid grid-cols-1 lg:grid-cols-2 gap-6">
        <!-- Models by Source -->
        <div class="bg-forge-card border border-forge-border rounded-xl p-5">
          <h3 class="text-sm font-semibold text-forge-text-muted uppercase mb-4">Models by Source</h3>
          <div class="space-y-3">
            <div v-for="item in (stats.bySource || [])" :key="item.source" class="space-y-1">
              <div class="flex justify-between text-sm">
                <span class="text-forge-text capitalize">{{ item.source || 'unknown' }}</span>
                <span class="text-forge-text-muted">{{ (item.count || 0).toLocaleString() }}</span>
              </div>
              <div class="h-3 bg-forge-bg rounded-full overflow-hidden">
                <div
                  :class="[sourceColors[item.source] || 'bg-source-manual', 'h-full rounded-full transition-all duration-500']"
                  :style="{ width: `${Math.max(2, ((item.count || 0) / maxOf(stats.bySource)) * 100)}%` }"
                ></div>
              </div>
            </div>
            <div v-if="!stats.bySource?.length" class="text-sm text-forge-text-muted">No data</div>
          </div>
        </div>

        <!-- Models by Category -->
        <div class="bg-forge-card border border-forge-border rounded-xl p-5">
          <h3 class="text-sm font-semibold text-forge-text-muted uppercase mb-4">Models by Category</h3>
          <div class="space-y-3">
            <div v-for="item in (stats.byCategory || [])" :key="item.category" class="space-y-1">
              <div class="flex justify-between text-sm">
                <span class="text-forge-text">{{ item.category || 'Uncategorized' }}</span>
                <span class="text-forge-text-muted">{{ (item.count || 0).toLocaleString() }}</span>
              </div>
              <div class="h-3 bg-forge-bg rounded-full overflow-hidden">
                <div
                  class="h-full rounded-full bg-forge-accent/60 transition-all duration-500"
                  :style="{ width: `${Math.max(2, ((item.count || 0) / maxOf(stats.byCategory)) * 100)}%` }"
                ></div>
              </div>
            </div>
            <div v-if="!stats.byCategory?.length" class="text-sm text-forge-text-muted">No data</div>
          </div>
        </div>

        <!-- Top Creators -->
        <div class="bg-forge-card border border-forge-border rounded-xl p-5">
          <h3 class="text-sm font-semibold text-forge-text-muted uppercase mb-4">Top Creators</h3>
          <div class="space-y-2">
            <div
              v-for="(creator, idx) in (stats.topCreators || []).slice(0, 15)"
              :key="creator.id || idx"
              class="flex items-center justify-between text-sm"
            >
              <div class="flex items-center gap-2 min-w-0">
                <span class="text-forge-text-muted text-xs w-5 text-right">{{ idx + 1 }}.</span>
                <RouterLink
                  v-if="creator.id"
                  :to="{ path: '/', query: { creatorId: creator.id } }"
                  class="text-forge-text hover:text-forge-accent truncate"
                >
                  {{ creator.name }}
                </RouterLink>
                <span v-else class="text-forge-text truncate">{{ creator.name }}</span>
              </div>
              <span class="text-forge-text-muted shrink-0 ml-2">
                {{ (creator.modelCount || creator.count || 0).toLocaleString() }} models
              </span>
            </div>
            <div v-if="!stats.topCreators?.length" class="text-sm text-forge-text-muted">No data</div>
          </div>
        </div>

        <!-- Printed vs Unprinted -->
        <div class="bg-forge-card border border-forge-border rounded-xl p-5">
          <h3 class="text-sm font-semibold text-forge-text-muted uppercase mb-4">Print Status</h3>
          <div class="flex items-center justify-center py-8">
            <div class="relative w-40 h-40">
              <!-- Simple donut chart using CSS conic-gradient -->
              <div
                class="w-full h-full rounded-full"
                :style="{
                  background: `conic-gradient(
                    var(--color-forge-accent) 0% ${stats.totalModels ? ((stats.printedCount || 0) / stats.totalModels * 100) : 0}%,
                    var(--color-forge-border) ${stats.totalModels ? ((stats.printedCount || 0) / stats.totalModels * 100) : 0}% 100%
                  )`,
                }"
              >
                <div class="absolute inset-4 rounded-full bg-forge-card flex items-center justify-center">
                  <div class="text-center">
                    <p class="text-lg font-bold text-forge-accent">
                      {{ stats.totalModels ? Math.round((stats.printedCount || 0) / stats.totalModels * 100) : 0 }}%
                    </p>
                    <p class="text-xs text-forge-text-muted">printed</p>
                  </div>
                </div>
              </div>
            </div>
          </div>
          <div class="flex justify-center gap-6 text-sm">
            <div class="flex items-center gap-2">
              <div class="w-3 h-3 rounded-full bg-forge-accent"></div>
              <span class="text-forge-text-muted">Printed ({{ stats.printedCount || 0 }})</span>
            </div>
            <div class="flex items-center gap-2">
              <div class="w-3 h-3 rounded-full bg-forge-border"></div>
              <span class="text-forge-text-muted">Unprinted ({{ stats.unprintedCount || 0 }})</span>
            </div>
          </div>
        </div>
      </div>

      <!-- Recent additions -->
      <div v-if="stats.recentAdditions?.length" class="bg-forge-card border border-forge-border rounded-xl p-5">
        <h3 class="text-sm font-semibold text-forge-text-muted uppercase mb-4">Recent Additions</h3>
        <div class="space-y-2">
          <RouterLink
            v-for="model in stats.recentAdditions"
            :key="model.id"
            :to="`/models/${model.id}`"
            class="flex items-center justify-between py-2 px-3 rounded-lg hover:bg-forge-bg/50 transition-colors"
          >
            <div class="flex items-center gap-3 min-w-0">
              <span class="text-forge-text text-sm truncate">{{ model.name }}</span>
              <span class="text-xs text-forge-text-muted">by {{ model.creatorName }}</span>
            </div>
            <span class="text-xs text-forge-text-muted shrink-0">
              {{ new Date(model.createdAt).toLocaleDateString() }}
            </span>
          </RouterLink>
        </div>
      </div>
    </div>
  </div>
</template>

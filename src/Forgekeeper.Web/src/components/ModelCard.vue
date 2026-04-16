<!--
  ModelCard.vue — Grid card for model search results
  Shows thumbnail, name, creator, source badge, file count, printed indicator
-->
<script setup>
import { computed } from 'vue'
import SourceBadge from './SourceBadge.vue'

const props = defineProps({
  model: { type: Object, required: true },
})

const thumbnailUrl = computed(() => {
  if (props.model.thumbnailPath) {
    return `/api/v1/models/${props.model.id}/thumbnail`
  }
  return null
})

/** Human-readable file size */
function formatSize(bytes) {
  if (!bytes) return '0 B'
  const units = ['B', 'KB', 'MB', 'GB']
  let i = 0
  let size = bytes
  while (size >= 1024 && i < units.length - 1) {
    size /= 1024
    i++
  }
  return `${size.toFixed(i > 0 ? 1 : 0)} ${units[i]}`
}
</script>

<template>
  <RouterLink
    :to="`/models/${model.id}`"
    class="group block bg-forge-card border border-forge-border rounded-xl overflow-hidden hover:border-forge-accent/50 hover:shadow-lg hover:shadow-forge-accent/5 transition-all duration-200"
  >
    <!-- Thumbnail -->
    <div class="aspect-square bg-forge-bg flex items-center justify-center overflow-hidden">
      <img
        v-if="thumbnailUrl"
        :src="thumbnailUrl"
        :alt="model.name"
        class="w-full h-full object-cover group-hover:scale-105 transition-transform duration-300"
        loading="lazy"
      />
      <div v-else class="text-5xl text-forge-text-muted/30">🗿</div>
    </div>

    <!-- Info -->
    <div class="p-3 space-y-2">
      <!-- Model name -->
      <h3 class="text-sm font-semibold text-forge-text truncate group-hover:text-forge-accent transition-colors">
        {{ model.name }}
      </h3>

      <!-- Creator + Source -->
      <div class="flex items-center justify-between gap-2">
        <span class="text-xs text-forge-text-muted truncate">{{ model.creatorName || 'Unknown' }}</span>
        <SourceBadge :source="model.source" size="sm" />
      </div>

      <!-- Stats row -->
      <div class="flex items-center justify-between text-xs text-forge-text-muted">
        <span>{{ model.fileCount || 0 }} files · {{ formatSize(model.totalSizeBytes) }}</span>
        <div class="flex items-center gap-2">
          <!-- Rating -->
          <span v-if="model.rating" class="text-forge-accent">
            {{ '★'.repeat(model.rating) }}
          </span>
          <!-- Printed indicator -->
          <span
            v-if="model.printed"
            class="text-forge-accent"
            title="Printed"
          >🖨️</span>
        </div>
      </div>

      <!-- Tags (show first 3) -->
      <div v-if="model.tags?.length" class="flex flex-wrap gap-1">
        <span
          v-for="tag in model.tags.slice(0, 3)"
          :key="tag"
          class="px-1.5 py-0.5 text-[10px] bg-forge-bg rounded text-forge-text-muted"
        >
          {{ tag }}
        </span>
        <span
          v-if="model.tags.length > 3"
          class="px-1.5 py-0.5 text-[10px] text-forge-text-muted"
        >
          +{{ model.tags.length - 3 }}
        </span>
      </div>
    </div>
  </RouterLink>
</template>

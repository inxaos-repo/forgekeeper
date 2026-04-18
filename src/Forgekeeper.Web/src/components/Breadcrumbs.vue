<!--
  Breadcrumbs.vue — Simple breadcrumb trail
  Props:
    crumbs: Array of { label: string, to?: string }
  Usage:
    <Breadcrumbs :crumbs="[{ label: 'Models', to: '/' }, { label: 'Creator', to: '/creators/1' }, { label: 'Model Name' }]" />
  Last item is rendered as plain text (current page); prior items are router links.
-->
<script setup>
import { RouterLink } from 'vue-router'

defineProps({
  crumbs: {
    type: Array,
    default: () => [],
    // Each item: { label: string, to?: string }
  },
})
</script>

<template>
  <nav class="flex items-center gap-1.5 text-sm text-forge-text-muted mb-6 flex-wrap" aria-label="Breadcrumb">
    <template v-for="(crumb, idx) in crumbs" :key="idx">
      <!-- Separator (not before first item) -->
      <span v-if="idx > 0" class="text-forge-border select-none">›</span>

      <!-- Last item → plain text (current page) -->
      <span v-if="idx === crumbs.length - 1" class="text-forge-text truncate max-w-xs">
        {{ crumb.label }}
      </span>

      <!-- Earlier items → router links -->
      <RouterLink
        v-else-if="crumb.to"
        :to="crumb.to"
        class="hover:text-forge-accent transition-colors truncate max-w-xs"
      >
        {{ crumb.label }}
      </RouterLink>

      <!-- Earlier items without a route (rare, fallback) -->
      <span v-else class="truncate max-w-xs">{{ crumb.label }}</span>
    </template>
  </nav>
</template>

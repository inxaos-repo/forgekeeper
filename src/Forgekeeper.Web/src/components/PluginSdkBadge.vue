<!--
  PluginSdkBadge.vue — SDK compatibility indicator for plugins.
  Props:
    level: "Compatible" | "MinorMismatch" | "MajorMismatch" | "Unknown" | null
    reason: string (tooltip text)
-->
<script setup>
defineProps({
  level: { type: String, default: null },
  reason: { type: String, default: '' },
})

function badgeInfo(level) {
  switch (level) {
    case 'Compatible':
      return { icon: '✓', label: 'SDK OK', classes: 'bg-green-500/15 text-green-400 border-green-500/30' }
    case 'MinorMismatch':
      return { icon: '⚠', label: 'SDK warn', classes: 'bg-yellow-500/15 text-yellow-400 border-yellow-500/30' }
    case 'MajorMismatch':
      return { icon: '✗', label: 'SDK incompatible', classes: 'bg-forge-danger/15 text-forge-danger border-forge-danger/30' }
    case 'Unknown':
      return { icon: '?', label: 'SDK unknown', classes: 'bg-forge-border/30 text-forge-text-muted border-forge-border' }
    default:
      return null
  }
}
</script>

<template>
  <span
    v-if="badgeInfo(level)"
    :class="['inline-flex items-center gap-1 px-2 py-0.5 rounded-full text-xs font-medium border', badgeInfo(level).classes]"
    :title="reason || badgeInfo(level).label"
  >
    <span>{{ badgeInfo(level).icon }}</span>
    <span>{{ badgeInfo(level).label }}</span>
  </span>
</template>

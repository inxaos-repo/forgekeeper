<!--
  PluginTrustBadge.vue — Colored trust indicator for plugin source/validity.
  Props:
    source: "builtin" | "registry" | "github" | "manual"
    manifestValid: boolean | null (null = no manifest)
-->
<script setup>
defineProps({
  source: { type: String, default: 'manual' },
  manifestValid: { type: Boolean, default: null },
})

function badgeInfo(source, manifestValid) {
  // Invalid manifest overrides source trust level
  if (manifestValid === false) {
    return { emoji: '🔴', label: 'Invalid', classes: 'bg-forge-danger/15 text-forge-danger border-forge-danger/30' }
  }
  switch (source) {
    case 'builtin':
      return { emoji: '🟢', label: 'Official', classes: 'bg-green-500/15 text-green-400 border-green-500/30' }
    case 'registry':
      return { emoji: '🔵', label: 'Community', classes: 'bg-blue-500/15 text-blue-400 border-blue-500/30' }
    case 'github':
      return { emoji: '🟡', label: 'Unverified', classes: 'bg-yellow-500/15 text-yellow-400 border-yellow-500/30' }
    case 'manual':
    default:
      return { emoji: '🟠', label: 'Manual', classes: 'bg-orange-500/15 text-orange-400 border-orange-500/30' }
  }
}
</script>

<template>
  <span
    :class="['inline-flex items-center gap-1 px-2 py-0.5 rounded-full text-xs font-medium border', badgeInfo(source, manifestValid).classes]"
    :title="badgeInfo(source, manifestValid).label + ' plugin'"
  >
    <span>{{ badgeInfo(source, manifestValid).emoji }}</span>
    <span>{{ badgeInfo(source, manifestValid).label }}</span>
  </span>
</template>

<!--
  SourceBadge.vue — Colored pill showing model source
  MMF=orange, Thangs=blue, Patreon=red, Cults3D=purple, Thingiverse=cyan, Manual=gray
-->
<script setup>
import { computed } from 'vue'

const props = defineProps({
  source: { type: String, default: 'manual' },
  size: { type: String, default: 'md' }, // sm | md
})

const sourceConfig = {
  mmf:         { label: 'MMF',         bg: 'bg-source-mmf/20',      text: 'text-source-mmf' },
  thangs:      { label: 'Thangs',      bg: 'bg-source-thangs/20',   text: 'text-source-thangs' },
  patreon:     { label: 'Patreon',     bg: 'bg-source-patreon/20',  text: 'text-source-patreon' },
  cults3d:     { label: 'Cults3D',     bg: 'bg-source-cults3d/20',  text: 'text-source-cults3d' },
  thingiverse: { label: 'Thingiverse', bg: 'bg-source-thingiverse/20', text: 'text-source-thingiverse' },
  manual:      { label: 'Manual',      bg: 'bg-source-manual/20',   text: 'text-source-manual' },
}

const config = computed(() => {
  const key = String(props.source || 'manual').toLowerCase()
  return sourceConfig[key] || sourceConfig.manual
})

const sizeClasses = computed(() =>
  props.size === 'sm' ? 'px-1.5 py-0.5 text-[10px]' : 'px-2 py-0.5 text-xs'
)
</script>

<template>
  <span
    :class="[config.bg, config.text, sizeClasses, 'rounded-full font-medium whitespace-nowrap']"
  >
    {{ config.label }}
  </span>
</template>

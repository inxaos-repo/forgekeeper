<!--
  StarRating.vue — Clickable 1-5 star rating
  Props: modelValue (number 0-5), readonly
  Emits: update:modelValue
-->
<script setup>
import { ref, computed } from 'vue'

const props = defineProps({
  modelValue: { type: Number, default: 0 },
  readonly: { type: Boolean, default: false },
})
const emit = defineEmits(['update:modelValue'])

const hoverValue = ref(0)

function setRating(val) {
  if (props.readonly) return
  // Click same star = clear rating
  emit('update:modelValue', val === props.modelValue ? 0 : val)
}

function starClass(index) {
  const active = hoverValue.value || props.modelValue
  return index <= active ? 'text-forge-accent' : 'text-forge-border'
}
</script>

<template>
  <div class="flex items-center gap-0.5" @mouseleave="hoverValue = 0">
    <button
      v-for="i in 5"
      :key="i"
      type="button"
      :class="[
        starClass(i),
        'text-xl transition-colors',
        readonly ? 'cursor-default' : 'cursor-pointer hover:scale-110',
      ]"
      @mouseenter="!readonly && (hoverValue = i)"
      @click="setRating(i)"
      :disabled="readonly"
    >
      ★
    </button>
  </div>
</template>

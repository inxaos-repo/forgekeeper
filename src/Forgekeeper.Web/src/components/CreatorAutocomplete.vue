<!--
  CreatorAutocomplete.vue
  Searchable creator input with debounced API lookup.
  Emits: update:modelValue (creator name string)
-->
<script setup>
import { ref, watch } from 'vue'
import { useApi } from '../composables/useApi.js'

const props = defineProps({
  modelValue: { type: String, default: '' },
  placeholder: { type: String, default: 'Search creators...' },
})

const emit = defineEmits(['update:modelValue'])

const api = useApi()

const inputValue = ref(props.modelValue)
const results = ref([])
const showDropdown = ref(false)
const highlighted = ref(-1)
let debounceTimer = null

watch(() => props.modelValue, (val) => {
  if (val !== inputValue.value) inputValue.value = val
})

function onInput() {
  clearTimeout(debounceTimer)
  highlighted.value = -1
  if (!inputValue.value.trim()) {
    results.value = []
    showDropdown.value = false
    emit('update:modelValue', '')
    return
  }
  debounceTimer = setTimeout(async () => {
    try {
      const data = await api.getCreators({ search: inputValue.value.trim(), pageSize: 10 })
      results.value = data?.items ?? data ?? []
      showDropdown.value = results.value.length > 0
    } catch {
      results.value = []
      showDropdown.value = false
    }
  }, 300)
}

function select(creator) {
  const name = creator.name ?? creator
  inputValue.value = name
  emit('update:modelValue', name)
  showDropdown.value = false
  results.value = []
}

function onKeydown(e) {
  if (!showDropdown.value) return
  if (e.key === 'ArrowDown') {
    e.preventDefault()
    highlighted.value = Math.min(highlighted.value + 1, results.value.length - 1)
  } else if (e.key === 'ArrowUp') {
    e.preventDefault()
    highlighted.value = Math.max(highlighted.value - 1, 0)
  } else if (e.key === 'Enter' && highlighted.value >= 0) {
    e.preventDefault()
    select(results.value[highlighted.value])
  } else if (e.key === 'Escape') {
    showDropdown.value = false
  }
}

function onBlur() {
  // Small delay so click on dropdown item fires first
  setTimeout(() => { showDropdown.value = false }, 150)
  emit('update:modelValue', inputValue.value)
}
</script>

<template>
  <div class="relative">
    <input
      v-model="inputValue"
      type="text"
      :placeholder="placeholder"
      autocomplete="off"
      class="w-full bg-forge-bg border border-forge-border rounded-lg px-3 py-1.5 text-sm text-forge-text placeholder-forge-text-muted focus:outline-none focus:border-forge-accent focus:ring-1 focus:ring-forge-accent transition-colors"
      @input="onInput"
      @keydown="onKeydown"
      @blur="onBlur"
      @focus="results.length ? showDropdown = true : null"
    />
    <ul
      v-if="showDropdown && results.length"
      class="absolute z-50 top-full left-0 right-0 mt-1 bg-forge-card border border-forge-border rounded-lg shadow-lg max-h-48 overflow-y-auto"
    >
      <li
        v-for="(creator, i) in results"
        :key="creator.id ?? creator.name ?? i"
        :class="[
          'px-3 py-2 text-sm cursor-pointer transition-colors',
          i === highlighted
            ? 'bg-forge-accent/20 text-forge-accent'
            : 'text-forge-text hover:bg-forge-accent/10',
        ]"
        @mousedown.prevent="select(creator)"
      >
        {{ creator.name ?? creator }}
        <span v-if="creator.modelCount" class="text-forge-text-muted text-xs ml-1">
          ({{ creator.modelCount }} models)
        </span>
      </li>
    </ul>
  </div>
</template>

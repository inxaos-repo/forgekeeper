<!--
  TagEditor.vue — Tag input with removable pills and autocomplete
  Props: modelValue (string[]), allTags (string[])
  Emits: update:modelValue, add (tagName), remove (tagName)
-->
<script setup>
import { ref, computed } from 'vue'

const props = defineProps({
  modelValue: { type: Array, default: () => [] },
  allTags: { type: Array, default: () => [] },
})
const emit = defineEmits(['update:modelValue', 'add', 'remove'])

const input = ref('')
const showSuggestions = ref(false)

const suggestions = computed(() => {
  if (!input.value.trim()) return []
  const q = input.value.toLowerCase()
  return props.allTags
    .filter((t) => {
      const name = typeof t === 'string' ? t : t.name
      return name.toLowerCase().includes(q) && !props.modelValue.includes(name)
    })
    .map((t) => (typeof t === 'string' ? t : t.name))
    .slice(0, 10)
})

function addTag(tagName) {
  const name = (tagName || input.value).trim().toLowerCase()
  if (!name || props.modelValue.includes(name)) return
  emit('update:modelValue', [...props.modelValue, name])
  emit('add', name)
  input.value = ''
  showSuggestions.value = false
}

function removeTag(tagName) {
  emit('update:modelValue', props.modelValue.filter((t) => t !== tagName))
  emit('remove', tagName)
}

function onKeydown(e) {
  if (e.key === 'Enter') {
    e.preventDefault()
    addTag()
  }
  if (e.key === 'Backspace' && !input.value && props.modelValue.length) {
    removeTag(props.modelValue[props.modelValue.length - 1])
  }
}
</script>

<template>
  <div>
    <!-- Existing tags as pills -->
    <div class="flex flex-wrap gap-1.5 mb-2" v-if="modelValue.length">
      <span
        v-for="tag in modelValue"
        :key="tag"
        class="inline-flex items-center gap-1 px-2 py-0.5 bg-forge-accent/15 text-forge-accent text-xs rounded-full"
      >
        {{ tag }}
        <button
          @click="removeTag(tag)"
          class="hover:text-forge-danger transition-colors text-sm leading-none"
          type="button"
        >×</button>
      </span>
    </div>

    <!-- Input with autocomplete -->
    <div class="relative">
      <input
        v-model="input"
        type="text"
        placeholder="Add tag..."
        class="w-full bg-forge-bg border border-forge-border rounded-lg px-3 py-1.5 text-sm text-forge-text placeholder-forge-text-muted focus:outline-none focus:border-forge-accent"
        @keydown="onKeydown"
        @focus="showSuggestions = true"
        @blur="setTimeout(() => (showSuggestions = false), 200)"
      />

      <!-- Suggestions dropdown -->
      <ul
        v-if="showSuggestions && suggestions.length"
        class="absolute z-10 mt-1 w-full bg-forge-surface border border-forge-border rounded-lg shadow-lg max-h-40 overflow-y-auto"
      >
        <li
          v-for="s in suggestions"
          :key="s"
          @mousedown.prevent="addTag(s)"
          class="px-3 py-1.5 text-sm text-forge-text hover:bg-forge-accent/10 hover:text-forge-accent cursor-pointer"
        >
          {{ s }}
        </li>
      </ul>
    </div>
  </div>
</template>

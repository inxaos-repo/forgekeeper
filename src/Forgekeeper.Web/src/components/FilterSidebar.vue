<!--
  FilterSidebar.vue — Reusable filter panel for models search
  Emits filter changes via v-model:filters
-->
<script setup>
import { ref, computed, watch, onMounted } from 'vue'
import { useApi } from '../composables/useApi.js'

const props = defineProps({
  filters: { type: Object, default: () => ({}) },
})
const emit = defineEmits(['update:filters'])

const api = useApi()

// Available options (loaded from API)
const creators = ref([])
const tags = ref([])
const creatorSearch = ref('')

// Local filter state
const localFilters = ref({ ...props.filters })

// Sync prop → local
watch(() => props.filters, (v) => { localFilters.value = { ...v } }, { deep: true })

// Emit on change
function emitFilters() {
  emit('update:filters', { ...localFilters.value })
}

// Toggle a source checkbox
function toggleSource(source) {
  const current = localFilters.value.source || ''
  const sources = current ? current.split(',') : []
  const idx = sources.indexOf(source)
  if (idx >= 0) sources.splice(idx, 1)
  else sources.push(source)
  localFilters.value.source = sources.join(',') || undefined
  emitFilters()
}

function isSourceChecked(source) {
  return (localFilters.value.source || '').split(',').includes(source)
}

function setFilter(key, value) {
  localFilters.value[key] = value || undefined
  emitFilters()
}

function clearAll() {
  localFilters.value = {}
  creatorSearch.value = ''
  emitFilters()
}

const hasAnyFilter = computed(() =>
  Object.values(localFilters.value).some((v) => v !== undefined && v !== '')
)

const filteredCreators = computed(() => {
  if (!creatorSearch.value) return creators.value.slice(0, 50)
  const q = creatorSearch.value.toLowerCase()
  return creators.value.filter((c) => c.name.toLowerCase().includes(q)).slice(0, 50)
})

const sources = [
  { key: 'mmf', label: 'MyMiniFactory' },
  { key: 'thangs', label: 'Thangs' },
  { key: 'patreon', label: 'Patreon' },
  { key: 'cults3d', label: 'Cults3D' },
  { key: 'thingiverse', label: 'Thingiverse' },
  { key: 'manual', label: 'Manual' },
]

const categories = [
  'Miniature', 'Terrain', 'Vehicle', 'Prop', 'Bust', 'Base', 'Scenery', 'Accessory', 'Other',
]

const gameSystems = [
  'Warhammer 40K', 'Age of Sigmar', 'D&D', 'Pathfinder', 'Star Wars Legion',
  'Bolt Action', 'Malifaux', 'Kill Team', 'Necromunda', 'Other',
]

const scales = ['28mm', '32mm', '54mm', '75mm', '15mm', '6mm', 'Other']

const printedOptions = [
  { value: '', label: 'All' },
  { value: 'true', label: 'Printed' },
  { value: 'false', label: 'Not Printed' },
]

const licenseTypes = [
  'personal', 'commercial', 'cc-by', 'cc-by-nc', 'cc-by-sa', 'cc0', 'unknown',
]

// Loaded from API
const collectionNames = ref([])

onMounted(async () => {
  try {
    const [creatorsRes, tagsRes] = await Promise.all([
      api.getCreators({ pageSize: 1000 }),
      api.getTags(),
    ])
    creators.value = creatorsRes?.items || creatorsRes || []
    tags.value = tagsRes?.items || tagsRes || []

    // Collection names would need a dedicated endpoint; text input for now
  } catch {
    // Filters still work, just without autocomplete
  }
})
</script>

<template>
  <aside class="space-y-5">
    <!-- Header + Clear -->
    <div class="flex items-center justify-between">
      <h3 class="text-sm font-semibold text-forge-text uppercase tracking-wider">Filters</h3>
      <button
        v-if="hasAnyFilter"
        @click="clearAll"
        class="text-xs text-forge-danger hover:underline"
      >
        Clear all
      </button>
    </div>

    <!-- Source checkboxes -->
    <div>
      <h4 class="text-xs font-medium text-forge-text-muted mb-2 uppercase">Source</h4>
      <label
        v-for="src in sources"
        :key="src.key"
        class="flex items-center gap-2 py-1 cursor-pointer text-sm text-forge-text hover:text-forge-accent"
      >
        <input
          type="checkbox"
          :checked="isSourceChecked(src.key)"
          @change="toggleSource(src.key)"
          class="rounded border-forge-border bg-forge-bg text-forge-accent focus:ring-forge-accent"
        />
        {{ src.label }}
      </label>
    </div>

    <!-- Creator search -->
    <div>
      <h4 class="text-xs font-medium text-forge-text-muted mb-2 uppercase">Creator</h4>
      <input
        v-model="creatorSearch"
        type="text"
        placeholder="Search creators..."
        class="w-full bg-forge-bg border border-forge-border rounded-lg px-3 py-1.5 text-sm text-forge-text placeholder-forge-text-muted focus:outline-none focus:border-forge-accent"
      />
      <select
        :value="localFilters.creatorId || ''"
        @change="setFilter('creatorId', $event.target.value)"
        class="w-full mt-1 bg-forge-bg border border-forge-border rounded-lg px-3 py-1.5 text-sm text-forge-text focus:outline-none focus:border-forge-accent"
      >
        <option value="">All Creators</option>
        <option v-for="c in filteredCreators" :key="c.id" :value="c.id">{{ c.name }}</option>
      </select>
    </div>

    <!-- Category -->
    <div>
      <h4 class="text-xs font-medium text-forge-text-muted mb-2 uppercase">Category</h4>
      <select
        :value="localFilters.category || ''"
        @change="setFilter('category', $event.target.value)"
        class="w-full bg-forge-bg border border-forge-border rounded-lg px-3 py-1.5 text-sm text-forge-text focus:outline-none focus:border-forge-accent"
      >
        <option value="">All Categories</option>
        <option v-for="cat in categories" :key="cat" :value="cat">{{ cat }}</option>
      </select>
    </div>

    <!-- Game System -->
    <div>
      <h4 class="text-xs font-medium text-forge-text-muted mb-2 uppercase">Game System</h4>
      <select
        :value="localFilters.gameSystem || ''"
        @change="setFilter('gameSystem', $event.target.value)"
        class="w-full bg-forge-bg border border-forge-border rounded-lg px-3 py-1.5 text-sm text-forge-text focus:outline-none focus:border-forge-accent"
      >
        <option value="">All Systems</option>
        <option v-for="gs in gameSystems" :key="gs" :value="gs">{{ gs }}</option>
      </select>
    </div>

    <!-- Scale -->
    <div>
      <h4 class="text-xs font-medium text-forge-text-muted mb-2 uppercase">Scale</h4>
      <select
        :value="localFilters.scale || ''"
        @change="setFilter('scale', $event.target.value)"
        class="w-full bg-forge-bg border border-forge-border rounded-lg px-3 py-1.5 text-sm text-forge-text focus:outline-none focus:border-forge-accent"
      >
        <option value="">All Scales</option>
        <option v-for="s in scales" :key="s" :value="s">{{ s }}</option>
      </select>
    </div>

    <!-- Tags -->
    <div>
      <h4 class="text-xs font-medium text-forge-text-muted mb-2 uppercase">Tags</h4>
      <select
        :value="localFilters.tag || ''"
        @change="setFilter('tag', $event.target.value)"
        class="w-full bg-forge-bg border border-forge-border rounded-lg px-3 py-1.5 text-sm text-forge-text focus:outline-none focus:border-forge-accent"
      >
        <option value="">All Tags</option>
        <option v-for="t in tags" :key="t.name || t" :value="t.name || t">{{ t.name || t }}</option>
      </select>
    </div>

    <!-- License Type -->
    <div>
      <h4 class="text-xs font-medium text-forge-text-muted mb-2 uppercase">License</h4>
      <select
        :value="localFilters.licenseType || ''"
        @change="setFilter('licenseType', $event.target.value)"
        class="w-full bg-forge-bg border border-forge-border rounded-lg px-3 py-1.5 text-sm text-forge-text focus:outline-none focus:border-forge-accent"
      >
        <option value="">All Licenses</option>
        <option v-for="lt in licenseTypes" :key="lt" :value="lt">{{ lt }}</option>
      </select>
    </div>

    <!-- Collection Name -->
    <div>
      <h4 class="text-xs font-medium text-forge-text-muted mb-2 uppercase">Collection</h4>
      <input
        :value="localFilters.collectionName || ''"
        @change="setFilter('collectionName', $event.target.value)"
        type="text"
        placeholder="Filter by collection..."
        class="w-full bg-forge-bg border border-forge-border rounded-lg px-3 py-1.5 text-sm text-forge-text placeholder-forge-text-muted focus:outline-none focus:border-forge-accent"
      />
    </div>

    <!-- Min Rating -->
    <div>
      <h4 class="text-xs font-medium text-forge-text-muted mb-2 uppercase">Min Rating</h4>
      <div class="flex gap-1">
        <button
          v-for="r in [0, 1, 2, 3, 4, 5]"
          :key="r"
          @click="setFilter('minRating', r || undefined)"
          :class="[
            'px-2 py-1 rounded-lg text-xs font-medium transition-colors',
            (parseInt(localFilters.minRating) || 0) === r
              ? 'bg-forge-accent text-forge-bg'
              : 'bg-forge-bg text-forge-text-muted hover:text-forge-text border border-forge-border',
          ]"
        >
          {{ r === 0 ? 'Any' : '★'.repeat(r) }}
        </button>
      </div>
    </div>

    <!-- Printed status -->
    <div>
      <h4 class="text-xs font-medium text-forge-text-muted mb-2 uppercase">Printed Status</h4>
      <div class="flex gap-2">
        <button
          v-for="opt in printedOptions"
          :key="opt.value"
          @click="setFilter('printed', opt.value)"
          :class="[
            'px-3 py-1 rounded-lg text-xs font-medium transition-colors',
            (localFilters.printed || '') === opt.value
              ? 'bg-forge-accent text-forge-bg'
              : 'bg-forge-bg text-forge-text-muted hover:text-forge-text border border-forge-border',
          ]"
        >
          {{ opt.label }}
        </button>
      </div>
    </div>
  </aside>
</template>

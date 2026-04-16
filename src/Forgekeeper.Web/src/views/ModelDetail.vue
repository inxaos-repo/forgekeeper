<!--
  ModelDetail.vue — Single model detail page
  Shows model info, variant list (grouped by type), 3D preview, edit metadata,
  print history, and components ("Build Your Model")
-->
<script setup>
import { ref, computed, onMounted, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { useApi } from '../composables/useApi.js'
import StlViewer from '../components/StlViewer.vue'
import SourceBadge from '../components/SourceBadge.vue'
import StarRating from '../components/StarRating.vue'
import TagEditor from '../components/TagEditor.vue'

const route = useRoute()
const router = useRouter()
const api = useApi()

const model = ref(null)
const allTags = ref([])
const selectedVariantId = ref(null)
const saving = ref(false)
const saveSuccess = ref(false)

// Print history
const showAddPrint = ref(false)
const savingPrint = ref(false)
const newPrint = ref(defaultPrint())

function defaultPrint() {
  return {
    date: new Date().toISOString().split('T')[0],
    printer: '',
    technology: '',
    material: '',
    layerHeight: null,
    scale: null,
    result: 'success',
    notes: '',
    duration: '',
    variant: '',
  }
}

// Editable fields (local copy for save)
const editForm = ref({
  tags: [],
  category: '',
  gameSystem: '',
  scale: '',
  rating: 0,
  printed: false,
  notes: '',
})

// Categories, game systems, scales — same as FilterSidebar
const categories = [
  'Miniature', 'Terrain', 'Vehicle', 'Prop', 'Bust', 'Base', 'Scenery', 'Accessory', 'Other',
]
const gameSystems = [
  'Warhammer 40K', 'Age of Sigmar', 'D&D', 'Pathfinder', 'Star Wars Legion',
  'Bolt Action', 'Malifaux', 'Kill Team', 'Necromunda', 'Other',
]
const scales = ['28mm', '32mm', '54mm', '75mm', '15mm', '6mm', 'Other']
const technologies = ['resin', 'fdm', 'sla', 'msla']
const printResults = ['success', 'failed', 'partial']

// Group variants by type
const variantGroups = computed(() => {
  if (!model.value?.variants) return {}
  const groups = {}
  for (const v of model.value.variants) {
    const type = v.variantType || 'other'
    if (!groups[type]) groups[type] = []
    groups[type].push(v)
  }
  return groups
})

const variantTypeLabels = {
  supported: '✅ Supported',
  unsupported: '📦 Unsupported',
  presupported: '🔧 Pre-supported',
  lychee: '🍋 Lychee',
  other: '📄 Other',
}

const variantTypeOrder = ['supported', 'presupported', 'unsupported', 'lychee', 'other']

const orderedGroups = computed(() =>
  variantTypeOrder
    .filter((t) => variantGroups.value[t])
    .map((t) => ({ type: t, label: variantTypeLabels[t] || t, variants: variantGroups.value[t] }))
)

// Currently selected variant URL for 3D preview
const previewUrl = computed(() => {
  if (!selectedVariantId.value) return ''
  return api.getVariantDownloadUrl(selectedVariantId.value)
})

// Components grouped by group name
const componentGroups = computed(() => {
  if (!model.value?.components?.length) return []
  const groups = {}
  for (const c of model.value.components) {
    const g = c.group || '_ungrouped'
    if (!groups[g]) groups[g] = []
    groups[g].push(c)
  }
  return Object.entries(groups).map(([name, items]) => ({
    name: name === '_ungrouped' ? null : name,
    items,
  }))
})

function formatSize(bytes) {
  if (!bytes) return '0 B'
  const units = ['B', 'KB', 'MB', 'GB']
  let i = 0
  let size = bytes
  while (size >= 1024 && i < units.length - 1) { size /= 1024; i++ }
  return `${size.toFixed(i > 0 ? 1 : 0)} ${units[i]}`
}

function isPreviewable(variant) {
  const ext = (variant.fileName || '').split('.').pop()?.toLowerCase()
  return ['stl', 'obj'].includes(ext)
}

function resultEmoji(result) {
  return { success: '✅', failed: '❌', partial: '⚠️' }[result] || '❓'
}

async function fetchModel() {
  const id = route.params.id
  try {
    const result = await api.getModel(id)
    model.value = result

    // Populate edit form
    editForm.value = {
      tags: [...(result.tags || [])],
      category: result.category || '',
      gameSystem: result.gameSystem || '',
      scale: result.scale || '',
      rating: result.rating || 0,
      printed: result.printed || false,
      notes: result.notes || '',
    }

    // Auto-select first previewable variant
    const first = result.variants?.find(isPreviewable)
    if (first) selectedVariantId.value = first.id
  } catch {
    model.value = null
  }
}

async function fetchTags() {
  try {
    const result = await api.getTags()
    allTags.value = result?.items || result || []
  } catch {
    // non-critical
  }
}

async function saveModel() {
  if (!model.value) return
  saving.value = true
  saveSuccess.value = false
  try {
    await api.updateModel(model.value.id, {
      category: editForm.value.category || null,
      gameSystem: editForm.value.gameSystem || null,
      scale: editForm.value.scale || null,
      rating: editForm.value.rating || null,
      notes: editForm.value.notes || null,
      tags: editForm.value.tags,
    })
    saveSuccess.value = true
    setTimeout(() => (saveSuccess.value = false), 3000)
  } catch {
    // Error shown by api.error
  } finally {
    saving.value = false
  }
}

async function addPrint() {
  if (!model.value) return
  savingPrint.value = true
  try {
    await api.post(`/models/${model.value.id}/prints`, {
      date: newPrint.value.date,
      printer: newPrint.value.printer || null,
      technology: newPrint.value.technology || null,
      material: newPrint.value.material || null,
      layerHeight: newPrint.value.layerHeight || null,
      scale: newPrint.value.scale || null,
      result: newPrint.value.result,
      notes: newPrint.value.notes || null,
      duration: newPrint.value.duration || null,
      variant: newPrint.value.variant || null,
    })
    // Refresh model to get updated print history
    await fetchModel()
    newPrint.value = defaultPrint()
    showAddPrint.value = false
  } catch {
    // Error shown by api.error
  } finally {
    savingPrint.value = false
  }
}

async function onTagAdd(tagName) {
  if (model.value) {
    try { await api.addTagToModel(model.value.id, tagName) } catch { /* save handles it */ }
  }
}

async function onTagRemove(tagName) {
  if (model.value) {
    try { await api.removeTagFromModel(model.value.id, tagName) } catch { /* save handles it */ }
  }
}

onMounted(() => {
  fetchModel()
  fetchTags()
})

watch(() => route.params.id, fetchModel)
</script>

<template>
  <div>
    <!-- Loading -->
    <div v-if="api.loading.value && !model" class="flex justify-center py-20">
      <div class="w-8 h-8 border-2 border-forge-accent border-t-transparent rounded-full animate-spin"></div>
    </div>

    <!-- Not found -->
    <div v-else-if="!model" class="text-center py-20">
      <span class="text-5xl">🗿</span>
      <p class="text-forge-text-muted mt-4">Model not found</p>
      <button @click="router.push('/')" class="mt-4 text-forge-accent hover:underline text-sm">
        ← Back to models
      </button>
    </div>

    <!-- Model detail -->
    <div v-else>
      <!-- Breadcrumb -->
      <div class="flex items-center gap-2 text-sm text-forge-text-muted mb-6">
        <RouterLink to="/" class="hover:text-forge-accent">Models</RouterLink>
        <span>›</span>
        <RouterLink
          v-if="model.creatorId"
          :to="{ path: '/', query: { creatorId: model.creatorId } }"
          class="hover:text-forge-accent"
        >
          {{ model.creatorName }}
        </RouterLink>
        <span v-if="model.creatorId">›</span>
        <span class="text-forge-text">{{ model.name }}</span>
      </div>

      <div class="grid grid-cols-1 lg:grid-cols-3 gap-6">
        <!-- Left column: 3D Preview + Variants + Print History + Components -->
        <div class="lg:col-span-2 space-y-6">
          <!-- Header -->
          <div class="flex items-start justify-between gap-4">
            <div>
              <h1 class="text-2xl font-bold text-forge-text">{{ model.name }}</h1>
              <div class="flex items-center gap-3 mt-2">
                <RouterLink
                  v-if="model.creatorId"
                  :to="{ path: '/', query: { creatorId: model.creatorId } }"
                  class="text-forge-accent hover:underline"
                >
                  {{ model.creatorName }}
                </RouterLink>
                <SourceBadge :source="model.source" />
                <span v-if="model.licenseType" class="text-xs bg-forge-bg text-forge-text-muted px-2 py-0.5 rounded-full border border-forge-border">
                  📄 {{ model.licenseType }}
                </span>
                <span v-if="model.collectionName" class="text-xs bg-forge-bg text-forge-text-muted px-2 py-0.5 rounded-full border border-forge-border">
                  📁 {{ model.collectionName }}
                </span>
              </div>
            </div>
            <div class="text-sm text-forge-text-muted">
              {{ model.fileCount }} files · {{ formatSize(model.totalSizeBytes) }}
            </div>
          </div>

          <!-- 3D Preview -->
          <div class="bg-forge-card border border-forge-border rounded-xl overflow-hidden">
            <StlViewer :url="previewUrl" />
          </div>

          <!-- Preview images from metadata -->
          <div v-if="model.previewImages?.length" class="space-y-2">
            <h3 class="text-sm font-semibold text-forge-text-muted uppercase">Preview Images</h3>
            <div class="grid grid-cols-2 sm:grid-cols-3 gap-3">
              <img
                v-for="(img, idx) in model.previewImages"
                :key="idx"
                :src="`/api/v1/models/${model.id}/images/${idx}`"
                :alt="`Preview ${idx + 1}`"
                class="rounded-lg border border-forge-border object-cover aspect-square"
                loading="lazy"
              />
            </div>
          </div>

          <!-- Variants grouped by type -->
          <div class="space-y-4">
            <h3 class="text-sm font-semibold text-forge-text-muted uppercase">Variants</h3>

            <div v-for="group in orderedGroups" :key="group.type" class="space-y-2">
              <h4 class="text-sm font-medium text-forge-text">{{ group.label }}</h4>
              <div class="space-y-1">
                <div
                  v-for="variant in group.variants"
                  :key="variant.id"
                  :class="[
                    'flex items-center justify-between px-3 py-2 rounded-lg text-sm transition-colors cursor-pointer',
                    selectedVariantId === variant.id
                      ? 'bg-forge-accent/10 border border-forge-accent/30'
                      : 'bg-forge-card border border-forge-border hover:border-forge-accent/30',
                  ]"
                  @click="isPreviewable(variant) && (selectedVariantId = variant.id)"
                >
                  <div class="flex items-center gap-3 min-w-0">
                    <span v-if="isPreviewable(variant)" class="text-forge-accent text-xs">🔍</span>
                    <span v-else class="text-forge-text-muted text-xs">📄</span>
                    <span class="text-forge-text truncate">{{ variant.fileName }}</span>
                  </div>
                  <div class="flex items-center gap-3 shrink-0">
                    <span v-if="variant.physicalProperties?.triangleCount" class="text-xs text-forge-text-muted">
                      {{ variant.physicalProperties.triangleCount.toLocaleString() }} tris
                    </span>
                    <span class="text-xs text-forge-text-muted">{{ formatSize(variant.fileSizeBytes) }}</span>
                    <a
                      :href="api.getVariantDownloadUrl(variant.id)"
                      class="text-forge-accent hover:text-forge-accent-hover text-xs font-medium"
                      @click.stop
                      download
                    >
                      Download
                    </a>
                  </div>
                </div>
              </div>
            </div>

            <div v-if="!orderedGroups.length" class="text-sm text-forge-text-muted py-4">
              No variants found for this model.
            </div>
          </div>

          <!-- Components ("Build Your Model") -->
          <div v-if="model.components?.length" class="space-y-4">
            <h3 class="text-sm font-semibold text-forge-text-muted uppercase">🛠️ Build Your Model</h3>

            <div v-for="group in componentGroups" :key="group.name || '_ungrouped'" class="space-y-2">
              <h4 v-if="group.name" class="text-sm font-medium text-forge-text capitalize">
                {{ group.name }}
                <span class="text-xs text-forge-text-muted font-normal ml-1">
                  (pick one)
                </span>
              </h4>
              <div class="space-y-1">
                <div
                  v-for="comp in group.items"
                  :key="comp.file"
                  class="flex items-center justify-between px-3 py-2 rounded-lg text-sm bg-forge-card border border-forge-border"
                >
                  <div class="flex items-center gap-2 min-w-0">
                    <span :class="comp.required ? 'text-forge-accent' : 'text-forge-text-muted'" class="text-xs">
                      {{ comp.required ? '●' : '○' }}
                    </span>
                    <span class="text-forge-text truncate">{{ comp.name }}</span>
                    <span v-if="!comp.required" class="text-xs text-forge-text-muted">(optional)</span>
                  </div>
                  <span class="text-xs text-forge-text-muted truncate max-w-48">{{ comp.file }}</span>
                </div>
              </div>
            </div>
          </div>

          <!-- Print History -->
          <div class="space-y-4">
            <div class="flex items-center justify-between">
              <h3 class="text-sm font-semibold text-forge-text-muted uppercase">
                🖨️ Print History
                <span v-if="model.printHistory?.length" class="text-forge-text-muted font-normal">
                  ({{ model.printHistory.length }})
                </span>
              </h3>
              <button
                @click="showAddPrint = !showAddPrint"
                class="text-xs text-forge-accent hover:text-forge-accent-hover font-medium"
              >
                {{ showAddPrint ? 'Cancel' : '+ Add Print' }}
              </button>
            </div>

            <!-- Add print form -->
            <div v-if="showAddPrint" class="bg-forge-card border border-forge-border rounded-xl p-4 space-y-3">
              <div class="grid grid-cols-2 gap-3">
                <div>
                  <label class="block text-xs font-medium text-forge-text-muted mb-1">Date</label>
                  <input v-model="newPrint.date" type="date"
                    class="w-full bg-forge-bg border border-forge-border rounded-lg px-3 py-1.5 text-sm text-forge-text focus:outline-none focus:border-forge-accent" />
                </div>
                <div>
                  <label class="block text-xs font-medium text-forge-text-muted mb-1">Result</label>
                  <select v-model="newPrint.result"
                    class="w-full bg-forge-bg border border-forge-border rounded-lg px-3 py-1.5 text-sm text-forge-text focus:outline-none focus:border-forge-accent">
                    <option v-for="r in printResults" :key="r" :value="r">{{ r }}</option>
                  </select>
                </div>
                <div>
                  <label class="block text-xs font-medium text-forge-text-muted mb-1">Printer</label>
                  <input v-model="newPrint.printer" type="text" placeholder="e.g. Elegoo Saturn 4 Ultra"
                    class="w-full bg-forge-bg border border-forge-border rounded-lg px-3 py-1.5 text-sm text-forge-text placeholder-forge-text-muted focus:outline-none focus:border-forge-accent" />
                </div>
                <div>
                  <label class="block text-xs font-medium text-forge-text-muted mb-1">Technology</label>
                  <select v-model="newPrint.technology"
                    class="w-full bg-forge-bg border border-forge-border rounded-lg px-3 py-1.5 text-sm text-forge-text focus:outline-none focus:border-forge-accent">
                    <option value="">—</option>
                    <option v-for="t in technologies" :key="t" :value="t">{{ t }}</option>
                  </select>
                </div>
                <div>
                  <label class="block text-xs font-medium text-forge-text-muted mb-1">Material</label>
                  <input v-model="newPrint.material" type="text" placeholder="e.g. Elegoo ABS-like Grey"
                    class="w-full bg-forge-bg border border-forge-border rounded-lg px-3 py-1.5 text-sm text-forge-text placeholder-forge-text-muted focus:outline-none focus:border-forge-accent" />
                </div>
                <div>
                  <label class="block text-xs font-medium text-forge-text-muted mb-1">Layer Height (mm)</label>
                  <input v-model.number="newPrint.layerHeight" type="number" step="0.01" placeholder="0.05"
                    class="w-full bg-forge-bg border border-forge-border rounded-lg px-3 py-1.5 text-sm text-forge-text placeholder-forge-text-muted focus:outline-none focus:border-forge-accent" />
                </div>
                <div>
                  <label class="block text-xs font-medium text-forge-text-muted mb-1">Duration</label>
                  <input v-model="newPrint.duration" type="text" placeholder="e.g. 4h 30m"
                    class="w-full bg-forge-bg border border-forge-border rounded-lg px-3 py-1.5 text-sm text-forge-text placeholder-forge-text-muted focus:outline-none focus:border-forge-accent" />
                </div>
                <div>
                  <label class="block text-xs font-medium text-forge-text-muted mb-1">Scale</label>
                  <input v-model.number="newPrint.scale" type="number" step="0.01" placeholder="1.0"
                    class="w-full bg-forge-bg border border-forge-border rounded-lg px-3 py-1.5 text-sm text-forge-text placeholder-forge-text-muted focus:outline-none focus:border-forge-accent" />
                </div>
              </div>
              <div>
                <label class="block text-xs font-medium text-forge-text-muted mb-1">Notes</label>
                <textarea v-model="newPrint.notes" rows="2" placeholder="Print notes..."
                  class="w-full bg-forge-bg border border-forge-border rounded-lg px-3 py-2 text-sm text-forge-text placeholder-forge-text-muted focus:outline-none focus:border-forge-accent resize-y"></textarea>
              </div>
              <button
                @click="addPrint"
                :disabled="savingPrint"
                class="px-4 py-2 bg-forge-accent hover:bg-forge-accent-hover text-forge-bg rounded-lg text-sm font-medium transition-colors disabled:opacity-50"
              >
                {{ savingPrint ? 'Saving...' : 'Save Print' }}
              </button>
            </div>

            <!-- Print history list -->
            <div v-if="model.printHistory?.length" class="space-y-2">
              <div
                v-for="print in model.printHistory"
                :key="print.id"
                class="flex items-start justify-between px-3 py-2 rounded-lg text-sm bg-forge-card border border-forge-border"
              >
                <div class="space-y-0.5">
                  <div class="flex items-center gap-2">
                    <span>{{ resultEmoji(print.result) }}</span>
                    <span class="text-forge-text font-medium">{{ print.date }}</span>
                    <span v-if="print.printer" class="text-forge-text-muted">on {{ print.printer }}</span>
                  </div>
                  <div class="flex flex-wrap gap-x-3 gap-y-0.5 text-xs text-forge-text-muted">
                    <span v-if="print.technology">{{ print.technology }}</span>
                    <span v-if="print.material">{{ print.material }}</span>
                    <span v-if="print.layerHeight">{{ print.layerHeight }}mm layers</span>
                    <span v-if="print.duration">{{ print.duration }}</span>
                    <span v-if="print.scale">{{ print.scale }}× scale</span>
                  </div>
                  <p v-if="print.notes" class="text-xs text-forge-text-muted mt-1 italic">{{ print.notes }}</p>
                </div>
              </div>
            </div>

            <div v-else-if="!showAddPrint" class="text-sm text-forge-text-muted py-2">
              No prints recorded yet.
            </div>
          </div>

          <!-- Filesystem path -->
          <div class="text-xs text-forge-text-muted bg-forge-bg rounded-lg px-3 py-2 font-mono break-all">
            📁 {{ model.basePath }}
          </div>
        </div>

        <!-- Right column: Metadata editor -->
        <div class="space-y-6">
          <div class="bg-forge-card border border-forge-border rounded-xl p-5 sticky top-20 space-y-5">
            <h3 class="text-sm font-semibold text-forge-text-muted uppercase">Metadata</h3>

            <!-- Rating -->
            <div>
              <label class="block text-xs font-medium text-forge-text-muted mb-1 uppercase">Rating</label>
              <StarRating v-model="editForm.rating" />
            </div>

            <!-- Printed status (read-only, computed from print history) -->
            <div class="flex items-center gap-2">
              <span :class="model.printed ? 'text-forge-accent' : 'text-forge-text-muted'" class="text-sm">
                {{ model.printed ? '✅ Printed' : '⬜ Not printed' }}
              </span>
              <span class="text-xs text-forge-text-muted">(based on print history)</span>
            </div>

            <!-- Category -->
            <div>
              <label class="block text-xs font-medium text-forge-text-muted mb-1 uppercase">Category</label>
              <select
                v-model="editForm.category"
                class="w-full bg-forge-bg border border-forge-border rounded-lg px-3 py-1.5 text-sm text-forge-text focus:outline-none focus:border-forge-accent"
              >
                <option value="">None</option>
                <option v-for="cat in categories" :key="cat" :value="cat">{{ cat }}</option>
              </select>
            </div>

            <!-- Game System -->
            <div>
              <label class="block text-xs font-medium text-forge-text-muted mb-1 uppercase">Game System</label>
              <select
                v-model="editForm.gameSystem"
                class="w-full bg-forge-bg border border-forge-border rounded-lg px-3 py-1.5 text-sm text-forge-text focus:outline-none focus:border-forge-accent"
              >
                <option value="">None</option>
                <option v-for="gs in gameSystems" :key="gs" :value="gs">{{ gs }}</option>
              </select>
            </div>

            <!-- Scale -->
            <div>
              <label class="block text-xs font-medium text-forge-text-muted mb-1 uppercase">Scale</label>
              <select
                v-model="editForm.scale"
                class="w-full bg-forge-bg border border-forge-border rounded-lg px-3 py-1.5 text-sm text-forge-text focus:outline-none focus:border-forge-accent"
              >
                <option value="">None</option>
                <option v-for="s in scales" :key="s" :value="s">{{ s }}</option>
              </select>
            </div>

            <!-- Tags -->
            <div>
              <label class="block text-xs font-medium text-forge-text-muted mb-1 uppercase">Tags</label>
              <TagEditor
                v-model="editForm.tags"
                :allTags="allTags"
                @add="onTagAdd"
                @remove="onTagRemove"
              />
            </div>

            <!-- Notes -->
            <div>
              <label class="block text-xs font-medium text-forge-text-muted mb-1 uppercase">Notes</label>
              <textarea
                v-model="editForm.notes"
                rows="4"
                placeholder="Print notes, paint scheme, settings..."
                class="w-full bg-forge-bg border border-forge-border rounded-lg px-3 py-2 text-sm text-forge-text placeholder-forge-text-muted focus:outline-none focus:border-forge-accent resize-y"
              ></textarea>
            </div>

            <!-- Save button -->
            <button
              @click="saveModel"
              :disabled="saving"
              :class="[
                'w-full py-2.5 rounded-lg font-medium text-sm transition-colors',
                saveSuccess
                  ? 'bg-forge-accent text-forge-bg'
                  : 'bg-forge-accent hover:bg-forge-accent-hover text-forge-bg',
                saving && 'opacity-50 cursor-not-allowed',
              ]"
            >
              {{ saving ? 'Saving...' : saveSuccess ? '✓ Saved!' : 'Save Changes' }}
            </button>

            <!-- Error -->
            <p v-if="api.error.value" class="text-xs text-forge-danger">{{ api.error.value }}</p>
          </div>
        </div>
      </div>
    </div>
  </div>
</template>

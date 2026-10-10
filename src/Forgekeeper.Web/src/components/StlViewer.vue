<!--
  StlViewer.vue — Three.js STL renderer with OrbitControls
  Props: url, color, backgroundColor, autoRotate, zUp
  zUp (default true): STL files are Z-up; three.js is Y-up, so rotate -90° on X (#65).
  Renders an STL file with lighting, camera controls, and responsive sizing
-->
<script setup>
import { ref, onMounted, onBeforeUnmount, watch } from 'vue'
import * as THREE from 'three'
import { OrbitControls } from 'three/examples/jsm/controls/OrbitControls.js'
import { STLLoader } from 'three/examples/jsm/loaders/STLLoader.js'

const props = defineProps({
  url: { type: String, default: '' },
  color: { type: String, default: '#c8b8a8' },
  backgroundColor: { type: String, default: '#1c1c1c' },
  autoRotate: { type: Boolean, default: true },
  zUp: { type: Boolean, default: true },
})

const zUpOn = ref(props.zUp)
let currentMesh = null

const containerRef = ref(null)
const isLoading = ref(false)
const hasError = ref(false)
const errorMsg = ref('')

let renderer, scene, camera, controls, animFrameId, resizeObserver

function init() {
  if (!containerRef.value) return

  const container = containerRef.value
  const w = container.clientWidth
  const h = container.clientHeight || 400

  // Scene
  scene = new THREE.Scene()
  scene.background = new THREE.Color(props.backgroundColor)

  // Camera
  camera = new THREE.PerspectiveCamera(45, w / h, 0.1, 10000)
  camera.position.set(0, 50, 100)

  // Renderer
  renderer = new THREE.WebGLRenderer({ antialias: true })
  renderer.setSize(w, h)
  renderer.setPixelRatio(Math.min(window.devicePixelRatio, 2))
  container.appendChild(renderer.domElement)

  // Controls
  controls = new OrbitControls(camera, renderer.domElement)
  controls.enableDamping = true
  controls.dampingFactor = 0.08
  controls.autoRotate = props.autoRotate
  controls.autoRotateSpeed = 1.5

  // Lights
  const ambientLight = new THREE.AmbientLight(0xffffff, 0.6)
  scene.add(ambientLight)

  const dirLight1 = new THREE.DirectionalLight(0xffffff, 0.8)
  dirLight1.position.set(1, 2, 1)
  scene.add(dirLight1)

  const dirLight2 = new THREE.DirectionalLight(0xffffff, 0.3)
  dirLight2.position.set(-1, -1, -1)
  scene.add(dirLight2)

  // Grid helper
  const grid = new THREE.GridHelper(200, 20, 0x404040, 0x404040)
  grid.material.opacity = 0.3
  grid.material.transparent = true
  scene.add(grid)

  // Responsive resize
  resizeObserver = new ResizeObserver(() => {
    const newW = container.clientWidth
    const newH = container.clientHeight || 400
    camera.aspect = newW / newH
    camera.updateProjectionMatrix()
    renderer.setSize(newW, newH)
  })
  resizeObserver.observe(container)

  // Animation loop
  function animate() {
    animFrameId = requestAnimationFrame(animate)
    controls.update()
    renderer.render(scene, camera)
  }
  animate()
}

function loadModel(url) {
  if (!url || !scene) return

  isLoading.value = true
  hasError.value = false
  errorMsg.value = ''

  // Remove existing mesh
  if (currentMesh) {
    currentMesh.geometry.dispose()
    currentMesh.material.dispose()
    scene.remove(currentMesh)
    currentMesh = null
  }

  const loader = new STLLoader()
  loader.load(
    url,
    (geometry) => {
      geometry.computeBoundingBox()
      geometry.computeVertexNormals()

      const material = new THREE.MeshStandardMaterial({
        color: new THREE.Color(props.color),
        metalness: 0.25,
        roughness: 0.6,
        flatShading: false,
      })
      const mesh = new THREE.Mesh(geometry, material)

      // Center geometry on origin
      geometry.center()
      geometry.computeBoundingBox()

      // Scale to fit view
      const size = new THREE.Vector3()
      geometry.boundingBox.getSize(size)
      const maxDim = Math.max(size.x, size.y, size.z)
      if (maxDim > 0) {
        mesh.scale.setScalar(80 / maxDim)
      }

      currentMesh = mesh
      applyOrientation()
      scene.add(mesh)

      // Adjust camera to frame the model
      camera.position.set(0, 70, 120)
      applyOrientation()

      isLoading.value = false
    },
    undefined,
    (err) => {
      isLoading.value = false
      hasError.value = true
      errorMsg.value = err?.message || 'Failed to load STL'
    }
  )
}

// Orient the mesh (Z-up vs Y-up) and rest it on the grid.
function applyOrientation() {
  if (!currentMesh) return
  currentMesh.rotation.set(zUpOn.value ? -Math.PI / 2 : 0, 0, 0)
  currentMesh.position.set(0, 0, 0)
  currentMesh.updateMatrixWorld(true)
  const box = new THREE.Box3().setFromObject(currentMesh)
  currentMesh.position.y = -box.min.y
  if (controls) {
    controls.target.set(0, (box.max.y - box.min.y) / 2, 0)
    controls.update()
  }
}

function toggleUp() {
  zUpOn.value = !zUpOn.value
  applyOrientation()
}

watch(() => props.url, (url) => loadModel(url))

onMounted(() => {
  init()
  if (props.url) loadModel(props.url)
})

onBeforeUnmount(() => {
  if (animFrameId) cancelAnimationFrame(animFrameId)
  if (resizeObserver) resizeObserver.disconnect()
  if (renderer) {
    renderer.dispose()
    renderer.domElement?.remove()
  }
  if (controls) controls.dispose()
})
</script>

<template>
  <div class="stl-viewer relative w-full rounded-lg overflow-hidden bg-forge-bg" style="min-height: 400px">
    <div ref="containerRef" class="w-full h-full" style="min-height: 400px"></div>

    <button
      type="button"
      class="absolute top-2 right-2 z-10 px-2 py-1 text-xs rounded bg-forge-bg/80 border border-forge-border text-forge-text-muted hover:text-forge-text"
      :title="zUpOn ? 'Showing Z-up (STL default). Click for Y-up.' : 'Showing Y-up. Click for Z-up.'"
      @click="toggleUp"
    >{{ zUpOn ? 'Z-up' : 'Y-up' }}</button>

    <!-- Loading overlay -->
    <div
      v-if="isLoading"
      class="absolute inset-0 flex items-center justify-center bg-forge-bg/80"
    >
      <div class="flex flex-col items-center gap-3">
        <div class="w-8 h-8 border-2 border-forge-accent border-t-transparent rounded-full animate-spin"></div>
        <span class="text-sm text-forge-text-muted">Loading model...</span>
      </div>
    </div>

    <!-- Error overlay -->
    <div
      v-if="hasError"
      class="absolute inset-0 flex items-center justify-center bg-forge-bg/80"
    >
      <div class="text-center">
        <span class="text-3xl">⚠️</span>
        <p class="text-sm text-forge-danger mt-2">{{ errorMsg }}</p>
      </div>
    </div>

    <!-- No URL placeholder -->
    <div
      v-if="!url && !isLoading && !hasError"
      class="absolute inset-0 flex items-center justify-center"
    >
      <div class="text-center text-forge-text-muted">
        <span class="text-4xl">🗿</span>
        <p class="text-sm mt-2">Select a variant to preview</p>
      </div>
    </div>
  </div>
</template>

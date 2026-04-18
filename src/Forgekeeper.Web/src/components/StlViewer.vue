<!--
  StlViewer.vue — Three.js STL renderer with OrbitControls
  Props: url, color, backgroundColor, autoRotate
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
})

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

  // Remove existing meshes
  const toRemove = []
  scene.traverse((child) => {
    if (child.isMesh) toRemove.push(child)
  })
  toRemove.forEach((m) => {
    m.geometry.dispose()
    m.material.dispose()
    scene.remove(m)
  })

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

      // Center the model
      const bbox = geometry.boundingBox
      const center = new THREE.Vector3()
      bbox.getCenter(center)
      mesh.position.sub(center)

      // Scale to fit view
      const size = new THREE.Vector3()
      bbox.getSize(size)
      const maxDim = Math.max(size.x, size.y, size.z)
      if (maxDim > 0) {
        const scale = 80 / maxDim
        mesh.scale.setScalar(scale)
      }

      scene.add(mesh)

      // Adjust camera to frame the model
      camera.position.set(0, 50, 100)
      controls.target.set(0, 0, 0)
      controls.update()

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

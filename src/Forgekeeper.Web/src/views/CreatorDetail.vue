<!--
  CreatorDetail.vue — Redirects to models page filtered by creator
  The router maps /creators/:id to this component, which simply redirects
-->
<script setup>
import { onMounted } from 'vue'
import { useRoute, useRouter } from 'vue-router'

const route = useRoute()
const router = useRouter()

onMounted(() => {
  router.replace({ path: '/', query: { creatorId: route.params.id } })
})
</script>

<template>
  <div class="flex justify-center py-20">
    <div class="w-8 h-8 border-2 border-forge-accent border-t-transparent rounded-full animate-spin"></div>
  </div>
</template>

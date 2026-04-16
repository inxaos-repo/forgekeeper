<!--
  App.vue — Root component
  NavBar + RouterView with transition
-->
<script setup>
import { RouterView } from 'vue-router'
import NavBar from './components/NavBar.vue'
</script>

<template>
  <div class="min-h-screen bg-forge-bg text-forge-text">
    <NavBar />
    <main class="max-w-7xl mx-auto px-4 sm:px-6 lg:px-8 py-8">
      <RouterView v-slot="{ Component }">
        <transition name="fade" mode="out-in">
          <component :is="Component" />
        </transition>
      </RouterView>
    </main>
  </div>
</template>

<style scoped>
.fade-enter-active,
.fade-leave-active {
  transition: opacity 0.15s ease;
}
.fade-enter-from,
.fade-leave-to {
  opacity: 0;
}
</style>

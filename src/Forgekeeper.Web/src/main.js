import { createApp } from 'vue'
import { createRouter, createWebHistory } from 'vue-router'
import App from './App.vue'
import './style.css'

const routes = [
  { path: '/', name: 'Models', component: () => import('./views/ModelsList.vue') },
  { path: '/models/:id', name: 'ModelDetail', component: () => import('./views/ModelDetail.vue'), props: true },
  { path: '/creators', name: 'Creators', component: () => import('./views/CreatorsList.vue') },
  { path: '/creators/:id', name: 'CreatorDetail', component: () => import('./views/CreatorDetail.vue'), props: true },
  { path: '/import', name: 'Import', component: () => import('./views/ImportQueue.vue') },
  { path: '/stats', name: 'Stats', component: () => import('./views/StatsView.vue') },
  { path: '/plugins', name: 'Plugins', component: () => import('./views/PluginsView.vue') },
  { path: '/sources', name: 'Sources', component: () => import('./views/SourcesView.vue') },
]

const router = createRouter({
  history: createWebHistory(),
  routes,
})

const app = createApp(App)
app.use(router)
app.mount('#app')

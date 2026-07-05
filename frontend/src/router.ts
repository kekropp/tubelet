import { ref } from 'vue'

// Tiny hash router — no dependency, no history plumbing. Views key off `route.value`.
// The backend SPA fallback serves index.html for any path, so hash routing needs no server config.
export type Route = 'home' | 'library' | 'channels' | 'settings'

const routes: Route[] = ['home', 'library', 'channels', 'settings']

function parse(): Route {
  const h = location.hash.replace(/^#\/?/, '').split('/')[0]
  return (routes as string[]).includes(h) ? (h as Route) : 'home'
}

export const route = ref<Route>(parse())

window.addEventListener('hashchange', () => { route.value = parse() })

export function go(r: Route) {
  location.hash = r === 'home' ? '#/' : `#/${r}`
}

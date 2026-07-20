import { defineConfig, loadEnv } from 'vite'
import react from '@vitejs/plugin-react'
import { VitePWA } from 'vite-plugin-pwa'

// https://vitejs.dev/config/
export default defineConfig(({ mode }) => {
  const env = loadEnv(mode, process.cwd(), '')
  const basePath = env.VITE_BASE_PATH || '/'
  // Ensure basePath ends with / for Vite base option
  const viteBase = basePath.endsWith('/') ? basePath : basePath + '/'
  return {
  plugins: [
    react(),
    VitePWA({
      registerType: 'autoUpdate',
      includeAssets: ['favicon.ico', 'apple-touch-icon.png', 'favicon.svg', 'web-app-manifest-*.png'],
      manifest: {
        name: 'Milvaion - Job Scheduler',
        short_name: 'Milvaion',
        description: 'Distributed job scheduling and execution platform',
        theme_color: '#242424',
        background_color: '#242424',
        display: 'standalone',
        orientation: 'portrait',
        scope: viteBase,
        start_url: viteBase,
        icons: [
          {
            src: 'web-app-manifest-192x192.png',
            sizes: '192x192',
            type: 'image/png',
            purpose: 'any'
          },
          {
            src: 'web-app-manifest-512x512.png',
            sizes: '512x512',
            type: 'image/png',
            purpose: 'any'
          },
          {
            src: 'web-app-manifest-192x192.png',
            sizes: '192x192',
            type: 'image/png',
            purpose: 'maskable'
          },
          {
            src: 'web-app-manifest-512x512.png',
            sizes: '512x512',
            type: 'image/png',
            purpose: 'maskable'
          }
        ]
      },
      workbox: {
        globPatterns: ['**/*.{js,css,html,ico,png,svg,woff,woff2}'],
        globDirectory: 'dist',
        runtimeCaching: [
          {
            urlPattern: /^https:\/\/fonts\.googleapis\.com\/.*/i,
            handler: 'CacheFirst',
            options: {
              cacheName: 'google-fonts-cache',
              expiration: {
                maxEntries: 10,
                maxAgeSeconds: 60 * 60 * 24 * 365 // 365 days
              },
              cacheableResponse: {
                statuses: [0, 200]
              }
            }
          },
          {
            urlPattern: /^https:\/\/fonts\.gstatic\.com\/.*/i,
            handler: 'CacheFirst',
            options: {
              cacheName: 'gstatic-fonts-cache',
              expiration: {
                maxEntries: 10,
                maxAgeSeconds: 60 * 60 * 24 * 365 // 365 days
              },
              cacheableResponse: {
                statuses: [0, 200]
              }
            }
          },
          {
            urlPattern: /\/api\/.*/i,
            handler: 'NetworkFirst',
            options: {
              cacheName: 'api-cache',
              expiration: {
                maxEntries: 50,
                maxAgeSeconds: 60 * 5 // 5 minutes
              },
              networkTimeoutSeconds: 10
            }
          }
        ]
      },
      devOptions: {
        enabled: true,
        type: 'module'
      }
    })
  ],
  base: viteBase,
  server: {
    port: 3000,
    proxy: (() => {
      const prefix = basePath === '/' ? '' : basePath
      return {
        [`${prefix}/api`]: {
          target: 'http://localhost:5000',
          changeOrigin: true,
          secure: false,
        },
        [`${prefix}/hubs`]: {
          target: 'http://localhost:5000',
          changeOrigin: true,
          secure: false,
          ws: true,
        }
      }
    })()
  },
  build: {
    outDir: 'dist',
    emptyOutDir: true,
    sourcemap: false,
    // Optimize chunks
    rollupOptions: {
      output: {
        // Heavy libraries get their own chunks so they are cached independently of
        // application code, and - because only a few lazily loaded routes import
        // them - are never fetched by someone who does not open those routes.
        manualChunks: {
          'react-vendor': ['react', 'react-dom', 'react-router-dom'],
          'signalr': ['@microsoft/signalr'],
          // Workflow builder and canvas only.
          'reactflow': ['reactflow'],
          // Reports and dashboard charts only.
          'charts': ['recharts'],
          // Large, and pulled in by date formatting used across most pages.
          'moment': ['moment'],
        }
      }
    },
    // Copy PWA icons to dist
    copyPublicDir: true
  }
  }
})

import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

// `URL` and `import.meta.url` are standard in both browser and Node ESM,
// so we don't need @types/node here.
const srcDir = new URL('./src', import.meta.url).pathname;

export default defineConfig({
  plugins: [react()],
  resolve: {
    alias: { '@': srcDir },
  },
  server: {
    port: 5173,
    strictPort: true,
  },
});

import { defineConfig } from 'vite';
export default defineConfig({ base: './', cacheDir: 'D:/Caches/KnowledgeClipper/chat-vite',
  build: { outDir: process.env.KC_CHAT_OUTPUT || 'D:/Artifacts/KnowledgeClipper/chat-view', emptyOutDir: false,
    assetsInlineLimit: 0 } });

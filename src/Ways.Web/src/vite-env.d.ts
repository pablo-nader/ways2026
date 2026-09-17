/// <reference types="vite/client" />

interface ImportMetaEnv {
  /** Override de la URL de descarga del instalador de Ways POS (ver `src/config/descargaPos.ts`). */
  readonly VITE_URL_DESCARGA_POS?: string
}

interface ImportMeta {
  readonly env: ImportMetaEnv
}

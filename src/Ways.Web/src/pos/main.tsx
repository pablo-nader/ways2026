import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'

import 'bootstrap/dist/css/bootstrap.min.css'
import '../estilos/template.css'
import '../estilos/ways.css'
import '../estilos/impresion.css'

import { inicializarUrlServidor } from '../api/entornoTauri'
import { AppPos } from './AppPos'

// stage-desktop-pos, slice 3: hay que esperar la URL del servidor (bajo Tauri, ver
// `entornoTauri.ts`) ANTES de montar `AppPos` — si el primer `fetch` de `cliente.ts` saliera
// antes de que se resuelva, iría con la URL todavía en null y, bajo Tauri, apuntaría mal (al
// protocolo de asset en vez de a la red). Fuera de Tauri esto resuelve enseguida sin hacer IPC.
void inicializarUrlServidor().then(() => {
  createRoot(document.getElementById('root')!).render(
    <StrictMode>
      <AppPos />
    </StrictMode>,
  )
})

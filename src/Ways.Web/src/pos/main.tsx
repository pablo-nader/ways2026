import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'

import 'bootstrap/dist/css/bootstrap.min.css'
import '../estilos/template.css'
import '../estilos/ways.css'
import '../estilos/impresion.css'

import { alPerderLaSesion } from '../api/cliente'
import { inicializarUrlServidor, limpiarSesionDeCajeroPersistida, restaurarSesionDeCajeroPersistida } from '../api/entornoTauri'
import { AppPos } from './AppPos'

// stage-pos-sesion-offline: un 401 en CUALQUIER request (`cliente.ts`, `exigirRespuestaOk`) ya
// limpia el bearer EN MEMORIA y dispara este observador — acá se lo usa, además, para limpiar la
// sesión PERSISTIDA (uno de los tres disparadores de limpieza, ver el doc-comment de
// `limpiarSesionDeCajeroPersistida`). Se suscribe una sola vez, al cargar este módulo (el único
// punto de entrada de `pos.html`, nunca se desmonta) — `entornoTauri.ts` no puede suscribirse a
// esto por su cuenta sin crear un import circular (`cliente.ts` ya importa de `entornoTauri.ts`).
alPerderLaSesion(() => {
  void limpiarSesionDeCajeroPersistida()
})

// stage-desktop-pos, slice 3: hay que esperar la URL del servidor (bajo Tauri, ver
// `entornoTauri.ts`) ANTES de montar `AppPos` — si el primer `fetch` de `cliente.ts` saliera
// antes de que se resuelva, iría con la URL todavía en null y, bajo Tauri, apuntaría mal (al
// protocolo de asset en vez de a la red). Fuera de Tauri esto resuelve enseguida sin hacer IPC.
//
// stage-pos-sesion-offline: junto con eso, se restaura la sesión de cajero persistida
// (`restaurarSesionDeCajeroPersistida`) — si es válida, instala el bearer en memoria ANTES de que
// `AppPos` haga su primer `GET /auth/me` (`resolverSesionDelDispositivo`), para que ese llamado
// autentique con la sesión restaurada en vez de salir sin token. Mismo motivo que
// `inicializarUrlServidor`: tiene que resolver ANTES de montar `AppPos`, nunca después. Fuera de
// Tauri ambas resuelven enseguida sin hacer IPC.
void Promise.all([inicializarUrlServidor(), restaurarSesionDeCajeroPersistida()]).then(() => {
  createRoot(document.getElementById('root')!).render(
    <StrictMode>
      <AppPos />
    </StrictMode>,
  )
})

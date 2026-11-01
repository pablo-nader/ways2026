import '@fontsource-variable/instrument-sans'
import '@fontsource-variable/bricolage-grotesque'
import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'

import 'bootstrap/dist/css/bootstrap.min.css'
import './estilos/tema.css'
import './estilos/componentes.css'
import './estilos/template.css'
import './estilos/ways.css'
import './estilos/impresion.css'

import { App } from './App'
import { aplicarTema, leerTemaGuardado } from './tema/tema'

aplicarTema(leerTemaGuardado())

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <App />
  </StrictMode>,
)

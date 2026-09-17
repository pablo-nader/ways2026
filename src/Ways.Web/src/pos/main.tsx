import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'

import 'bootstrap/dist/css/bootstrap.min.css'
import '../estilos/template.css'
import '../estilos/ways.css'
import '../estilos/impresion.css'

import { AppPos } from './AppPos'

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <AppPos />
  </StrictMode>,
)

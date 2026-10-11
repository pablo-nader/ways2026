import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'

import 'bootstrap/dist/css/bootstrap.min.css'
import '../estilos/tema.css'

import { aplicarTema, leerTemaGuardado } from '../tema/tema'
import { Tienda } from './Tienda'

aplicarTema(leerTemaGuardado())

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <Tienda />
  </StrictMode>,
)

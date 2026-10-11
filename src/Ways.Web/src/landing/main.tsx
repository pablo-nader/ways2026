import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'

import 'bootstrap/dist/css/bootstrap.min.css'
import '../estilos/tema.css'

import { aplicarTema, leerTemaGuardado } from '../tema/tema'
import { Landing } from './Landing'

aplicarTema(leerTemaGuardado())

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <Landing />
  </StrictMode>,
)

import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'

import 'bootstrap/dist/css/bootstrap.min.css'
import '../estilos/tema.css'
import '../publico/publico.css'

import { Tienda } from './Tienda'

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <Tienda />
  </StrictMode>,
)

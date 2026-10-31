import '@fontsource-variable/instrument-sans'
import '@fontsource-variable/bricolage-grotesque'
import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'

import 'bootstrap/dist/css/bootstrap.min.css'
import '../estilos/tema.css'
import '../publico/publico.css'

import { Landing } from './Landing'

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <Landing />
  </StrictMode>,
)

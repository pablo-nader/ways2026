import { PREFIJO_ONLINE } from '../config/rutasPublicas'
import { CabeceraPublica } from '../publico/CabeceraPublica'
import { PorEstadoDeVitrina } from '../publico/PorEstadoDeVitrina'
import { useVitrinaPublica } from '../publico/useVitrinaPublica'
import { Descripcion } from './Descripcion'
import { Portada } from './Portada'
import { Sucursales } from './Sucursales'

export function Landing() {
  const { resultado, recargar } = useVitrinaPublica()

  return (
    <PorEstadoDeVitrina resultado={resultado} recargar={recargar}>
      {(vitrina) => (
        <div className="publico-pagina">
          <CabeceraPublica vitrina={vitrina}>
            <a className="publico-boton publico-boton-acento publico-boton-chico" href={PREFIJO_ONLINE}>
              Comprar online
            </a>
          </CabeceraPublica>
          <main>
            <Portada vitrina={vitrina} />
            <Descripcion texto={vitrina.descripcion} />
            <Sucursales sucursales={vitrina.sucursales} />
          </main>
        </div>
      )}
    </PorEstadoDeVitrina>
  )
}

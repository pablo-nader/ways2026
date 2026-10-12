import { PREFIJO_ONLINE } from '../config/rutasPublicas'
import { CabeceraPublica } from '../publico/CabeceraPublica'
import { PorEstadoDeVitrina } from '../publico/PorEstadoDeVitrina'
import { enlaceWhatsappDelComercio } from '../publico/enlaces'
import { useVitrinaPublica } from '../publico/useVitrinaPublica'
import { Categorias } from './Categorias'
import { Cierre } from './Cierre'
import { ComoComprar } from './ComoComprar'
import { CombosDeLaLanding } from './CombosDeLaLanding'
import { Destacados } from './Destacados'
import { Nosotros } from './Nosotros'
import { Portada } from './Portada'
import { Sucursales } from './Sucursales'
import { useCatalogoDeLaLanding } from './useCatalogoDeLaLanding'

export function Landing() {
  const { resultado, recargar } = useVitrinaPublica()
  const catalogo = useCatalogoDeLaLanding()

  return (
    <PorEstadoDeVitrina resultado={resultado} recargar={recargar}>
      {(vitrina) => {
        const whatsapp = enlaceWhatsappDelComercio(vitrina.sucursales)

        return (
          <>
            <div className="publico-pagina">
              <CabeceraPublica vitrina={vitrina}>
                <a className="publico-boton publico-boton-acento publico-boton-chico" href={PREFIJO_ONLINE}>
                  Comprar online
                </a>
              </CabeceraPublica>
            </div>
            <main>
              <div className="publico-pagina">
                <Portada vitrina={vitrina} />
                <Destacados articulos={catalogo?.articulos ?? []} />
                <CombosDeLaLanding combos={catalogo?.combos ?? []} />
                <Categorias categorias={catalogo?.categorias ?? []} />
                <Nosotros texto={vitrina.descripcion} />
                <ComoComprar conWhatsapp={whatsapp !== null} />
                <Sucursales sucursales={vitrina.sucursales} />
              </div>
              <Cierre whatsapp={whatsapp} />
            </main>
          </>
        )
      }}
    </PorEstadoDeVitrina>
  )
}

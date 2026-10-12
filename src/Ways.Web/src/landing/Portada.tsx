import type { VitrinaPublica } from '../api/tipos'
import { PREFIJO_ONLINE } from '../config/rutasPublicas'
import { Chevrones, GlifoWhatsapp } from '../publico/Glifos'
import { enlaceWhatsappDelComercio } from '../publico/enlaces'

export function Portada({ vitrina }: { vitrina: VitrinaPublica }) {
  const titular = vitrina.tagline?.trim() || vitrina.nombre
  const whatsapp = enlaceWhatsappDelComercio(vitrina.sucursales)

  return (
    <section className="publico-portada" aria-labelledby="portada-titular">
      <Chevrones className="publico-portada-chevrones" />
      <h1 id="portada-titular" className="publico-titulo publico-portada-titular">
        {titular}
      </h1>
      <div className="publico-barra" aria-hidden="true" />
      <div className="publico-portada-acciones">
        <a className="publico-boton publico-boton-acento" href={PREFIJO_ONLINE}>
          Comprar online
        </a>
        {whatsapp && (
          <a className="publico-boton publico-boton-contorno" href={whatsapp} target="_blank" rel="noopener">
            <GlifoWhatsapp />
            Escribinos por WhatsApp
          </a>
        )}
      </div>
    </section>
  )
}

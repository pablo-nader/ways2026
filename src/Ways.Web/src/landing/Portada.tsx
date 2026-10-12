import type { SucursalPublica, VitrinaPublica } from '../api/tipos'
import { PREFIJO_ONLINE } from '../config/rutasPublicas'
import { Chevrones, GlifoReloj, GlifoUbicacion, GlifoWhatsapp } from '../publico/Glifos'
import { enlaceWhatsappDelComercio } from '../publico/enlaces'
import { textoDelSello } from './horario'

function datoDe(sucursal: SucursalPublica | undefined, campo: 'horario' | 'domicilio'): string | null {
  const texto = sucursal?.[campo]?.trim()

  return texto ? texto : null
}

/** Datos prácticos de la primera sucursal, en píldoras bajo los botones; el pedido por WhatsApp sale de cualquier sucursal con número usable. */
function Pildoras({ horario, domicilio, whatsapp }: { horario: string | null; domicilio: string | null; whatsapp: boolean }) {
  if (!horario && !domicilio && !whatsapp) return null

  return (
    <ul className="publico-pildoras">
      {horario && (
        <li className="publico-pildora">
          <GlifoReloj />
          <span>{horario}</span>
        </li>
      )}
      {domicilio && (
        <li className="publico-pildora">
          <GlifoUbicacion />
          <span>{domicilio}</span>
        </li>
      )}
      {whatsapp && (
        <li className="publico-pildora">
          <GlifoWhatsapp />
          <span>Pedidos por WhatsApp</span>
        </li>
      )}
    </ul>
  )
}

/**
 * El sello repite el horario en grande, a modo de cartel; como el dato completo ya está en la
 * píldora, se oculta a la tecnología asistiva en vez de leerse dos veces. Muestra hasta tres líneas.
 */
function Sello({ horario }: { horario: string }) {
  return (
    <div className="publico-sello" aria-hidden="true">
      <Chevrones className="publico-sello-marca" />
      <p className="publico-titulo publico-sello-texto">{textoDelSello(horario)}</p>
    </div>
  )
}

export function Portada({ vitrina }: { vitrina: VitrinaPublica }) {
  const titular = vitrina.tagline?.trim() || vitrina.nombre
  const whatsapp = enlaceWhatsappDelComercio(vitrina.sucursales)
  const principal = vitrina.sucursales[0]
  const horario = datoDe(principal, 'horario')
  const domicilio = datoDe(principal, 'domicilio')

  return (
    <section
      className={horario ? 'publico-portada publico-portada-con-sello' : 'publico-portada'}
      aria-labelledby="portada-titular"
    >
      <div className="publico-portada-texto">
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
        <Pildoras horario={horario} domicilio={domicilio} whatsapp={whatsapp !== null} />
      </div>
      {horario && <Sello horario={horario} />}
    </section>
  )
}

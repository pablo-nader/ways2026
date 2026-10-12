import type { ReactNode } from 'react'

import type { SucursalPublica } from '../api/tipos'
import { GlifoFacebook, GlifoInstagram, GlifoUbicacion, GlifoWeb, GlifoWhatsapp } from '../publico/Glifos'
import { enlaceFacebook, enlaceInstagram, enlaceWeb, enlaceWhatsapp } from '../publico/enlaces'

function EnlaceExterno({ href, etiqueta, nombre, children }: { href: string; etiqueta: string; nombre: string; children: ReactNode }) {
  return (
    <a
      className="publico-boton publico-boton-contorno publico-boton-chico"
      href={href}
      target="_blank"
      rel="noopener"
      aria-label={`${etiqueta} de ${nombre}`}
    >
      {children}
      {etiqueta}
    </a>
  )
}

/** Mapa de Google del domicilio. `output=embed` es la forma sin clave de API; el domicilio viaja codificado en la consulta. */
function urlDelMapa(domicilio: string): string {
  return `https://www.google.com/maps?q=${encodeURIComponent(domicilio)}&output=embed`
}

/** Enlace de respaldo a Google Maps, por si el mapa embebido no carga (bloqueador, red o política del navegador). */
function urlDeBusquedaEnMaps(domicilio: string): string {
  return `https://www.google.com/maps/search/?api=1&query=${encodeURIComponent(domicilio)}`
}

/**
 * El mapa es contenido de terceros: el `sandbox` le deja ejecutar su propio script y abrir enlaces en pestañas
 * nuevas, pero no navegar esta página ni enviar formularios. Sin `referrerPolicy` rige la del navegador
 * (`strict-origin-when-cross-origin`).
 */
function Mapa({ nombre, domicilio }: { nombre: string; domicilio: string }) {
  return (
    <>
      <iframe
        className="publico-mapa"
        title={`Mapa de ${nombre}`}
        src={urlDelMapa(domicilio)}
        loading="lazy"
        sandbox="allow-scripts allow-same-origin allow-popups allow-popups-to-escape-sandbox"
      />
      <div className="publico-tarjeta-enlaces">
        <EnlaceExterno href={urlDeBusquedaEnMaps(domicilio)} etiqueta="Abrir en Google Maps" nombre={nombre}>
          <GlifoUbicacion />
        </EnlaceExterno>
      </div>
    </>
  )
}

function TarjetaDeSucursal({ sucursal }: { sucursal: SucursalPublica }) {
  const whatsapp = enlaceWhatsapp(sucursal.whatsapp)
  const instagram = enlaceInstagram(sucursal.instagram)
  const facebook = enlaceFacebook(sucursal.facebook)
  const web = enlaceWeb(sucursal.web)
  const domicilio = sucursal.domicilio?.trim()

  return (
    <li className="publico-tarjeta">
      <h3 className="publico-tarjeta-nombre">{sucursal.nombre}</h3>
      {domicilio && <p className="publico-tarjeta-dato">{sucursal.domicilio}</p>}
      {sucursal.horario && <p className="publico-tarjeta-dato">{sucursal.horario}</p>}
      {(whatsapp || instagram || facebook || web) && (
        <div className="publico-tarjeta-enlaces">
          {whatsapp && (
            <EnlaceExterno href={whatsapp} etiqueta="WhatsApp" nombre={sucursal.nombre}>
              <GlifoWhatsapp />
            </EnlaceExterno>
          )}
          {instagram && (
            <EnlaceExterno href={instagram} etiqueta="Instagram" nombre={sucursal.nombre}>
              <GlifoInstagram />
            </EnlaceExterno>
          )}
          {facebook && (
            <EnlaceExterno href={facebook} etiqueta="Facebook" nombre={sucursal.nombre}>
              <GlifoFacebook />
            </EnlaceExterno>
          )}
          {web && (
            <EnlaceExterno href={web} etiqueta="Web" nombre={sucursal.nombre}>
              <GlifoWeb />
            </EnlaceExterno>
          )}
        </div>
      )}
      {domicilio && <Mapa nombre={sucursal.nombre} domicilio={domicilio} />}
    </li>
  )
}

export function Sucursales({ sucursales }: { sucursales: readonly SucursalPublica[] }) {
  if (sucursales.length === 0) return null

  return (
    <section className="publico-seccion" aria-labelledby="sucursales-titulo">
      <h2 id="sucursales-titulo" className="publico-titulo publico-seccion-titulo">
        Dónde estamos
      </h2>
      <div className="publico-barra publico-barra-fina" aria-hidden="true" />
      <ul className="publico-tarjetas">
        {sucursales.map((sucursal, indice) => (
          <TarjetaDeSucursal key={`${indice}-${sucursal.nombre}`} sucursal={sucursal} />
        ))}
      </ul>
    </section>
  )
}

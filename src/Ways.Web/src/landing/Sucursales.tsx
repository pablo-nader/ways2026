import type { ReactNode } from 'react'

import type { SucursalPublica } from '../api/tipos'
import { GlifoFacebook, GlifoInstagram, GlifoWeb, GlifoWhatsapp } from '../publico/Glifos'
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

function TarjetaDeSucursal({ sucursal }: { sucursal: SucursalPublica }) {
  const whatsapp = enlaceWhatsapp(sucursal.whatsapp)
  const instagram = enlaceInstagram(sucursal.instagram)
  const facebook = enlaceFacebook(sucursal.facebook)
  const web = enlaceWeb(sucursal.web)

  return (
    <li className="publico-tarjeta">
      <h3 className="publico-tarjeta-nombre">{sucursal.nombre}</h3>
      {sucursal.domicilio && <p className="publico-tarjeta-dato">{sucursal.domicilio}</p>}
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
    </li>
  )
}

export function Sucursales({ sucursales }: { sucursales: readonly SucursalPublica[] }) {
  if (sucursales.length === 0) return null

  return (
    <section className="publico-seccion" aria-labelledby="sucursales-titulo">
      <h2 id="sucursales-titulo" className="publico-titulo publico-seccion-titulo">
        Sucursales
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

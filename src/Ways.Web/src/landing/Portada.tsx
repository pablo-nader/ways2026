import type { ReactNode } from 'react'

import type { SucursalPublica, VitrinaPublica } from '../api/tipos'
import { PREFIJO_ONLINE } from '../config/rutasPublicas'
import { Chevrones, GlifoReloj, GlifoUbicacion, GlifoWhatsapp } from '../publico/Glifos'
import { enlaceWhatsapp, enlaceWhatsappDelComercio } from '../publico/enlaces'
import { textoDelSello } from './horario'

/** Un dato práctico y la sucursal de la que sale; `sucursal` solo viene cuando el comercio tiene más de una. */
interface DatoDePildora {
  texto: string
  sucursal: string | null
}

interface DatosDeLasPildoras {
  horario: DatoDePildora | null
  domicilio: DatoDePildora | null
  whatsapp: DatoDePildora | null
}

function textoDe(sucursal: SucursalPublica | undefined, campo: 'horario' | 'domicilio'): string | null {
  const texto = sucursal?.[campo]?.trim()

  return texto ? texto : null
}

function nombreSiHayVarias(sucursal: SucursalPublica, sucursales: readonly SucursalPublica[]): string | null {
  return sucursales.length > 1 ? sucursal.nombre.trim() || null : null
}

/**
 * Horario y domicilio son los de la primera sucursal. El WhatsApp sale de esa misma sucursal si tiene un número
 * usable y, si no, de la primera que lo tenga (la misma del botón). Con más de una sucursal cada dato lleva su nombre.
 */
function datosDeLasPildoras(sucursales: readonly SucursalPublica[]): DatosDeLasPildoras {
  const principal = sucursales[0]
  // La primera sucursal con número usable es la principal si lo tiene y, si no, la del botón de WhatsApp.
  const conWhatsapp = sucursales.find((sucursal) => enlaceWhatsapp(sucursal.whatsapp) !== null)

  function dato(sucursal: SucursalPublica | undefined, texto: string | null): DatoDePildora | null {
    return sucursal && texto ? { texto, sucursal: nombreSiHayVarias(sucursal, sucursales) } : null
  }

  return {
    horario: dato(principal, textoDe(principal, 'horario')),
    domicilio: dato(principal, textoDe(principal, 'domicilio')),
    whatsapp: dato(conWhatsapp, 'WhatsApp'),
  }
}

function Pildora({ etiqueta, dato, glifo }: { etiqueta: string; dato: DatoDePildora; glifo: ReactNode }) {
  return (
    <li className="publico-pildora">
      {glifo}
      <span>
        <span className="publico-solo-lectores">{etiqueta}: </span>
        {dato.sucursal ? `${dato.sucursal} · ${dato.texto}` : dato.texto}
      </span>
    </li>
  )
}

function Pildoras({ horario, domicilio, whatsapp }: DatosDeLasPildoras) {
  if (!horario && !domicilio && !whatsapp) return null

  return (
    <ul className="publico-pildoras">
      {horario && <Pildora etiqueta="Horario" dato={horario} glifo={<GlifoReloj />} />}
      {domicilio && <Pildora etiqueta="Dirección" dato={domicilio} glifo={<GlifoUbicacion />} />}
      {whatsapp && <Pildora etiqueta="Contacto" dato={whatsapp} glifo={<GlifoWhatsapp />} />}
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
  const horario = textoDe(vitrina.sucursales[0], 'horario')

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
        <Pildoras {...datosDeLasPildoras(vitrina.sucursales)} />
      </div>
      {horario && <Sello horario={horario} />}
    </section>
  )
}

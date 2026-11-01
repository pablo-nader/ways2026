import type { ModoPuntoVenta, PuntoVentaListado } from '../../api/tipos'

/** Edición descriptiva de un punto de venta: los textos opcionales viajan como `''` (sin informar). */
export type FormularioDePuntoVenta = {
  id: number
  nombre: string
  domicilio: string
  horario: string
  whatsapp: string
  instagram: string
  facebook: string
  web: string
  envioPorDefecto: boolean
}

/** Alta de un punto de venta. `idEmpresa` y `modo` arrancan en '' (sin elegir) a propósito: el
 * modo nunca se infiere (docs/10 §9.1) y la empresa no tiene un valor razonable por defecto. */
export type FormularioDeAltaDePuntoVenta = {
  idEmpresa: string
  /** Sucursal donde nace la caja; '' = una sucursal nueva con el nombre del punto de venta. */
  idSucursal: string
  nombre: string
  modo: ModoPuntoVenta | ''
  domicilio: string
  horario: string
  whatsapp: string
  instagram: string
  facebook: string
  web: string
}

/** Cambio de modo: precondición y rastro de auditoría propios, aparte de la edición descriptiva. */
export type FormularioDeModoDePuntoVenta = { id: number; nombre: string; modo: ModoPuntoVenta }

/** Mover la caja a otra sucursal de su empresa: endpoint, precondiciones y rastro propios, como el modo.
 * `idSucursal` es la elegida ('' = ninguna todavía); `idSucursalActual` la que tiene hoy. */
export type FormularioDeSucursalDePuntoVenta = {
  id: number
  nombre: string
  idEmpresa: number
  idSucursalActual: number | null
  idSucursal: string
}

export function aFormularioDeSucursalDePuntoVenta(p: PuntoVentaListado): FormularioDeSucursalDePuntoVenta {
  return {
    id: p.id,
    nombre: p.nombre,
    idEmpresa: p.idEmpresa,
    idSucursalActual: p.idSucursal ?? null,
    idSucursal: p.idSucursal === undefined ? '' : String(p.idSucursal),
  }
}

export const ALTA_VACIA: FormularioDeAltaDePuntoVenta = {
  idEmpresa: '',
  idSucursal: '',
  nombre: '',
  modo: '',
  domicilio: '',
  horario: '',
  whatsapp: '',
  instagram: '',
  facebook: '',
  web: '',
}

export const CAMPOS_DESCRIPTIVOS_DE_ALTA = [
  { clave: 'domicilio', etiqueta: 'Domicilio', max: 255, columnas: 'col-md-4' },
  { clave: 'horario', etiqueta: 'Horario', max: 255, columnas: 'col-md-3' },
  { clave: 'whatsapp', etiqueta: 'WhatsApp', max: 30, columnas: 'col-md-2' },
  { clave: 'instagram', etiqueta: 'Instagram', max: 150, columnas: 'col-md-3' },
  { clave: 'facebook', etiqueta: 'Facebook', max: 150, columnas: 'col-md-3' },
  { clave: 'web', etiqueta: 'Sitio web', max: 255, columnas: 'col-md-3' },
] as const

export function aFormularioDePuntoVenta(p: PuntoVentaListado): FormularioDePuntoVenta {
  return {
    id: p.id,
    nombre: p.nombre,
    domicilio: p.domicilio ?? '',
    horario: p.horario ?? '',
    whatsapp: p.whatsapp ?? '',
    instagram: p.instagram ?? '',
    facebook: p.facebook ?? '',
    web: p.web ?? '',
    envioPorDefecto: p.envioPorDefecto ?? false,
  }
}

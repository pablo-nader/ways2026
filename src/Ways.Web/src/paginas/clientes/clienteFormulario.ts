import type { AltaCliente, ClienteListado, TipoDocumento } from '../../api/tipos'

export type Formulario = {
  id: number | null
  nombre: string
  apellido: string
  razonSocial: string
  tipoDocumento: TipoDocumento | ''
  numeroDocumento: string
  idCondicionFiscal: number | ''
  nacimiento: string
  domicilio: string
  telefono: string
  celular: string
  email: string
  observaciones: string
  idListaPrecio: number | ''
  limiteCredito: number | null
  creditoIlimitado: boolean
  activo: boolean
  /** Empresa de la fila que se edita; null en un alta y en un cliente de todo el tenant. */
  idEmpresa: number | null
}

export function formularioVacio(idListaPrecioPorDefecto: number | ''): Formulario {
  return {
    id: null,
    nombre: '',
    apellido: '',
    razonSocial: '',
    tipoDocumento: '',
    numeroDocumento: '',
    idCondicionFiscal: '',
    nacimiento: '',
    domicilio: '',
    telefono: '',
    celular: '',
    email: '',
    observaciones: '',
    idListaPrecio: idListaPrecioPorDefecto,
    limiteCredito: 0,
    creditoIlimitado: false,
    activo: true,
    idEmpresa: null,
  }
}

export function aFormulario(c: ClienteListado): Formulario {
  return {
    id: c.id,
    nombre: c.nombre,
    apellido: c.apellido ?? '',
    razonSocial: c.razonSocial ?? '',
    tipoDocumento: c.tipoDocumento ?? '',
    numeroDocumento: c.numeroDocumento ?? '',
    idCondicionFiscal: c.idCondicionFiscal,
    nacimiento: c.nacimiento ?? '',
    domicilio: c.domicilio ?? '',
    telefono: c.telefono ?? '',
    celular: c.celular ?? '',
    email: c.email ?? '',
    observaciones: c.observaciones ?? '',
    idListaPrecio: c.idListaPrecio,
    limiteCredito: c.limiteCredito,
    creditoIlimitado: c.creditoIlimitado,
    activo: c.activo,
    idEmpresa: c.idEmpresa,
  }
}

function aVacioNulo(valor: string): string | null {
  const limpio = valor.trim()
  return limpio === '' ? null : limpio
}

export function aAlta(f: Formulario): AltaCliente {
  return {
    nombre: f.nombre.trim(),
    apellido: aVacioNulo(f.apellido),
    razonSocial: aVacioNulo(f.razonSocial),
    tipoDocumento: f.tipoDocumento === '' ? null : f.tipoDocumento,
    numeroDocumento: aVacioNulo(f.numeroDocumento),
    idCondicionFiscal: f.idCondicionFiscal === '' ? 0 : f.idCondicionFiscal,
    nacimiento: f.nacimiento === '' ? null : f.nacimiento,
    domicilio: aVacioNulo(f.domicilio),
    telefono: aVacioNulo(f.telefono),
    celular: aVacioNulo(f.celular),
    email: aVacioNulo(f.email),
    observaciones: aVacioNulo(f.observaciones),
    idListaPrecio: f.idListaPrecio === '' ? 0 : f.idListaPrecio,
    limiteCredito: f.limiteCredito ?? 0,
    creditoIlimitado: f.creditoIlimitado,
    idEmpresa: f.idEmpresa,
    activo: f.activo,
  }
}

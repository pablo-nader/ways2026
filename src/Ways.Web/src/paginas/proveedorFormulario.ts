import type { AltaProveedor, ProveedorListado } from '../api/tipos'

/** Estado del formulario del ABM de proveedores: texto para los campos libres, `''` para los
 * numéricos vacíos. `aAlta` lo convierte al cuerpo del request (vacío → `null`). */
export type Formulario = {
  id: number | null
  razonSocial: string
  nombreFantasia: string
  cuit: string
  idCondicionFiscal: number | ''
  domicilio: string
  telefono: string
  email: string
  vendedor: string
  celularVendedor: string
  supervisor: string
  celularSupervisor: string
  margen: string
  observaciones: string
  activo: boolean
  percibeIibb: boolean
  percibeIva: boolean
  preciosIncluyenIva: boolean
}

export function formularioVacio(): Formulario {
  return {
    id: null,
    razonSocial: '',
    nombreFantasia: '',
    cuit: '',
    idCondicionFiscal: '',
    domicilio: '',
    telefono: '',
    email: '',
    vendedor: '',
    celularVendedor: '',
    supervisor: '',
    celularSupervisor: '',
    margen: '',
    observaciones: '',
    activo: true,
    percibeIibb: false,
    percibeIva: false,
    preciosIncluyenIva: false,
  }
}

export function aFormulario(p: ProveedorListado): Formulario {
  return {
    id: p.id,
    razonSocial: p.razonSocial,
    nombreFantasia: p.nombreFantasia ?? '',
    cuit: p.cuit ?? '',
    idCondicionFiscal: p.idCondicionFiscal,
    domicilio: p.domicilio ?? '',
    telefono: p.telefono ?? '',
    email: p.email ?? '',
    vendedor: p.vendedor ?? '',
    celularVendedor: p.celularVendedor ?? '',
    supervisor: p.supervisor ?? '',
    celularSupervisor: p.celularSupervisor ?? '',
    margen: p.margen === null ? '' : String(p.margen),
    observaciones: p.observaciones ?? '',
    activo: p.activo,
    percibeIibb: p.percibeIibb,
    percibeIva: p.percibeIva,
    preciosIncluyenIva: p.preciosIncluyenIva,
  }
}

function aVacioNulo(valor: string): string | null {
  const limpio = valor.trim()
  return limpio === '' ? null : limpio
}

export function aAlta(f: Formulario): AltaProveedor {
  return {
    razonSocial: f.razonSocial.trim(),
    nombreFantasia: aVacioNulo(f.nombreFantasia),
    cuit: aVacioNulo(f.cuit),
    idCondicionFiscal: f.idCondicionFiscal === '' ? 0 : f.idCondicionFiscal,
    domicilio: aVacioNulo(f.domicilio),
    telefono: aVacioNulo(f.telefono),
    email: aVacioNulo(f.email),
    vendedor: aVacioNulo(f.vendedor),
    celularVendedor: aVacioNulo(f.celularVendedor),
    supervisor: aVacioNulo(f.supervisor),
    celularSupervisor: aVacioNulo(f.celularSupervisor),
    margen: f.margen.trim() === '' ? null : Number(f.margen),
    observaciones: aVacioNulo(f.observaciones),
    idEmpresa: null,
    activo: f.activo,
    percibeIibb: f.percibeIibb,
    percibeIva: f.percibeIva,
    preciosIncluyenIva: f.preciosIncluyenIva,
  }
}

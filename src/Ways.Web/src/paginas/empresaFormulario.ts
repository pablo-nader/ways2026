import type { EmpresaEdicion, EmpresaListado } from '../api/tipos'

/** Estado del formulario de edición de empresa: las alícuotas de percepción viajan como texto
 * (`''` = sin informar) para no pisar lo que el operador está tipeando. */
export type FormularioDeEmpresa = {
  id: number
  razonSocial: string
  nombreFantasia: string
  cuit: string
  alicuotaPercepcionIibb: string
  alicuotaPercepcionIva: string
}

export const MENSAJE_ALICUOTA_DE_PERCEPCION_INVALIDA =
  'Las alícuotas de percepción tienen que estar entre 0 y 100, con hasta 3 decimales.'

export function aFormularioDeEmpresa(e: EmpresaListado): FormularioDeEmpresa {
  return {
    id: e.id,
    razonSocial: e.razonSocial,
    nombreFantasia: e.nombreFantasia ?? '',
    cuit: e.cuit ?? '',
    alicuotaPercepcionIibb: e.alicuotaPercepcionIibb === null ? '' : String(e.alicuotaPercepcionIibb),
    alicuotaPercepcionIva: e.alicuotaPercepcionIva === null ? '' : String(e.alicuotaPercepcionIva),
  }
}

/** `''` es "sin informar" (`null`); cualquier otro texto tiene que ser un porcentaje de 0 a 100 con
 * hasta 3 decimales (lo que guarda `numeric(6,3)`). `undefined` marca un valor inválido. */
function aAlicuota(texto: string): number | null | undefined {
  const limpio = texto.trim()
  if (limpio === '') return null

  const valor = Number(limpio.replace(',', '.'))
  if (!Number.isFinite(valor) || valor < 0 || valor > 100) return undefined
  if (Math.round(valor * 1000) / 1000 !== valor) return undefined

  return valor
}

export function alicuotasDePercepcionValidas(f: FormularioDeEmpresa): boolean {
  return aAlicuota(f.alicuotaPercepcionIibb) !== undefined && aAlicuota(f.alicuotaPercepcionIva) !== undefined
}

/** El formulario → cuerpo del `PUT /api/empresas/{id}`. Quien lo llama verifica antes con
 * `alicuotasDePercepcionValidas`: un texto inválido no tiene valor que enviar y acá queda en `null`. */
export function aEdicionDeEmpresa(f: FormularioDeEmpresa): EmpresaEdicion {
  return {
    razonSocial: f.razonSocial,
    nombreFantasia: f.nombreFantasia || null,
    cuit: f.cuit || null,
    alicuotaPercepcionIibb: aAlicuota(f.alicuotaPercepcionIibb) ?? null,
    alicuotaPercepcionIva: aAlicuota(f.alicuotaPercepcionIva) ?? null,
  }
}

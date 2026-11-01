/**
 * Textos de la sección "Ofertas" del artículo (helpers puros, probados sin montar el componente).
 * El beneficio reusa `textoDeBeneficio` de la pantalla de ofertas para que ambas lo escriban igual.
 */
import { formularioOfertaVacio, type FormularioOferta } from '../../api/ofertas'
import { DIAS_SEMANA } from '../../api/tipos'
import type { OfertaListado } from '../../api/tipos'
import { isoATexto } from '../../formato/fechas'

/** Sin el nombre de la familia del artículo (todavía no cargó), se rotula por número. */
export function textoDeAlcanceEnArticulo(o: OfertaListado, nombreDeLaFamilia: string | null): string {
  if (o.idArticulo !== null) return 'Este artículo'

  return `Familia: ${nombreDeLaFamilia ?? `#${o.idFamilia}`}`
}

export function textoDeCondicion(o: OfertaListado): string {
  if (o.cantidadMinima === null) return '—'

  return o.soloMultiplos ? `Mínimo ${o.cantidadMinima}, solo múltiplos` : `Mínimo ${o.cantidadMinima}`
}

function recortarHora(hora: string): string {
  return hora.slice(0, 5)
}

/** Fechas, horas y días en una sola línea; una oferta sin restricciones de tiempo vale siempre. */
export function textoDeVigencia(o: OfertaListado): string {
  const partes: string[] = []

  if (o.fechaDesde !== null && o.fechaHasta !== null) partes.push(`${isoATexto(o.fechaDesde)} al ${isoATexto(o.fechaHasta)}`)
  else if (o.fechaDesde !== null) partes.push(`desde ${isoATexto(o.fechaDesde)}`)
  else if (o.fechaHasta !== null) partes.push(`hasta ${isoATexto(o.fechaHasta)}`)

  if (o.horaDesde !== null && o.horaHasta !== null) partes.push(`${recortarHora(o.horaDesde)} a ${recortarHora(o.horaHasta)} h`)
  else if (o.horaDesde !== null) partes.push(`desde ${recortarHora(o.horaDesde)} h`)
  else if (o.horaHasta !== null) partes.push(`hasta ${recortarHora(o.horaHasta)} h`)

  if (o.diasSemana.length > 0) {
    const dias = [...o.diasSemana]
      .sort((a, b) => a - b)
      .map((d) => DIAS_SEMANA.find((x) => x.valor === d)?.etiqueta.slice(0, 3) ?? String(d))
    partes.push(dias.join(', '))
  }

  return partes.length === 0 ? 'Siempre' : partes.join(' · ')
}

export function formularioDeOfertaParaArticulo(idArticulo: number, nombreArticulo: string): FormularioOferta {
  return { ...formularioOfertaVacio(), alcance: 'Articulo', idArticulo, nombreArticulo }
}

export function formularioDeOfertaParaFamilia(idFamilia: number): FormularioOferta {
  return { ...formularioOfertaVacio(), alcance: 'Familia', idFamilia }
}

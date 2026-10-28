import type { FiltrosDePadron } from '../../api/padron'
import { BotonIcono } from '../BotonIcono'
import { CampoImporte } from '../CampoImporte'

export type OpcionDeFiltroDePadron = { valor: string; etiqueta: string }

/** Una celda de la fila de filtros, en el orden de las columnas del `<thead>`. Las claves son las del objeto de
 * filtros de la pantalla, que son también los parámetros del servidor. */
export type CeldaDeFiltroDePadron<F extends FiltrosDePadron> =
  /** Texto que la columna contiene, sin mayúsculas ni acentos. */
  | { tipo: 'texto'; clave: keyof F & string; etiqueta: string }
  /** Número entero exacto: solo deja tipear dígitos. */
  | { tipo: 'entero'; clave: keyof F & string; etiqueta: string }
  | { tipo: 'opciones'; clave: keyof F & string; etiqueta: string; opciones: readonly OpcionDeFiltroDePadron[] }
  /** Rango inclusivo de un importe: dos campos, desde y hasta. */
  | { tipo: 'rango'; claveDesde: keyof F & string; claveHasta: keyof F & string; etiqueta: string }
  /** La celda de acciones: lleva el botón que limpia los filtros. */
  | { tipo: 'acciones' }
  | { tipo: 'vacia' }

type Props<F extends FiltrosDePadron> = {
  celdas: readonly CeldaDeFiltroDePadron<F>[]
  valores: F
  /** Los selectores aplican en el acto; los textos y los rangos esperan la demora del hook. */
  onCambio: (cambios: Partial<F>, enElActo: boolean) => void
  onLimpiar: () => void
  hayFiltros: boolean
  deshabilitado: boolean
}

function comoTexto(valor: unknown): string {
  return typeof valor === 'string' ? valor : ''
}

function comoNumero(valor: unknown): number | null {
  return typeof valor === 'number' ? valor : null
}

/** Segunda fila del `<thead>` de un padrón que filtra en el servidor: un control por columna, siempre montado. */
export function FilaDeFiltrosDePadron<F extends FiltrosDePadron>({
  celdas,
  valores,
  onCambio,
  onLimpiar,
  hayFiltros,
  deshabilitado,
}: Props<F>) {
  function cambiar(clave: keyof F & string, valor: string | number | null, enElActo: boolean) {
    onCambio({ [clave]: valor } as Partial<F>, enElActo)
  }

  return (
    <tr>
      {celdas.map((celda, posicion) => {
        switch (celda.tipo) {
          case 'texto':
            return (
              <th key={posicion} scope="col">
                <input
                  type="search"
                  className="form-control form-control-sm"
                  aria-label={`Filtrar por ${celda.etiqueta}`}
                  value={comoTexto(valores[celda.clave])}
                  disabled={deshabilitado}
                  onChange={(e) => cambiar(celda.clave, e.target.value, false)}
                />
              </th>
            )
          case 'entero':
            return (
              <th key={posicion} scope="col">
                <input
                  type="search"
                  inputMode="numeric"
                  className="form-control form-control-sm"
                  aria-label={`Filtrar por ${celda.etiqueta}`}
                  value={comoTexto(valores[celda.clave])}
                  disabled={deshabilitado}
                  onChange={(e) => {
                    // Un texto que no es un entero, o que pasa de nueve dígitos (más de eso puede salirse del rango
                    // de un int y el servidor lo rechazaría con 400), no se acepta: el campo conserva lo anterior.
                    if (/^\d{0,9}$/.test(e.target.value)) cambiar(celda.clave, e.target.value, false)
                  }}
                />
              </th>
            )
          case 'opciones':
            return (
              <th key={posicion} scope="col">
                <select
                  className="form-select form-select-sm"
                  aria-label={`Filtrar por ${celda.etiqueta}`}
                  value={comoTexto(valores[celda.clave])}
                  disabled={deshabilitado}
                  onChange={(e) => cambiar(celda.clave, e.target.value, true)}
                >
                  <option value="">Todos</option>
                  {celda.opciones.map((o) => (
                    <option key={o.valor} value={o.valor}>
                      {o.etiqueta}
                    </option>
                  ))}
                </select>
              </th>
            )
          case 'rango':
            return (
              <th key={posicion} scope="col">
                <div className="d-flex gap-1">
                  <CampoImporte
                    aria-label={`${celda.etiqueta} desde`}
                    className="form-control form-control-sm"
                    valor={comoNumero(valores[celda.claveDesde])}
                    disabled={deshabilitado}
                    onChange={(valor) => cambiar(celda.claveDesde, valor, false)}
                  />
                  <CampoImporte
                    aria-label={`${celda.etiqueta} hasta`}
                    className="form-control form-control-sm"
                    valor={comoNumero(valores[celda.claveHasta])}
                    disabled={deshabilitado}
                    onChange={(valor) => cambiar(celda.claveHasta, valor, false)}
                  />
                </div>
              </th>
            )
          case 'acciones':
            return (
              <th key={posicion} scope="col" className="text-end">
                <BotonIcono icono="limpiar" onClick={onLimpiar} disabled={deshabilitado || !hayFiltros} />
              </th>
            )
          case 'vacia':
            return <th key={posicion} scope="col" />
        }
      })}
    </tr>
  )
}

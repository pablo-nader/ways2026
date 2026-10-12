import { opcionesDeFamiliaDeOferta } from '../../api/ofertas'
import {
  COMPONENTES_MAXIMOS,
  filaDeComponenteVacia,
  MENSAJE_COMPONENTE_NO_DISPONIBLE,
  UNIDADES_MINIMAS,
  type ErrorDeFila,
  type FilaDeComponente,
  type TipoDeComponente,
} from '../../api/combosAbm'
import type { FamiliaListado } from '../../api/tipos'
import { BotonIcono } from '../../componentes/BotonIcono'
import { SelectorDeArticuloDeCombo } from './SelectorDeArticuloDeCombo'

type Props = {
  filas: FilaDeComponente[]
  familias: FamiliaListado[]
  /** Errores por `clave` de fila ya filtrados por quien llama (los de validación solo se ven tras un intento de guardar). */
  errores: Record<number, ErrorDeFila>
  /** Error del conjunto (mínimo de unidades, tope, rechazos del servidor sin fila identificable). */
  errorDelConjunto?: string
  resumen: string
  disabled: boolean
  onCambio: (filas: FilaDeComponente[]) => void
}

/** Editor de las filas `[tipo] [artículo o familia] [cantidad]` de un combo. Cada fila es un grupo con nombre propio
 * para que sus controles se distingan y se puedan ubicar por fila. */
export function EditorDeComponentes({ filas, familias, errores, errorDelConjunto, resumen, disabled, onCambio }: Props) {
  const opcionesFamilia = opcionesDeFamiliaDeOferta(familias)

  function cambiarFila(clave: number, cambio: Partial<FilaDeComponente>) {
    onCambio(filas.map((f) => (f.clave === clave ? { ...f, ...cambio } : f)))
  }

  function cambiarTipo(fila: FilaDeComponente, tipo: TipoDeComponente) {
    if (tipo === fila.tipo) return
    cambiarFila(fila.clave, { tipo, idArticulo: '', idFamilia: '', nombre: '', disponible: true })
  }

  return (
    <fieldset className="border-0 p-0 m-0" disabled={disabled}>
      <legend className="h6 mb-1">Componentes</legend>
      <div className="form-text mb-2">
        Cada componente es un artículo o una familia (sirve cualquier artículo de la familia) con su cantidad. Solo se pueden usar
        artículos que se venden por unidad. El combo suma al menos {UNIDADES_MINIMAS} unidades.
      </div>

      {filas.map((fila, indice) => {
        const numero = indice + 1
        const error = errores[fila.clave]
        const errorDeReferencia = error?.referencia ?? (fila.disponible ? undefined : MENSAJE_COMPONENTE_NO_DISPONIBLE)
        const familiaFaltante = fila.idFamilia !== '' && !familias.some((f) => f.id === fila.idFamilia)

        return (
          <div key={fila.clave} role="group" aria-label={`Componente ${numero}`} className="border rounded p-2 mb-2">
            <div className="row g-2 align-items-start">
              <div className="col-md-2">
                <select
                  className="form-select form-select-sm"
                  aria-label={`Tipo del componente ${numero}`}
                  value={fila.tipo}
                  onChange={(e) => cambiarTipo(fila, e.target.value as TipoDeComponente)}
                >
                  <option value="Articulo">Artículo</option>
                  <option value="Familia">Familia</option>
                </select>
              </div>

              <div className="col-md-6">
                {fila.tipo === 'Articulo' ? (
                  <SelectorDeArticuloDeCombo
                    numero={numero}
                    idArticulo={fila.idArticulo}
                    nombre={fila.nombre}
                    disabled={disabled}
                    onSeleccionar={(id, nombre) => cambiarFila(fila.clave, { idArticulo: id, nombre, disponible: true })}
                  />
                ) : (
                  <select
                    className="form-select form-select-sm"
                    aria-label={`Familia del componente ${numero}`}
                    value={fila.idFamilia}
                    onChange={(e) => {
                      const id = Number(e.target.value)
                      cambiarFila(fila.clave, {
                        idFamilia: id,
                        nombre: familias.find((f) => f.id === id)?.nombre ?? '',
                        disponible: true,
                      })
                    }}
                  >
                    <option value="" disabled>
                      Elegir…
                    </option>
                    {familiaFaltante && <option value={fila.idFamilia}>Familia #{fila.idFamilia} (no disponible)</option>}
                    {opcionesFamilia.map((o) => (
                      <option key={o.valor} value={o.valor}>
                        {o.etiqueta}
                      </option>
                    ))}
                  </select>
                )}
              </div>

              <div className="col-6 col-md-2">
                <input
                  type="number"
                  min="1"
                  step="1"
                  className={`form-control form-control-sm ${error?.cantidad ? 'is-invalid' : ''}`}
                  aria-label={`Cantidad del componente ${numero}`}
                  value={fila.cantidad}
                  onChange={(e) => cambiarFila(fila.clave, { cantidad: e.target.value })}
                />
              </div>

              <div className="col-6 col-md-2 text-end">
                <BotonIcono
                  icono="eliminar"
                  etiqueta={`Quitar componente ${numero}`}
                  disabled={disabled}
                  onClick={() => onCambio(filas.filter((f) => f.clave !== fila.clave))}
                />
              </div>
            </div>

            {errorDeReferencia && <div className="text-danger small mt-1">{errorDeReferencia}</div>}
            {error?.cantidad && <div className="text-danger small mt-1">{error.cantidad}</div>}
          </div>
        )
      })}

      <button
        type="button"
        className="btn btn-sm btn-outline-primary"
        disabled={disabled || filas.length >= COMPONENTES_MAXIMOS}
        onClick={() => onCambio([...filas, filaDeComponenteVacia()])}
      >
        Agregar componente
      </button>

      {errorDelConjunto && <div className="text-danger small mt-2">{errorDelConjunto}</div>}
      {resumen && (
        <p className="mt-2 mb-0" aria-live="polite">
          Combo: <strong>{resumen}</strong>
        </p>
      )}
    </fieldset>
  )
}

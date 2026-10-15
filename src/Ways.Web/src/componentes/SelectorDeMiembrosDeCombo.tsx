import { useEffect, useRef, useState } from 'react'
import { atributosDeCampoDeCantidad, restriccionDeCantidad } from '../api/cantidadPorUnidad'
import type { ArticuloEscaneado } from '../api/tipos'
import { parsearNumeroDecimal } from '../formato/numeros'
import {
  componentesAElegir,
  formatearCantidadDeCombo,
  resolverLineasDeCombo,
  totalElegido,
  type ComboParaCargar,
  type EleccionDeMiembros,
} from '../pos/cargaDeCombos'
import { Modal } from './Modal'

export type PropsSelectorDeMiembrosDeCombo = {
  combo: ComboParaCargar
  /** Las líneas de TODO el combo (también las de los componentes que no necesitaron elegir), listas para agregar. */
  onConfirmar: (lineas: ArticuloEscaneado[]) => void
  /** Descartar el combo entero: no se agrega ninguna línea. */
  onCancelar: () => void
}

/**
 * Reparto de la cantidad de los componentes de familia entre sus miembros, en UN solo diálogo para todo el combo. Cada
 * componente con varios candidatos pide que el total elegido sea exactamente su cantidad; "Agregar combo" queda
 * deshabilitado hasta que todos cierren. El campo de cada miembro sigue su unidad de venta: enteros si se vende por
 * unidad, kilos con hasta tres decimales si se vende por peso. Cancelar (botón, Escape o ×) no agrega nada: un combo se
 * carga entero o no se carga. Los componentes con un solo candidato no preguntan nada y se listan aparte.
 */
export function SelectorDeMiembrosDeCombo({ combo, onConfirmar, onCancelar }: PropsSelectorDeMiembrosDeCombo) {
  const [elecciones, setElecciones] = useState<EleccionDeMiembros>({})
  // El texto tal cual se tipea: "0," todavía no es un número cerrado y no se puede perder a mitad de tipeo.
  const [textos, setTextos] = useState<Record<string, string>>({})
  // Reentrancia de primera línea: un segundo Enter o clic antes de que el cierre se comite no vuelve a confirmar.
  const confirmadoRef = useRef(false)
  const primerCampoRef = useRef<HTMLInputElement>(null)

  // El modal enfoca la × por ser lo primero enfocable; acá lo útil es el primer campo.
  useEffect(() => {
    primerCampoRef.current?.focus()
  }, [])

  const aElegir = componentesAElegir(combo)
  const resolucion = resolverLineasDeCombo(combo, elecciones)
  const fijos = combo.componentes.filter((_, i) => !aElegir.includes(i))

  function cambiar(indiceDeComponente: number, idArticulo: number, texto: string) {
    setTextos((prev) => ({ ...prev, [`${indiceDeComponente}-${idArticulo}`]: texto }))
    const cantidad = parsearNumeroDecimal(texto) ?? 0
    setElecciones((prev) => ({ ...prev, [indiceDeComponente]: { ...prev[indiceDeComponente], [idArticulo]: cantidad } }))
  }

  function confirmar() {
    if (!resolucion.ok || confirmadoRef.current) return
    confirmadoRef.current = true
    onConfirmar(resolucion.lineas)
  }

  return (
    <Modal
      titulo={`Elegir artículos de ${combo.nombre}`}
      restaurarFoco={false}
      onCerrar={onCancelar}
      pie={
        <>
          <button type="button" className="btn btn-outline-secondary" onClick={onCancelar}>
            Cancelar
          </button>
          <button type="button" className="btn btn-primary" disabled={!resolucion.ok} onClick={confirmar}>
            Agregar combo
          </button>
        </>
      }
    >
      <form
        onSubmit={(e) => {
          e.preventDefault()
          confirmar()
        }}
      >
        {aElegir.map((indice) => {
          const componente = combo.componentes[indice]
          const elegido = totalElegido(elecciones, indice)
          return (
            <fieldset key={indice} className="mb-3">
              <legend className="fs-6">
                {componente.etiqueta}: elegí {formatearCantidadDeCombo(componente.cantidad)}
              </legend>
              {componente.candidatos.map((candidato, posicion) => (
                <div key={candidato.idArticulo} className="d-flex align-items-center justify-content-between gap-2 mb-1">
                  <label htmlFor={`miembro-${indice}-${candidato.idArticulo}`} className="mb-0">
                    {candidato.nombre}
                  </label>
                  <input
                    ref={indice === aElegir[0] && posicion === 0 ? primerCampoRef : undefined}
                    id={`miembro-${indice}-${candidato.idArticulo}`}
                    {...atributosDeCampoDeCantidad(restriccionDeCantidad(candidato.unidadVenta))}
                    className="form-control form-control-sm"
                    style={{ width: 90 }}
                    value={textos[`${indice}-${candidato.idArticulo}`] ?? formatearCantidadDeCombo(elecciones[indice]?.[candidato.idArticulo] ?? 0)}
                    onChange={(e) => cambiar(indice, candidato.idArticulo, e.target.value)}
                  />
                </div>
              ))}
              <div className={`small ${elegido === componente.cantidad ? 'text-success' : 'text-muted'}`} role="status">
                Elegidas {formatearCantidadDeCombo(elegido)} de {formatearCantidadDeCombo(componente.cantidad)}
              </div>
            </fieldset>
          )
        })}

        {fijos.length > 0 && (
          <p className="small text-muted mb-0">
            También se agrega: {fijos.map((c) => `${c.candidatos[0]?.nombre ?? c.etiqueta} × ${formatearCantidadDeCombo(c.cantidad)}`).join(', ')}.
          </p>
        )}
        <button type="submit" hidden aria-hidden="true" tabIndex={-1} />
      </form>
    </Modal>
  )
}

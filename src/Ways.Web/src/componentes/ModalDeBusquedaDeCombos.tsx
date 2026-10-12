import { useEffect, useId, useRef, useState } from 'react'
import { ErrorApi } from '../api/cliente'
import { formatearImporte } from '../formato/importes'
import { normalizarParaBuscar } from '../formato/texto'
import { motivoDeComboNoCargable, rotuloDeCodigoDeCombo, type ComboParaCargar } from '../pos/cargaDeCombos'
import type { ComboDeLista, FuenteDeCombos } from '../pos/fuenteDeCombos'
import { Modal } from './Modal'

export type PropsModalDeBusquedaDeCombos = {
  fuente: FuenteDeCombos
  /** Mismo contrato que `ModalDeBusquedaDeArticulos`: definido ⇒ el combo se puede mirar pero no cargar, y el texto dice por qué. */
  motivoSinAgregar?: string
  /** El combo elegido, con sus candidatos y ya comprobado como cargable. Quien llama lo carga y cierra el modal. */
  onCargar: (combo: ComboParaCargar) => void
  onCerrar: () => void
}

function coincide(combo: ComboDeLista, termino: string): boolean {
  if (termino === '') return true
  return normalizarParaBuscar(combo.nombre).includes(termino) || normalizarParaBuscar(rotuloDeCodigoDeCombo(combo.codigo)).includes(termino)
}

/**
 * Modal de búsqueda de combos del POS, modelado sobre `ModalDeBusquedaDeArticulos`: F3 o "Buscar combo" lo abren. Lista los
 * combos activos (código `C12`, nombre, precio y un resumen de sus componentes), se filtra por nombre o código y se
 * recorre con las flechas; Enter o un clic elige. Un combo con un componente sin artículos vendibles se muestra
 * deshabilitado con el motivo. Con la API el motivo recién se conoce al elegirlo (el detalle se pide en ese momento) y
 * entonces la fila queda marcada.
 *
 * Elegir un combo NO agrega nada acá: devuelve el combo con sus candidatos (`onCargar`) y `Pos.tsx` agrega las líneas por el
 * mismo camino que un código escaneado. Como el modal de artículos, no restaura el foco: es del POS.
 */
export function ModalDeBusquedaDeCombos({ fuente, motivoSinAgregar, onCargar, onCerrar }: PropsModalDeBusquedaDeCombos) {
  const puedeAgregar = motivoSinAgregar === undefined
  const idDeLista = useId()
  const [combos, setCombos] = useState<ComboDeLista[] | null>(null)
  const [cargandoLista, setCargandoLista] = useState(true)
  const [errorDeLista, setErrorDeLista] = useState('')
  const [reintento, setReintento] = useState(0)
  const [termino, setTermino] = useState('')
  const [indiceActivo, setIndiceActivo] = useState(0)
  const [eligiendo, setEligiendo] = useState<number | null>(null)
  const [motivoDeEleccion, setMotivoDeEleccion] = useState('')

  // react-async-state: cada lectura y cada elección llevan su generación; cerrar el modal las invalida todas.
  const generacionDeListaRef = useRef(0)
  const generacionDeEleccionRef = useRef(0)
  // Reentrancia de primera línea: un segundo Enter o clic antes de que el cierre se comite no vuelve a elegir.
  const eligiendoRef = useRef(false)
  const entregadoRef = useRef(false)
  const inputRef = useRef<HTMLInputElement>(null)

  useEffect(() => {
    inputRef.current?.focus()
  }, [])

  useEffect(() => {
    const generacion = (generacionDeListaRef.current += 1)
    setCargandoLista(true)
    setErrorDeLista('')
    fuente
      .listar()
      .then((lista) => {
        if (generacionDeListaRef.current !== generacion) return
        setCombos(lista)
      })
      .catch((e: unknown) => {
        if (generacionDeListaRef.current !== generacion) return
        setCombos(null)
        setErrorDeLista(e instanceof ErrorApi ? e.message : 'No se pudieron leer los combos.')
      })
      .finally(() => {
        if (generacionDeListaRef.current === generacion) setCargandoLista(false)
      })
    return () => {
      generacionDeListaRef.current += 1
    }
  }, [fuente, reintento])

  useEffect(
    () => () => {
      generacionDeEleccionRef.current += 1
    },
    [],
  )

  const terminoNormalizado = normalizarParaBuscar(termino.trim())
  const visibles = (combos ?? []).filter((c) => coincide(c, terminoNormalizado))
  const activo = visibles.length === 0 ? -1 : Math.min(indiceActivo, visibles.length - 1)

  function cambiarTermino(valor: string) {
    setTermino(valor)
    setIndiceActivo(0)
    setMotivoDeEleccion('')
  }

  async function elegir(combo: ComboDeLista) {
    if (!puedeAgregar || combo.motivoNoCargable !== null || eligiendoRef.current || entregadoRef.current) return
    eligiendoRef.current = true
    const generacion = (generacionDeEleccionRef.current += 1)
    setEligiendo(combo.idCombo)
    setMotivoDeEleccion('')

    try {
      const completo = await fuente.cargar(combo.idCombo)
      if (generacionDeEleccionRef.current !== generacion) return
      const motivo = motivoDeComboNoCargable(completo)
      if (motivo !== null) {
        setCombos((prev) => prev && prev.map((c) => (c.idCombo === combo.idCombo ? { ...c, motivoNoCargable: motivo } : c)))
        return
      }
      entregadoRef.current = true
      onCargar(completo)
    } catch (e) {
      if (generacionDeEleccionRef.current !== generacion) return
      setMotivoDeEleccion(e instanceof ErrorApi ? e.message : 'No se pudo leer el combo.')
    } finally {
      if (generacionDeEleccionRef.current === generacion) {
        eligiendoRef.current = false
        setEligiendo(null)
      }
    }
  }

  function alTeclear(evento: React.KeyboardEvent<HTMLInputElement>) {
    if (evento.key === 'ArrowDown' || evento.key === 'ArrowUp') {
      evento.preventDefault()
      if (visibles.length === 0) return
      const paso = evento.key === 'ArrowDown' ? 1 : -1
      setIndiceActivo((actual) => (Math.min(actual, visibles.length - 1) + paso + visibles.length) % visibles.length)
      return
    }
    if (evento.key === 'Enter') {
      evento.preventDefault()
      if (activo >= 0) void elegir(visibles[activo])
    }
  }

  return (
    <Modal titulo="Buscar combo" tamano="lg" restaurarFoco={false} onCerrar={onCerrar}>
      <div className="mb-3">
        <input
          ref={inputRef}
          type="search"
          className="form-control"
          placeholder="Buscar por nombre o código (ej. C12)…"
          aria-label="Buscar combo por nombre o código"
          role="combobox"
          aria-expanded="true"
          aria-controls={idDeLista}
          aria-activedescendant={activo >= 0 ? `${idDeLista}-${visibles[activo].idCombo}` : undefined}
          value={termino}
          onChange={(e) => cambiarTermino(e.target.value)}
          onKeyDown={alTeclear}
        />
      </div>

      {motivoSinAgregar && <div className="alert alert-warning py-1 px-2 small">{motivoSinAgregar}</div>}
      {errorDeLista && (
        <div className="alert alert-danger py-1 px-2 small d-flex justify-content-between align-items-center gap-2">
          <span>{errorDeLista}</span>
          <button type="button" className="btn btn-sm btn-outline-danger" onClick={() => setReintento((r) => r + 1)}>
            Reintentar
          </button>
        </div>
      )}
      {motivoDeEleccion && <div className="alert alert-danger py-1 px-2 small">{motivoDeEleccion}</div>}

      {cargandoLista && <div className="text-center text-muted py-3">Cargando combos…</div>}
      {!cargandoLista && combos !== null && visibles.length === 0 && (
        <div className="text-center text-muted py-3">{combos.length === 0 ? 'No hay combos activos' : 'Sin resultados'}</div>
      )}

      <div id={idDeLista} role="listbox" aria-label="Combos" className="list-group">
        {!cargandoLista &&
          visibles.map((combo, i) => {
            const noCargable = combo.motivoNoCargable !== null
            const deshabilitado = noCargable || !puedeAgregar
            return (
              <button
                key={combo.idCombo}
                id={`${idDeLista}-${combo.idCombo}`}
                type="button"
                role="option"
                tabIndex={-1}
                aria-selected={i === activo}
                aria-disabled={deshabilitado}
                title={noCargable ? undefined : motivoSinAgregar}
                className={`list-group-item list-group-item-action text-start${i === activo ? ' active' : ''}${deshabilitado ? ' text-muted' : ''}`}
                onClick={() => void elegir(combo)}
                disabled={eligiendo !== null && eligiendo !== combo.idCombo}
              >
                <div className="d-flex justify-content-between gap-2">
                  <span>
                    <strong>{rotuloDeCodigoDeCombo(combo.codigo)}</strong> {combo.nombre}
                  </span>
                  <span>{formatearImporte(combo.precio, { simbolo: true })}</span>
                </div>
                <div className="small">{combo.resumen}</div>
                {noCargable && <div className="small fw-bold">No se puede cargar: {combo.motivoNoCargable}</div>}
                {eligiendo === combo.idCombo && <div className="small">Cargando…</div>}
              </button>
            )
          })}
      </div>
    </Modal>
  )
}

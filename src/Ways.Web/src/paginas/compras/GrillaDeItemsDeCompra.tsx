import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import type { KeyboardEvent } from 'react'
import { calcularTotalesDeCompra, lineaFormularioACalculo } from '../../api/compras'
import { cambiosAlElegirArticulo, lineaTieneDatos, type LineaDeCompraFormulario } from '../../api/lineaDeCompra'
import type { AlicuotaIvaListado, ArticuloListado } from '../../api/tipos'
import { AltaDeArticuloDesdeCompra } from './AltaDeArticuloDesdeCompra'
import { FilaDeGrilla } from './FilaDeGrilla'
import { precargaDeArticuloDesdeLinea } from './precargaDeArticuloDesdeLinea'
import { useConsultaDeCodigos } from './useConsultaDeCodigos'

type Props = {
  lineas: LineaDeCompraFormulario[]
  alicuotas: AlicuotaIvaListado[]
  disabled: boolean
  discriminaIva: boolean
  /** Los precios tipeados ya traen el IVA (solo con un comprobante que discrimina). */
  preciosFinales: boolean
  porcentajePorAlicuota: Record<number, number>
  idProveedor: number | null
  /** El rol puede crear artículos (hoy, el mismo que escribe compras): sin él no se ofrece el "+". */
  puedeCrearArticulos: boolean
  /** Pinta las celdas que le faltan a cada fila con datos (después de un intento de guardar). */
  marcarFaltantes: boolean
  /** Sube cada vez que el operador cambia el proveedor del encabezado. */
  cambiosDeProveedor: number
  /** Avisa si hay códigos de proveedor consultándose (guardar tiene que esperarlos). */
  onConsultando?: (hayEnVuelo: boolean) => void
  /** Avisa si hay un alta de artículo abierta sobre la grilla (guardar la compra tiene que esperar a que se cierre). */
  onCreandoArticulo?: (abierta: boolean) => void
  onCambio: (clave: number, cambios: Partial<LineaDeCompraFormulario>) => void
  onQuitar: (clave: number) => void
}

type PedidoDeFoco = { clave: number; celda?: 'codigo' }

/** Solo cambiar el Código, el Detalle o el artículo de una fila deja sin efecto su consulta en vuelo. */
function afectaALaConsulta(cambios: Partial<LineaDeCompraFormulario>): boolean {
  return 'codigoProveedor' in cambios || 'idArticulo' in cambios || 'descripcion' in cambios
}

function sonIguales(fila: LineaDeCompraFormulario, cambios: Partial<LineaDeCompraFormulario>): boolean {
  return (Object.keys(cambios) as (keyof LineaDeCompraFormulario)[]).every((campo) => Object.is(fila[campo], cambios[campo]))
}

/**
 * Grilla de items de un borrador de compra: Código, Detalle, UM, Cantidad, Importe, Descuento, IVA
 * y Total. Siempre cierra con una fila vacía (la mantiene el padre con `normalizarFilas`). Tab y
 * Shift+Tab recorren las celdas en orden de lectura y pasan de fila; Enter en una celda (sin lista
 * de artículos abierta) va al Código de la fila siguiente; el botón de quitar queda fuera del
 * orden de tabulación (Alt+Supr quita la fila donde está el foco). Resuelve el código del proveedor al salir de su celda y el alta
 * de artículo desde la fila, con el formulario completo del ABM en un modal.
 */
export function GrillaDeItemsDeCompra({
  lineas,
  alicuotas,
  disabled,
  discriminaIva,
  preciosFinales,
  porcentajePorAlicuota,
  idProveedor,
  puedeCrearArticulos,
  marcarFaltantes,
  cambiosDeProveedor,
  onConsultando,
  onCreandoArticulo,
  onCambio,
  onQuitar,
}: Props) {
  const tablaRef = useRef<HTMLTableElement>(null)
  const lineasRef = useRef(lineas)
  const [foco, setFoco] = useState<PedidoDeFoco | null>(null)
  const [creando, setCreando] = useState<number | null>(null)

  useEffect(() => {
    lineasRef.current = lineas
  }, [lineas])

  /** Pasa el foco a la siguiente celda editable de la fila, solo si el operador sigue en su Detalle. */
  const alResolver = useCallback((clave: number) => {
    const detalle = tablaRef.current?.querySelector(`tr[data-fila="${clave}"] [data-celda="detalle"]`)
    if (detalle && document.activeElement === detalle) setFoco({ clave })
  }, [])

  const modoDePrecios = useMemo(() => ({ discriminaIva, preciosIncluyenIva: preciosFinales }), [discriminaIva, preciosFinales])

  const { consultas, alSalirDelCodigo, invalidar, olvidar } = useConsultaDeCodigos({
    lineas,
    idProveedor,
    alicuotas,
    modoDePrecios,
    disabled,
    cambiosDeProveedor,
    aplicar: onCambio,
    alResolver,
    alCambiarConsultasEnVuelo: onConsultando,
  })

  useEffect(() => {
    if (foco === null) return
    const fila = tablaRef.current?.querySelector(`tr[data-fila="${foco.clave}"]`)
    const selector =
      foco.celda === 'codigo' ? '[data-celda="codigo"]' : '[data-celda="um"]:not(:disabled), [data-celda="cantidad"]:not(:disabled)'
    fila?.querySelector<HTMLElement>(selector)?.focus()
    setFoco(null)
  }, [foco])

  function cambiar(clave: number, cambios: Partial<LineaDeCompraFormulario>) {
    const fila = lineasRef.current.find((l) => l.clave === clave)
    if (fila && sonIguales(fila, cambios)) return
    if (afectaALaConsulta(cambios)) invalidar(clave)
    onCambio(clave, cambios)
  }

  function elegir(clave: number, articulo: ArticuloListado) {
    const fila = lineasRef.current.find((l) => l.clave === clave)
    if (!fila) return
    cambiar(clave, cambiosAlElegirArticulo(fila, articulo, alicuotas, 'busqueda', modoDePrecios))
  }

  function quitar(clave: number) {
    olvidar(clave)
    onQuitar(clave)
  }

  function alTeclear(evento: KeyboardEvent<HTMLTableElement>) {
    if (evento.key === 'Delete' && evento.altKey && !evento.ctrlKey && !evento.metaKey && !evento.shiftKey) {
      if (!evento.repeat) quitarFilaDelFoco(evento)
      return
    }
    if (evento.key !== 'Enter' || evento.defaultPrevented || evento.ctrlKey || evento.metaKey || evento.altKey || evento.shiftKey) return
    const origen = evento.target as HTMLElement
    if (!origen.matches('input, select')) return

    let fila = origen.closest('tr')?.nextElementSibling ?? null
    while (fila && !fila.matches('tr[data-fila]')) fila = fila.nextElementSibling
    const codigo = fila?.querySelector<HTMLElement>('[data-celda="codigo"]')
    if (!codigo) return
    evento.preventDefault()
    codigo.focus()
  }

  function quitarFilaDelFoco(evento: KeyboardEvent<HTMLTableElement>) {
    const fila = (evento.target as HTMLElement).closest<HTMLElement>('tr[data-fila], tr[data-lote-de]')
    if (!fila || disabled) return
    const clave = Number(fila.dataset.fila ?? fila.dataset.loteDe)
    const linea = lineasRef.current.find((l) => l.clave === clave)
    if (!linea || !lineaTieneDatos(linea)) return
    evento.preventDefault()

    const filaDeDatos = tablaRef.current?.querySelector<HTMLElement>(`tr[data-fila="${clave}"]`)
    let siguiente = filaDeDatos?.nextElementSibling ?? null
    while (siguiente && !siguiente.matches('tr[data-fila]')) siguiente = siguiente.nextElementSibling
    const destino = Number((siguiente as HTMLElement | null)?.dataset.fila)
    quitar(clave)
    if (Number.isFinite(destino)) setFoco({ clave: destino, celda: 'codigo' })
  }

  const filaEnAlta = creando === null ? null : lineas.find((l) => l.clave === creando)
  const hayAlta = filaEnAlta !== null && filaEnAlta !== undefined && puedeCrearArticulos
  useEffect(() => {
    onCreandoArticulo?.(hayAlta)
  }, [hayAlta, onCreandoArticulo])
  useEffect(() => () => onCreandoArticulo?.(false), [onCreandoArticulo])

  return (
    <>
      <div className="table-responsive">
        <table
          ref={tablaRef}
          className="table table-sm table-bordered align-middle mb-2"
          aria-label="Items de la compra"
          onKeyDown={alTeclear}
        >
          <thead>
            <tr>
              <th scope="col">Código</th>
              <th scope="col">Detalle</th>
              <th scope="col">UM</th>
              <th scope="col">Cantidad</th>
              <th scope="col">Importe</th>
              <th scope="col" title="Porcentaje sobre el bruto de la línea">
                Descuento
              </th>
              <th scope="col">IVA</th>
              <th scope="col" className="text-end">
                Total
              </th>
              <th scope="col">
                <span className="visually-hidden">Quitar</span>
              </th>
            </tr>
          </thead>
          <tbody>
            {lineas.map((linea, indice) => (
              <FilaDeGrilla
                key={linea.clave}
                numero={indice + 1}
                linea={linea}
                alicuotas={alicuotas}
                disabled={disabled}
                idProveedor={idProveedor}
                marcarFaltantes={marcarFaltantes}
                total={calcularTotalesDeCompra([lineaFormularioACalculo(linea, porcentajePorAlicuota)], discriminaIva).items[0].total}
                consulta={consultas[linea.clave]}
                onCambio={cambiar}
                onCodigoSalida={alSalirDelCodigo}
                onElegir={elegir}
                onQuitar={quitar}
                puedeCrearArticulos={puedeCrearArticulos}
                onCrear={setCreando}
              />
            ))}
          </tbody>
        </table>
      </div>

      {hayAlta && (
        <AltaDeArticuloDesdeCompra
          key={filaEnAlta.clave}
          precarga={precargaDeArticuloDesdeLinea({
            descripcion: filaEnAlta.descripcion,
            codigoProveedor: filaEnAlta.codigoProveedor,
            idAlicuotaIva: filaEnAlta.idAlicuotaIva,
            importe: filaEnAlta.importe,
            idProveedor,
            discriminaIva: modoDePrecios.discriminaIva,
            preciosFinales: modoDePrecios.preciosIncluyenIva,
          })}
          idProveedor={idProveedor}
          onCreado={(articulo, codigoProveedor) => {
            const fila = lineasRef.current.find((l) => l.clave === filaEnAlta.clave)
            if (!fila) return
            cambiar(fila.clave, {
              ...cambiosAlElegirArticulo(fila, articulo, alicuotas, 'alta', modoDePrecios),
              // Sin código asociado a este proveedor la fila conserva el que tenía: el operador lo tipeó para esta compra.
              codigoProveedor: codigoProveedor ?? fila.codigoProveedor,
              codigoConsultado: codigoProveedor ?? '',
            })
            setCreando(null)
            setFoco({ clave: fila.clave })
          }}
          onCancelar={() => setCreando(null)}
        />
      )}
    </>
  )
}

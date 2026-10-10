import { useCallback, useEffect, useRef, useState } from 'react'
import type { KeyboardEvent } from 'react'
import { calcularTotalesDeCompra, lineaFormularioACalculo } from '../../api/compras'
import { cambiosAlElegirArticulo, type LineaDeCompraFormulario } from '../../api/lineaDeCompra'
import type { AlicuotaIvaListado, ArticuloListado } from '../../api/tipos'
import { AltaRapidaArticuloDeCompra } from './AltaRapidaArticuloDeCompra'
import { FilaDeGrilla } from './FilaDeGrilla'
import { useConsultaDeCodigos } from './useConsultaDeCodigos'

type Props = {
  lineas: LineaDeCompraFormulario[]
  alicuotas: AlicuotaIvaListado[]
  disabled: boolean
  discriminaIva: boolean
  porcentajePorAlicuota: Record<number, number>
  idProveedor: number | null
  nombreProveedor: string
  /** Pinta las celdas que le faltan a cada fila con datos (después de un intento de guardar). */
  marcarFaltantes: boolean
  onCambio: (clave: number, cambios: Partial<LineaDeCompraFormulario>) => void
  onQuitar: (clave: number) => void
}

type PedidoDeFoco = { clave: number }

function sonIguales(fila: LineaDeCompraFormulario, cambios: Partial<LineaDeCompraFormulario>): boolean {
  return (Object.keys(cambios) as (keyof LineaDeCompraFormulario)[]).every((campo) => Object.is(fila[campo], cambios[campo]))
}

/**
 * Grilla de items de un borrador de compra: Código, Detalle, UM, Cantidad, Importe, Descuento, IVA
 * y Total. Siempre cierra con una fila vacía (la mantiene el padre con `normalizarFilas`). Tab y
 * Shift+Tab recorren las celdas en orden de lectura y pasan de fila; Enter en una celda (sin lista
 * de artículos abierta) va al Código de la fila siguiente; el botón de quitar queda fuera del
 * orden de tabulación. Resuelve el código del proveedor al salir de su celda y el alta rápida de
 * artículo desde la fila.
 */
export function GrillaDeItemsDeCompra({
  lineas,
  alicuotas,
  disabled,
  discriminaIva,
  porcentajePorAlicuota,
  idProveedor,
  nombreProveedor,
  marcarFaltantes,
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

  const { consultas, alSalirDelCodigo, invalidar, olvidar } = useConsultaDeCodigos({
    lineas,
    idProveedor,
    alicuotas,
    aplicar: onCambio,
    alResolver,
  })

  useEffect(() => {
    if (foco === null) return
    const fila = tablaRef.current?.querySelector(`tr[data-fila="${foco.clave}"]`)
    fila?.querySelector<HTMLElement>('[data-celda="um"]:not(:disabled), [data-celda="cantidad"]:not(:disabled)')?.focus()
    setFoco(null)
  }, [foco])

  function cambiar(clave: number, cambios: Partial<LineaDeCompraFormulario>) {
    const fila = lineasRef.current.find((l) => l.clave === clave)
    if (fila && sonIguales(fila, cambios)) return
    invalidar(clave)
    onCambio(clave, cambios)
  }

  function elegir(clave: number, articulo: ArticuloListado) {
    const fila = lineasRef.current.find((l) => l.clave === clave)
    if (!fila) return
    cambiar(clave, cambiosAlElegirArticulo(fila, articulo, alicuotas, 'busqueda'))
  }

  function quitar(clave: number) {
    olvidar(clave)
    onQuitar(clave)
  }

  function alTeclear(evento: KeyboardEvent<HTMLTableElement>) {
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

  const filaEnAlta = creando === null ? null : lineas.find((l) => l.clave === creando)

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
            {lineas.map((linea) => (
              <FilaDeGrilla
                key={linea.clave}
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
                onCrear={setCreando}
              />
            ))}
          </tbody>
        </table>
      </div>

      {filaEnAlta && (
        <AltaRapidaArticuloDeCompra
          nombreInicial={filaEnAlta.descripcion.trim()}
          codigoInicial={filaEnAlta.codigoProveedor.trim()}
          idProveedor={idProveedor}
          nombreProveedor={nombreProveedor}
          alicuotas={alicuotas}
          idAlicuotaIvaDeLaLinea={filaEnAlta.idAlicuotaIva}
          costoDeLaLinea={filaEnAlta.importe}
          onCreado={(articulo, codigoProveedor) => {
            const clave = filaEnAlta.clave
            cambiar(clave, {
              ...cambiosAlElegirArticulo(filaEnAlta, articulo, alicuotas, 'alta'),
              codigoProveedor: codigoProveedor ?? '',
              codigoConsultado: codigoProveedor ?? '',
            })
            setCreando(null)
            setFoco({ clave })
          }}
          onCancelar={() => setCreando(null)}
        />
      )}
    </>
  )
}

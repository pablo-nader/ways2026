import { useEffect, useRef, useState } from 'react'
import type { ArticuloListado } from '../../api/tipos'
import { ConfirmacionDeBaja } from '../../componentes/ConfirmacionDeBaja'
import { ModalDeArticulo } from '../articulos/ModalDeArticulo'
import { useFormularioDeArticulo, type PrecargaDeAlta } from '../articulos/useFormularioDeArticulo'

// Al cerrar, `Modal` devuelve el foco a quien lo abrió; no hay un destino de reserva que sumar.
const sinReserva = { current: null }

type Props = {
  /** Lo que la fila adelanta al formulario; solo se lee al abrir. */
  precarga: PrecargaDeAlta
  /** El proveedor de la compra: el código que se asocia al crear solo vale para él. */
  idProveedor: number | null
  /** `codigoProveedor` es el que quedó asociado al proveedor de la compra, o `null` si no se asoció ninguno. */
  onCreado: (articulo: ArticuloListado, codigoProveedor: string | null) => void
  onCancelar: () => void
}

/**
 * El alta de artículo de siempre —el mismo formulario, los mismos catálogos y las mismas validaciones que el ABM— en
 * un modal sobre la compra, sin tocar la URL. Mientras está abierto la pantalla de atrás queda tapada y fuera del
 * recorrido de Tab (la trampa de foco de `Modal`) y, al cerrarlo, el foco vuelve a quien lo abrió.
 *
 * El alta no navega: tras crear, avisa con `onCreado`. Una respuesta que llega con el modal ya cerrado no avisa: el
 * artículo existe, pero la fila que lo pidió no cambia.
 */
export function AltaDeArticuloDesdeCompra({ precarga, idProveedor, onCreado, onCancelar }: Props) {
  const [precargaInicial] = useState(precarga)
  // Cerrar por cualquier camino (cancelar, quitar la fila, salir de la pantalla) desmonta este componente: lo que
  // llegue después no es de nadie.
  const vigenteRef = useRef(false)
  useEffect(() => {
    vigenteRef.current = true
    return () => {
      vigenteRef.current = false
    }
  }, [])

  const alta = useFormularioDeArticulo({
    precargaInicial,
    compra: { idProveedor },
    alCrear: (creado, codigoEnviado) => {
      if (!vigenteRef.current) return
      // El servidor asocia el código al proveedor habitual del artículo, que el operador pudo cambiar en el formulario.
      onCreado(creado, creado.idProveedorHabitual === idProveedor ? codigoEnviado : null)
    },
  })

  const [confirmandoDescarte, setConfirmandoDescarte] = useState(false)

  function cerrar() {
    if (alta.ocupado) return
    if (alta.haySinGuardar()) {
      setConfirmandoDescarte(true)
      return
    }
    onCancelar()
  }

  return (
    <>
      <ModalDeArticulo {...alta.propsDelModal({ ocupado: alta.ocupado, focoDeReserva: sinReserva, onCerrar: cerrar })} />
      {confirmandoDescarte && (
        <ConfirmacionDeBaja
          titulo="los cambios sin guardar del artículo"
          pregunta="Descartar"
          nota="Cancelar vuelve a la edición."
          etiquetaConfirmar="Descartar cambios"
          etiquetaEnCurso="Descartando…"
          ocupado={false}
          onConfirmar={onCancelar}
          onCancelar={() => setConfirmandoDescarte(false)}
        />
      )}
    </>
  )
}

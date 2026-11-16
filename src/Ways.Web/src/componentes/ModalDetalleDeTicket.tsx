import { useEffect, useState } from 'react'
import { clienteDeCatalogo } from '../api/catalogos'
import { ErrorApi } from '../api/cliente'
import type { ComprobanteEmitido, MedioPagoAlta, MedioPagoListado } from '../api/tipos'
import { clienteDeVentas } from '../api/ventas'
import { ModalDetalleDeVenta } from './DetalleDeVenta'

const clienteMediosPago = clienteDeCatalogo<MedioPagoListado, MedioPagoAlta>('medios-pago')

const SIN_MEDIOS: MedioPagoListado[] = []

/** Los nombres de los medios solo nombran pagos: sin catálogo el detalle sale igual con el rótulo de respaldo. */
function cargarMedios(): Promise<MedioPagoListado[]> {
  return clienteMediosPago.listar(false).catch(() => SIN_MEDIOS)
}

type Props = {
  idTicket: number
  nombreCliente: string | undefined
  /** Medios que el dueño ya tiene cargados; sin ellos se piden al catálogo. */
  medios?: MedioPagoListado[]
  disparador: HTMLElement | null
  onCerrar: () => void
}

/** Detalle de solo lectura de un ticket. Se monta con `key` por ticket: cambiar de ticket descarta la respuesta del
 * anterior y nunca se ve el error o el comprobante de otro. */
export function ModalDetalleDeTicket({ idTicket, nombreCliente, medios: mediosDelDueno, disparador, onCerrar }: Props) {
  const [comprobante, setComprobante] = useState<ComprobanteEmitido | null>(null)
  const [mediosCargados, setMediosCargados] = useState<MedioPagoListado[]>(SIN_MEDIOS)
  const [cargando, setCargando] = useState(true)
  const [error, setError] = useState('')
  const tieneMedios = mediosDelDueno !== undefined

  useEffect(() => {
    let vigente = true
    Promise.all([clienteDeVentas.obtener(idTicket), tieneMedios ? Promise.resolve(null) : cargarMedios()])
      .then(([venta, listaDeMedios]) => {
        if (!vigente) return
        if (listaDeMedios) setMediosCargados(listaDeMedios)
        setComprobante(venta)
      })
      .catch((e) => {
        if (!vigente) return
        setError(e instanceof ErrorApi ? e.message : 'No se pudo cargar el detalle de la venta.')
      })
      .finally(() => {
        if (vigente) setCargando(false)
      })
    return () => {
      vigente = false
    }
  }, [idTicket, tieneMedios])

  return (
    <ModalDetalleDeVenta
      comprobante={comprobante}
      nombreCliente={nombreCliente}
      cargando={cargando}
      error={error}
      medios={mediosDelDueno ?? mediosCargados}
      puedeReimprimirAca={false}
      reimprimiendo={false}
      errorReimprimir=""
      disparador={disparador}
      onReimprimir={() => {}}
      onCerrar={onCerrar}
    />
  )
}

import { useEffect, useRef, useState } from 'react'
import { clienteDeCatalogo } from '../api/catalogos'
import { ErrorApi } from '../api/cliente'
import { clienteDePersonal, FILTROS_DE_PERSONAL_VACIOS, TAMANIO_MAXIMO_DE_PAGINA } from '../api/personal'
import { clienteDeProveedores } from '../api/proveedores'
import {
  advertenciasDePagoDeAdministracion,
  clienteDePagosDeAdministracion,
  copiaDeFalloDeEdicion,
  esPagoOriginadoEnPersonal,
  MOTIVO_PAGO_ORIGINADO_EN_PERSONAL,
} from '../api/registroDePagos'
import { elegiblesComoPersonal } from '../api/sueldoConPersonal'
import type {
  AreaAlta,
  AreaListado,
  MedioPagoAlta,
  MedioPagoListado,
  OpcionDeProveedor,
  PagoDeAdministracionListado,
  PersonalListado,
  SolicitudDeEdicionDePagoConLineas,
} from '../api/tipos'
import { ModalDeEdicionDePago } from '../paginas/ModalDeEdicionDePago'
import { origenEditableDelPago } from '../paginas/utilidadesPagosDelTurno'
import { Cargando } from './Cargando'
import { Modal } from './Modal'

const clienteMediosPago = clienteDeCatalogo<MedioPagoListado, MedioPagoAlta>('medios-pago')
const clienteAreas = clienteDeCatalogo<AreaListado, AreaAlta>('areas')

type Datos = {
  pago: PagoDeAdministracionListado
  medios: MedioPagoListado[]
  proveedores: OpcionDeProveedor[]
  areas: AreaListado[]
  personal: PersonalListado[]
  errorDePersonal: string
}

type Props = {
  idPago: number
  /** Se llama una vez, con el pago ya corregido en el servidor; el dueño avisa y vuelve a pedir lo que muestra. */
  onGuardado: () => void
  onCerrar: () => void
}

/** El personal solo acompaña la edición: si no se puede leer, el pago sigue editable sin empleado. */
function cargarPersonal(): Promise<{ personal: PersonalListado[]; errorDePersonal: string }> {
  return clienteDePersonal
    .listarGrilla({ filtros: FILTROS_DE_PERSONAL_VACIOS, orden: null, pagina: 1, tamanio: TAMANIO_MAXIMO_DE_PAGINA })
    .then((respuesta) => ({ personal: elegiblesComoPersonal(respuesta.items), errorDePersonal: '' }))
    .catch(() => ({ personal: [], errorDePersonal: 'No se pudo cargar el personal.' }))
}

/**
 * Edición administrativa de un pago pedido por id (`GET /api/pagos/administracion/{id}`), para las pantallas que no
 * tienen el pago en la forma del listado de administración (el detalle de un turno). Se monta con `key={idPago}`: lee el
 * pago y sus catálogos una sola vez, y recién entonces muestra `ModalDeEdicionDePago`; mientras tanto, o si la lectura
 * falla, muestra un diálogo propio con el motivo.
 *
 * Lectura: una generación descarta lo que llega después de cerrar. Escritura: ref sincrónico contra el doble clic y todo
 * el diálogo inerte hasta que el servidor responde; la respuesta no toca estado si el diálogo ya no está montado.
 */
export function EdicionDePagoDelTurno({ idPago, onGuardado, onCerrar }: Props) {
  const [datos, setDatos] = useState<Datos | null>(null)
  const [errorDeCarga, setErrorDeCarga] = useState('')
  const [guardando, setGuardando] = useState(false)
  const [errorDeEdicion, setErrorDeEdicion] = useState('')
  const generacionRef = useRef(0)
  const guardandoRef = useRef(false)
  const montadoRef = useRef(true)

  useEffect(() => {
    montadoRef.current = true
    const miGeneracion = ++generacionRef.current
    Promise.all([
      clienteDePagosDeAdministracion.obtener(idPago),
      clienteMediosPago.listar(false),
      clienteAreas.listar(false),
      clienteDeProveedores.opciones(),
      cargarPersonal(),
    ])
      .then(([pago, medios, areas, proveedores, { personal, errorDePersonal }]) => {
        if (generacionRef.current !== miGeneracion) return
        if (esPagoOriginadoEnPersonal(pago)) {
          setErrorDeCarga(MOTIVO_PAGO_ORIGINADO_EN_PERSONAL)
          return
        }
        // La cuenta corriente es el crédito del cliente en una venta, nunca una salida de dinero.
        setDatos({
          pago,
          medios: medios.filter((m) => m.comportamiento !== 'CuentaCorriente'),
          proveedores,
          areas,
          personal,
          errorDePersonal,
        })
      })
      .catch((e) => {
        if (generacionRef.current !== miGeneracion) return
        setErrorDeCarga(e instanceof ErrorApi ? e.message : 'No se pudo cargar el pago.')
      })

    return () => {
      montadoRef.current = false
      generacionRef.current += 1
    }
  }, [idPago])

  async function guardar(solicitud: SolicitudDeEdicionDePagoConLineas) {
    if (guardandoRef.current) return
    guardandoRef.current = true
    setGuardando(true)
    setErrorDeEdicion('')
    let guardado = false
    try {
      await clienteDePagosDeAdministracion.actualizar(idPago, solicitud)
      guardado = true
    } catch (e) {
      if (montadoRef.current) setErrorDeEdicion(copiaDeFalloDeEdicion(e))
    } finally {
      guardandoRef.current = false
      if (montadoRef.current) setGuardando(false)
    }
    if (guardado) onGuardado()
  }

  if (datos === null) {
    return (
      <Modal titulo="Editar pago" tamano="sm" onCerrar={onCerrar}>
        {errorDeCarga ? (
          <div className="alert alert-danger mb-0" role="alert">
            {errorDeCarga}
          </div>
        ) : (
          <Cargando texto="Cargando el pago…" />
        )}
      </Modal>
    )
  }

  return (
    <ModalDeEdicionDePago
      pago={datos.pago}
      medios={datos.medios}
      proveedores={datos.proveedores}
      areas={datos.areas}
      ligadoACompra={datos.pago.idComprobanteCompra !== null}
      personal={datos.personal}
      errorDePersonal={datos.errorDePersonal}
      advertencias={advertenciasDePagoDeAdministracion(datos.pago)}
      origenEditable={origenEditableDelPago(datos.pago)}
      guardando={guardando}
      error={errorDeEdicion}
      onGuardarConLineas={(solicitud) => void guardar(solicitud)}
      onCancelar={onCerrar}
    />
  )
}

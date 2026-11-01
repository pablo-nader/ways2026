import { useCallback, useEffect, useRef, useState } from 'react'
import { copiaDeFalloDeBaja } from '../api/bajas'
import { ErrorApi } from '../api/cliente'
import { clienteDeOrganizacion, ETIQUETA_SIN_DUENIO } from '../api/organizacion'
import { puedeGestionarCatalogos, ROL } from '../api/tipos'
import type { EmpresaListado, SucursalListado } from '../api/tipos'
import { useAuth } from '../auth/useAuth'
import { BotonIcono } from '../componentes/BotonIcono'
import { Box } from '../componentes/Box'
import { Cargando } from '../componentes/Cargando'
import { ConfirmacionDeBaja } from '../componentes/ConfirmacionDeBaja'
import {
  EncabezadoOrdenable,
  FilaDeFiltros,
  useFiltrosDeColumna,
  useOrdenamiento,
  type ColumnasFiltrables,
  type ColumnasOrdenables,
} from '../componentes/grilla'
import { ModalDeFusionDeSucursal, type FormularioDeFusion } from './organizacion/ModalDeFusionDeSucursal'
import { aDireccionDeLaApi, aDireccionDelFormulario, DIRECCION_VACIA } from './organizacion/direccionDeSucursal'
import { ModalDeMercadoPagoDeSucursal } from './organizacion/ModalDeMercadoPagoDeSucursal'
import { ModalDeSucursal, type FormularioDeSucursal } from './organizacion/ModalDeSucursal'

const AVISO_REFRESCO_FALLIDO = 'Se guardó, pero no se pudo actualizar la vista. Recargá la pantalla.'
const AVISO_REFRESCO_FALLIDO_BAJA = 'Se eliminó, pero no se pudo actualizar la vista. Recargá la pantalla.'

const ALTA_VACIA: FormularioDeSucursal = { id: null, idEmpresa: '', nombre: '', ...DIRECCION_VACIA }

const textoDeId = (s: SucursalListado) => String(s.id).padStart(4, '0')
const textoDeEmpresa = (s: SucursalListado) => s.razonSocialEmpresa ?? ETIQUETA_SIN_DUENIO
const sinMarcaDeDuenio = (texto: string) => (texto === ETIQUETA_SIN_DUENIO ? '' : texto)

const ORDENABLES: ColumnasOrdenables<SucursalListado> = {
  id: { valor: (s) => s.id },
  empresa: { valor: (s) => sinMarcaDeDuenio(textoDeEmpresa(s)) },
  nombre: { valor: (s) => s.nombre },
  cajas: { valor: (s) => s.cantidadPuntosVenta },
}
const FILTRABLES: ColumnasFiltrables<SucursalListado> = {
  id: { etiqueta: 'ID', valor: textoDeId },
  empresa: { etiqueta: 'Empresa', valor: (s) => sinMarcaDeDuenio(textoDeEmpresa(s)) },
  nombre: { etiqueta: 'Nombre', valor: (s) => s.nombre },
  cajas: { etiqueta: 'Puntos de venta', valor: (s) => String(s.cantidadPuntosVenta) },
}
/** Una entrada por columna del encabezado, en su orden; `null` es Acciones. */
const CELDAS = ['id', 'empresa', 'nombre', 'cajas', null]

/**
 * ABM de sucursales (tenant → empresa → sucursal → punto de venta): el stock es de la sucursal y las cajas la
 * comparten. Alta, edición (nombre y dirección), baja lógica y fusión en otra sucursal de la misma empresa (sus cajas y su stock pasan al
 * destino); mover una caja de sucursal se hace desde Puntos de venta. La baja la
 * rechaza el servidor mientras la sucursal tenga cajas vivas o uso (stock, lotes, movimientos). Mismo patrón
 * de escrituras que `PuntosVenta.tsx`: token de generación para las lecturas y una guarda síncrona de
 * re-entrancia para las escrituras.
 */
export function Sucursales() {
  const { usuario } = useAuth()
  const esPlataforma = usuario?.rolId === ROL.Root
  /** Mercado Pago es solo Admin (`GestionDeCatalogo` en la API): root entra a la pantalla pero no ve la acción. */
  const puedeMercadoPago = !!usuario && puedeGestionarCatalogos(usuario.rolId)

  const [items, setItems] = useState<SucursalListado[]>([])
  const [cargando, setCargando] = useState(true)
  /** Solo la primera lectura reemplaza la tabla por "Cargando…": los refrescos la dejan montada para que los filtros conserven lo escrito. */
  const [cargada, setCargada] = useState(false)
  const [error, setError] = useState('')
  const [aviso, setAviso] = useState('')
  const [formulario, setFormulario] = useState<FormularioDeSucursal | null>(null)
  const [errorFormulario, setErrorFormulario] = useState('')
  const [disparoDeError, setDisparoDeError] = useState(0)
  /** Las empresas del alta se piden al abrirla (no al montar): cambian desde otra pantalla. */
  const [empresas, setEmpresas] = useState<EmpresaListado[] | null>(null)
  const [errorEmpresas, setErrorEmpresas] = useState('')
  /** `ocupado` es el id de la fila en escritura; el alta no tiene fila todavía y usa 0. */
  const [ocupado, setOcupado] = useState<number | null>(null)
  const [baja, setBaja] = useState<SucursalListado | null>(null)
  const [fusion, setFusion] = useState<FormularioDeFusion | null>(null)
  const [errorFusion, setErrorFusion] = useState('')
  const [mercadoPago, setMercadoPago] = useState<SucursalListado | null>(null)
  const [disparadorDeLaPuerta, setDisparadorDeLaPuerta] = useState<HTMLElement | null>(null)

  const generacion = useRef(0)
  const generacionEmpresas = useRef(0)
  const ocupadoRef = useRef(false)
  const nuevoRef = useRef<HTMLButtonElement>(null)

  const cargar = useCallback(async (token: number, propagar = false) => {
    setCargando(true)
    try {
      const filas = await clienteDeOrganizacion.listarSucursales()
      if (generacion.current !== token) return
      setItems(filas)
      setError('')
    } catch (e) {
      if (generacion.current !== token) return
      if (propagar) throw e
      setError(e instanceof ErrorApi ? e.message : 'No se pudieron cargar las sucursales.')
    } finally {
      if (generacion.current === token) {
        setCargando(false)
        setCargada(true)
      }
    }
  }, [])

  useEffect(() => {
    void cargar(++generacion.current)
  }, [cargar])

  /** El refresco post-escritura va fuera del try/catch de la escritura: una escritura que ya commiteó nunca se
   * reporta como fallida (`react-async-state` regla 6). NO apaga `ocupado`: de eso se ocupa el `finally` de cada escritura. */
  async function refrescarTrasEscribir(token: number, mensajeOk: string, avisoDeFallo: string) {
    if (generacion.current !== token) return

    setAviso(mensajeOk)
    try {
      await cargar(token, true)
    } catch {
      if (generacion.current === token) setAviso(`${mensajeOk} ${avisoDeFallo}`)
    }
  }

  async function abrirAlta() {
    if (ocupadoRef.current) return

    const token = ++generacionEmpresas.current
    setFormulario(ALTA_VACIA)
    setEmpresas(null)
    setErrorEmpresas('')
    setErrorFormulario('')
    setError('')
    setAviso('')
    try {
      const filas = await clienteDeOrganizacion.listarEmpresas()
      if (generacionEmpresas.current !== token) return
      setEmpresas(filas)
    } catch (e) {
      if (generacionEmpresas.current !== token) return
      setErrorEmpresas(e instanceof ErrorApi ? e.message : 'No se pudieron cargar las empresas.')
    }
  }

  function abrirEdicion(sucursal: SucursalListado) {
    setErrorFormulario('')
    setFormulario({
      id: sucursal.id,
      idEmpresa: String(sucursal.idEmpresa),
      nombre: sucursal.nombre,
      ...aDireccionDelFormulario(sucursal),
    })
  }

  function cerrarFormulario() {
    generacionEmpresas.current++
    setFormulario(null)
  }

  async function guardar() {
    if (!formulario || ocupadoRef.current) return

    const datos = formulario
    const nombre = datos.nombre.trim()
    if (nombre === '' || (datos.id === null && datos.idEmpresa === '')) {
      setErrorFormulario(datos.id === null ? 'Elegí la empresa y completá el nombre.' : 'Completá el nombre.')
      setDisparoDeError((n) => n + 1)

      return
    }

    const direccion = aDireccionDeLaApi(datos)
    if (direccion.error !== null) {
      setErrorFormulario(direccion.error)
      setDisparoDeError((n) => n + 1)

      return
    }

    const habiaLecturaEnVuelo = cargando
    const token = ++generacion.current
    ocupadoRef.current = true
    setOcupado(datos.id ?? 0)
    setError('')
    setErrorFormulario('')
    setAviso('')
    try {
      // La respuesta de la escritura no se compara con la generación: el modal queda inerte hasta que el refresco aterriza.
      try {
        if (datos.id === null) {
          await clienteDeOrganizacion.crearSucursal({ idEmpresa: Number(datos.idEmpresa), nombre, ...direccion.direccion })
        } else {
          await clienteDeOrganizacion.editarSucursal(datos.id, { nombre, ...direccion.direccion })
        }
      } catch (e) {
        setErrorFormulario(e instanceof ErrorApi ? e.message : 'No se pudo guardar.')
        if (habiaLecturaEnVuelo) void cargar(++generacion.current)

        return
      }

      limpiarFiltros()
      await refrescarTrasEscribir(
        token,
        datos.id === null ? `Se creó la sucursal "${nombre}".` : `Se actualizó "${nombre}".`,
        AVISO_REFRESCO_FALLIDO,
      )
      cerrarFormulario()
    } finally {
      ocupadoRef.current = false
      setOcupado(null)
    }
  }

  function pedirBaja(sucursal: SucursalListado, disparador: HTMLElement | null) {
    if (ocupadoRef.current) return

    setDisparadorDeLaPuerta(disparador)
    setBaja(sucursal)
    setError('')
    setAviso('')
  }

  function cancelarBaja() {
    if (ocupadoRef.current) return

    setDisparadorDeLaPuerta(null)
    setBaja(null)
    setError('')
    setAviso('')
  }

  async function confirmarBaja() {
    if (!baja || ocupadoRef.current) return

    const fila = baja
    const token = ++generacion.current
    ocupadoRef.current = true
    setOcupado(fila.id)
    setError('')
    setAviso('')
    try {
      try {
        await clienteDeOrganizacion.eliminarSucursal(fila.id)
      } catch (e) {
        // Un rechazo SIEMPRE se rinde, con la puerta abierta y el motivo adentro de ella.
        setError(copiaDeFalloDeBaja(e, 'la sucursal'))

        return
      }

      setBaja(null)
      setFormulario((prev) => (prev?.id === fila.id ? null : prev))
      await refrescarTrasEscribir(token, `Se dio de baja la sucursal "${fila.nombre}".`, AVISO_REFRESCO_FALLIDO_BAJA)
    } finally {
      ocupadoRef.current = false
      setOcupado(null)
    }
  }

  function abrirFusion(sucursal: SucursalListado) {
    if (ocupadoRef.current) return

    setErrorFusion('')
    setError('')
    setAviso('')
    setFusion({ origen: sucursal, idDestino: '', confirmado: false })
  }

  function cancelarFusion() {
    if (ocupadoRef.current) return

    setFusion(null)
  }

  async function confirmarFusion() {
    if (!fusion || ocupadoRef.current) return

    const datos = fusion
    const destino = items.find((s) => String(s.id) === datos.idDestino)
    if (!destino || !datos.confirmado) return

    const habiaLecturaEnVuelo = cargando
    const token = ++generacion.current
    ocupadoRef.current = true
    setOcupado(datos.origen.id)
    setErrorFusion('')
    setError('')
    setAviso('')
    try {
      // La respuesta no se compara con la generación: el modal y la tabla quedan inertes hasta que el refresco aterriza.
      try {
        await clienteDeOrganizacion.fusionarSucursal(datos.origen.id, { idSucursalDestino: destino.id })
      } catch (e) {
        setErrorFusion(e instanceof ErrorApi ? e.message : 'No se pudo fusionar.')
        if (habiaLecturaEnVuelo) void cargar(++generacion.current)

        return
      }

      setFormulario((prev) => (prev?.id === datos.origen.id ? null : prev))
      limpiarFiltros()
      await refrescarTrasEscribir(
        token,
        `Se fusionó "${datos.origen.nombre}" en "${destino.nombre}".`,
        AVISO_REFRESCO_FALLIDO,
      )
      setFusion(null)
    } finally {
      ocupadoRef.current = false
      setOcupado(null)
    }
  }

  const bloqueado = ocupado !== null || baja !== null

  const { filas: filtrados, filtros, cambiar: cambiarFiltro, limpiar: limpiarFiltros, hayFiltros } = useFiltrosDeColumna(
    items,
    FILTRABLES,
  )
  const { filas: visibles, orden, alternar } = useOrdenamiento(filtrados, ORDENABLES)

  return (
    <div className="container-fluid py-4">
      <Box titulo="Sucursales" variante="inverse">
        {error && !baja && <div className="alert alert-danger">{error}</div>}
        {aviso && <div className="alert alert-success">{aviso}</div>}

        {baja && (
          <ConfirmacionDeBaja
            titulo={`la sucursal "${baja.nombre}"`}
            ocupado={ocupado !== null}
            disparador={disparadorDeLaPuerta}
            error={error}
            onConfirmar={confirmarBaja}
            onCancelar={cancelarBaja}
          />
        )}

        <div className="mb-3">
          <button
            ref={nuevoRef}
            type="button"
            className="btn btn-primary"
            onClick={() => void abrirAlta()}
            disabled={bloqueado}
          >
            Nueva sucursal
          </button>
        </div>

        <div>
          {formulario && (
            <ModalDeSucursal
              valor={formulario}
              empresas={empresas}
              errorEmpresas={errorEmpresas}
              esPlataforma={esPlataforma}
              guardando={ocupado !== null}
              bloqueado={bloqueado}
              error={errorFormulario}
              disparoDeError={disparoDeError}
              focoDeReserva={nuevoRef}
              onCambio={setFormulario}
              onGuardar={() => void guardar()}
              onCancelar={cerrarFormulario}
            />
          )}
          {mercadoPago && puedeMercadoPago && (
            <ModalDeMercadoPagoDeSucursal
              key={mercadoPago.id}
              sucursal={mercadoPago}
              focoDeReserva={nuevoRef}
              onCerrar={() => setMercadoPago(null)}
            />
          )}
          {fusion && (
            <ModalDeFusionDeSucursal
              valor={fusion}
              sucursales={items}
              guardando={ocupado !== null}
              bloqueado={bloqueado}
              error={errorFusion}
              focoDeReserva={nuevoRef}
              onCambio={setFusion}
              onConfirmar={() => void confirmarFusion()}
              onCancelar={cancelarFusion}
            />
          )}
        </div>

        {!cargada ? (
          <Cargando />
        ) : (
          <div className="table-responsive">
            <table className="table table-striped table-hover table-bordered align-middle">
              <thead>
                <tr>
                  <EncabezadoOrdenable columna="id" orden={orden} onAlternar={alternar}>
                    ID
                  </EncabezadoOrdenable>
                  <EncabezadoOrdenable columna="empresa" orden={orden} onAlternar={alternar}>
                    Empresa
                  </EncabezadoOrdenable>
                  <EncabezadoOrdenable columna="nombre" orden={orden} onAlternar={alternar}>
                    Nombre
                  </EncabezadoOrdenable>
                  <EncabezadoOrdenable columna="cajas" orden={orden} onAlternar={alternar} className="text-end">
                    Puntos de venta
                  </EncabezadoOrdenable>
                  <th scope="col" className="text-end">
                    Acciones
                  </th>
                </tr>
                <FilaDeFiltros
                  celdas={CELDAS}
                  columnas={FILTRABLES}
                  filtros={filtros}
                  onCambio={cambiarFiltro}
                  onLimpiar={limpiarFiltros}
                  hayFiltros={hayFiltros}
                  deshabilitado={bloqueado}
                />
              </thead>
              <tbody>
                {cargando && (
                  <tr>
                    <td colSpan={5}>
                      <Cargando />
                    </td>
                  </tr>
                )}
                {!cargando &&
                  visibles.map((s) => (
                    <tr key={s.id}>
                      <td>{textoDeId(s)}</td>
                      <td>{textoDeEmpresa(s)}</td>
                      <td>{s.nombre}</td>
                      <td className="text-end">{s.cantidadPuntosVenta}</td>
                      <td className="text-end text-nowrap">
                        <BotonIcono icono="editar" className="me-1" onClick={() => abrirEdicion(s)} disabled={bloqueado} />
                        <button
                          type="button"
                          className="btn btn-sm btn-outline-secondary me-1"
                          onClick={() => abrirFusion(s)}
                          disabled={bloqueado}
                        >
                          Fusionar
                        </button>
                        {puedeMercadoPago && (
                          <button
                            type="button"
                            className="btn btn-sm btn-outline-secondary me-1"
                            onClick={() => setMercadoPago(s)}
                            disabled={bloqueado}
                          >
                            Mercado Pago
                          </button>
                        )}
                        <BotonIcono
                          icono="eliminar"
                          onClick={(evento) => pedirBaja(s, evento.currentTarget)}
                          disabled={bloqueado}
                        />
                      </td>
                    </tr>
                  ))}
                {!cargando && visibles.length === 0 && (
                  <tr>
                    <td colSpan={5} className="text-center text-muted py-4">
                      {items.length === 0 ? 'No hay sucursales cargadas.' : 'Ningún registro coincide con los filtros.'}
                    </td>
                  </tr>
                )}
              </tbody>
            </table>
          </div>
        )}
      </Box>
    </div>
  )
}

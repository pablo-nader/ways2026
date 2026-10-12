import { useCallback, useEffect, useRef, useState } from 'react'
import { Link } from 'react-router'
import { useAuth } from '../auth/useAuth'
import { ErrorApi } from '../api/cliente'
import { clienteDePersonal } from '../api/personal'
import { puedeGestionarCatalogos } from '../api/tipos'
import type { ClienteListado, PaginaDe, PersonalListado, UsuarioListado } from '../api/tipos'
import { Box } from '../componentes/Box'
import { BotonIcono } from '../componentes/BotonIcono'
import { CampoImporte } from '../componentes/CampoImporte'
import { Cargando } from '../componentes/Cargando'
import { ConfirmacionDeBaja } from '../componentes/ConfirmacionDeBaja'
import { ErrorDeFormulario } from '../componentes/ErrorDeFormulario'
import { Modal } from '../componentes/Modal'
import { SelectorConBusqueda } from '../componentes/SelectorConBusqueda'
import { formatearImporte } from '../formato/importes'
import {
  aDatos,
  aFormulario,
  copiaDeBajaDePersonal,
  errorAlGuardar,
  formularioVacio,
  opcionesDeClientes,
  opcionesDeUsuarios,
  validarFormulario,
  type CampoConError,
  type ErrorDePersonal,
  type Formulario,
  type OpcionDeVinculo,
  type VinculoActual,
} from './personalFormulario'

const ID_FORMULARIO_PERSONAL = 'formulario-personal'

const AVISO_REFRESCO_FALLIDO = 'Se guardó, pero no se pudo actualizar la vista. Recargá la pantalla.'
const AVISO_REFRESCO_FALLIDO_BAJA = 'Se dio de baja, pero no se pudo actualizar la vista. Recargá la pantalla.'

function formatearMoneda(valor: number): string {
  return formatearImporte(valor, { simbolo: true })
}

function SinVincular() {
  return <span className="text-muted fst-italic">sin vincular</span>
}

/**
 * ABM del personal. Mismo esqueleto que `Proveedores.tsx` (token de generación, `ocupadoRef` de
 * re-entrancia compartido entre guardado y baja, `bloqueado` deja inerte la pantalla entera mientras
 * una escritura está en vuelo). El Supervisor solo lee: las acciones de escritura existen
 * únicamente para quien `puedeGestionarCatalogos`, igual que la política del servidor.
 */
export function Personal() {
  const { usuario } = useAuth()
  const puedeEscribir = usuario !== null && puedeGestionarCatalogos(usuario.rolId)

  const [pagina, setPagina] = useState<PaginaDe<PersonalListado> | null>(null)
  const [busqueda, setBusqueda] = useState('')
  const [cargando, setCargando] = useState(true)
  const [error, setError] = useState('')
  const [aviso, setAviso] = useState('')
  const [formulario, setFormulario] = useState<Formulario | null>(null)
  const [errorFormulario, setErrorFormulario] = useState<ErrorDePersonal | null>(null)
  const [guardando, setGuardando] = useState(false)
  /** Baja pendiente de confirmación: ver `Empresas.tsx`. */
  const [baja, setBaja] = useState<PersonalListado | null>(null)
  const [ocupadoBaja, setOcupadoBaja] = useState<number | null>(null)
  const [disparadorDeLaPuerta, setDisparadorDeLaPuerta] = useState<HTMLElement | null>(null)
  /** Vínculo elegido de cada selector, con su etiqueta: el selector muestra la etiqueta de la opción
   * elegida, y la elegida por búsqueda remota o ya vinculada puede no estar en el listado de
   * referencia. */
  const [vinculosActuales, setVinculosActuales] = useState<{
    usuario: VinculoActual | null
    cliente: VinculoActual | null
  }>({ usuario: null, cliente: null })
  const contenedorRef = useRef<HTMLDivElement>(null)

  const [usuarios, setUsuarios] = useState<UsuarioListado[] | null>(null)
  const [clientes, setClientes] = useState<ClienteListado[] | null>(null)
  const [errorReferencias, setErrorReferencias] = useState('')

  /** Contrato de invalidación compartido por el listado, guardar y la baja: toda acción que
   * reemplaza lo que hay en pantalla (buscar, guardar, dar de baja) acuña una generación nueva ANTES
   * de cambiar el estado, y toda respuesta tardía de una generación anterior se descarta. */
  const generacion = useRef(0)
  /** Espejo síncrono de "hay una escritura en vuelo": la ÚNICA guarda de re-entrancia válida. */
  const ocupadoRef = useRef(false)

  const cargar = useCallback(async (token: number, termino: string, propagar = false) => {
    setCargando(true)
    try {
      const resultado = await clienteDePersonal.listar(termino)
      if (generacion.current !== token) return
      setPagina(resultado)
      setError('')
    } catch (e) {
      if (generacion.current !== token) return
      if (propagar) throw e
      setError(e instanceof ErrorApi ? e.message : 'No se pudo cargar el personal.')
    } finally {
      if (generacion.current === token) setCargando(false)
    }
  }, [])

  useEffect(() => {
    void cargar(++generacion.current, '')
  }, [cargar])

  // Lecturas de referencia (solo quien escribe las necesita). La generación es la propia corrida del
  // efecto: el desmontaje o un cambio de rol la invalidan y una respuesta tardía se descarta.
  useEffect(() => {
    if (!puedeEscribir) return

    let vigente = true
    const falloDeReferencias = () =>
      setErrorReferencias('No se pudieron cargar todos los usuarios y clientes para vincular.')

    clienteDePersonal
      .usuariosParaVincular()
      .then((p) => {
        if (vigente) setUsuarios(p.items)
      })
      .catch(() => {
        if (!vigente) return
        setUsuarios([])
        falloDeReferencias()
      })

    clienteDePersonal
      .clientesParaVincular()
      .then((p) => {
        if (vigente) setClientes(p.items)
      })
      .catch(() => {
        if (!vigente) return
        setClientes([])
        falloDeReferencias()
      })

    return () => {
      vigente = false
    }
  }, [puedeEscribir])

  /** El refresco post-escritura va fuera del try/catch de la escritura: una escritura que ya
   * commiteó nunca se reporta como fallida. */
  async function refrescarTrasEscribir(token: number, mensajeOk: string, avisoDeFallo: string) {
    if (generacion.current !== token) return

    setAviso(mensajeOk)
    try {
      await cargar(token, busqueda, true)
    } catch {
      if (generacion.current === token) setAviso(`${mensajeOk} ${avisoDeFallo}`)
    }
  }

  async function guardar() {
    if (!formulario || ocupadoRef.current) return

    const datos = formulario
    const rechazoLocal = validarFormulario(datos)
    if (rechazoLocal) {
      setErrorFormulario(rechazoLocal)
      return
    }

    const token = ++generacion.current
    ocupadoRef.current = true
    setGuardando(true)
    setError('')
    setErrorFormulario(null)
    setAviso('')
    try {
      try {
        const cuerpo = aDatos(datos)
        if (datos.id === null) {
          await clienteDePersonal.crear(cuerpo)
        } else {
          await clienteDePersonal.actualizar(datos.id, cuerpo)
        }
      } catch (e) {
        if (generacion.current === token) setErrorFormulario(errorAlGuardar(e))

        return
      }

      if (generacion.current !== token) return

      setFormulario(null)
      await refrescarTrasEscribir(
        token,
        datos.id === null ? `Personal "${datos.nombre.trim()}" creado.` : `Personal "${datos.nombre.trim()}" actualizado.`,
        AVISO_REFRESCO_FALLIDO,
      )
    } finally {
      ocupadoRef.current = false
      setGuardando(false)
    }
  }

  function abrirFormulario(valor: Formulario, original: PersonalListado | null) {
    setVinculosActuales({
      usuario:
        original?.idUsuario != null ? { id: original.idUsuario, nombre: original.nombreUsuario ?? `Usuario ${original.idUsuario}` } : null,
      cliente:
        original?.idCliente != null ? { id: original.idCliente, nombre: original.nombreCliente ?? `Cliente ${original.idCliente}` } : null,
    })
    setFormulario(valor)
    setAviso('')
    setError('')
    setErrorFormulario(null)
  }

  /** La modal no ofrece cancelar con una escritura en vuelo, así que cerrar no supersede ninguna
   * escritura y no acuña generación. */
  function cancelarFormulario() {
    setFormulario(null)
    setErrorFormulario(null)
  }

  function cambiarFormulario(valor: Formulario) {
    setFormulario(valor)
    setErrorFormulario(null)
  }

  function elegirVinculo(tipo: 'usuario' | 'cliente', opcion: OpcionDeVinculo | null) {
    setVinculosActuales((prev) => ({ ...prev, [tipo]: opcion && { id: opcion.id, nombre: opcion.etiqueta } }))
    setFormulario((prev) =>
      prev && { ...prev, [tipo === 'usuario' ? 'idUsuario' : 'idCliente']: opcion?.id ?? null },
    )
    setErrorFormulario(null)
  }

  const buscarUsuarios = useCallback(
    async (texto: string) => opcionesDeUsuarios((await clienteDePersonal.usuariosParaVincular(texto)).items, null),
    [],
  )
  const buscarClientes = useCallback(
    async (texto: string) => opcionesDeClientes((await clienteDePersonal.clientesParaVincular(texto)).items, null),
    [],
  )

  function pedirBaja(p: PersonalListado, disparador: HTMLElement | null) {
    if (ocupadoRef.current) return

    setDisparadorDeLaPuerta(disparador)
    setBaja(p)
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

    const p = baja
    const token = ++generacion.current
    ocupadoRef.current = true
    setOcupadoBaja(p.id)
    setError('')
    setAviso('')
    try {
      try {
        await clienteDePersonal.eliminar(p.id)
      } catch (e) {
        setError(copiaDeBajaDePersonal(e, p.nombre))

        return
      }

      setBaja(null)
      setFormulario((prev) => (prev?.id === p.id ? null : prev))
      await refrescarTrasEscribir(token, `Personal "${p.nombre}" dado de baja.`, AVISO_REFRESCO_FALLIDO_BAJA)
    } finally {
      ocupadoRef.current = false
      setOcupadoBaja(null)
    }
  }

  /** La puerta abierta o cualquier escritura en vuelo bloquean la pantalla entera. */
  const bloqueado = guardando || ocupadoBaja !== null || baja !== null

  const herramientas = (
    <nav className="p-2 d-flex gap-2">
      <input
        type="search"
        className="form-control form-control-sm"
        placeholder="Buscar por nombre o documento…"
        aria-label="Buscar personal"
        value={busqueda}
        onChange={(e) => setBusqueda(e.target.value)}
        onKeyDown={(e) => e.key === 'Enter' && cargar(++generacion.current, busqueda)}
        disabled={bloqueado}
      />
      <button
        type="button"
        className="btn btn-sm btn-outline-secondary"
        onClick={() => cargar(++generacion.current, busqueda)}
        disabled={bloqueado}
      >
        Buscar
      </button>
      {puedeEscribir && (
        <BotonIcono
          icono="agregar"
          className="text-nowrap"
          onClick={() => abrirFormulario(formularioVacio(), null)}
          disabled={bloqueado}
        />
      )}
    </nav>
  )

  return (
    <div ref={contenedorRef} tabIndex={-1} style={{ outline: 'none' }} className="container-fluid py-4">
      <Box titulo="Personal" variante="inverse" herramientas={herramientas}>
        {error && <div className="alert alert-danger">{error}</div>}
        {aviso && <div className="alert alert-success">{aviso}</div>}

        {baja && (
          <ConfirmacionDeBaja
            titulo={`el personal "${baja.nombre}"`}
            ocupado={ocupadoBaja !== null}
            disparador={disparadorDeLaPuerta}
            onConfirmar={confirmarBaja}
            onCancelar={cancelarBaja}
          />
        )}

        {puedeEscribir && formulario && (
          <ModalDePersonal
            valor={formulario}
            opcionesUsuario={opcionesDeUsuarios(usuarios ?? [], vinculosActuales.usuario)}
            opcionesCliente={opcionesDeClientes(clientes ?? [], vinculosActuales.cliente)}
            alBuscarUsuarios={buscarUsuarios}
            alBuscarClientes={buscarClientes}
            errorReferencias={errorReferencias}
            guardando={guardando}
            bloqueado={bloqueado}
            error={errorFormulario}
            focoDeReserva={contenedorRef}
            onCambio={cambiarFormulario}
            onElegirVinculo={elegirVinculo}
            onGuardar={guardar}
            onCancelar={cancelarFormulario}
          />
        )}

        {cargando ? (
          <Cargando />
        ) : (
          <div className="table-responsive">
            <table className="table table-striped table-hover table-bordered align-middle">
              <thead>
                <tr>
                  <th>Nombre</th>
                  <th className="text-end">Valor hora</th>
                  <th className="text-end" title="Positivo: la empresa le debe al empleado.">
                    Saldo
                  </th>
                  <th>Usuario</th>
                  <th>Cliente</th>
                  <th className="text-end">Acciones</th>
                </tr>
              </thead>
              <tbody>
                {pagina?.items.map((p) => (
                  <tr key={p.id}>
                    <td>
                      {p.nombre}
                      {p.documento && <div className="text-muted small">{p.documento}</div>}
                    </td>
                    <td className="text-end">{formatearMoneda(p.valorHora)}</td>
                    <td className="text-end">{formatearMoneda(p.saldo)}</td>
                    <td>{p.nombreUsuario ?? <SinVincular />}</td>
                    <td>{p.nombreCliente ?? <SinVincular />}</td>
                    <td className="text-end text-nowrap">
                      <Link
                        to={`/personal/${p.id}/cuenta`}
                        className="btn btn-sm btn-outline-secondary me-1"
                        aria-label={`Cuenta de ${p.nombre}`}
                      >
                        Cuenta
                      </Link>
                      {puedeEscribir && (
                        <>
                          <BotonIcono
                            icono="editar"
                            etiqueta={`Editar ${p.nombre}`}
                            className="me-1"
                            onClick={() => abrirFormulario(aFormulario(p), p)}
                            disabled={bloqueado}
                          />
                          <BotonIcono
                            icono="eliminar"
                            etiqueta={`Eliminar ${p.nombre}`}
                            onClick={(evento) => pedirBaja(p, evento.currentTarget)}
                            disabled={bloqueado}
                          />
                        </>
                      )}
                    </td>
                  </tr>
                ))}
                {pagina?.items.length === 0 && (
                  <tr>
                    <td colSpan={6} className="text-center text-muted py-4">
                      No hay personal que coincida con la búsqueda.
                    </td>
                  </tr>
                )}
              </tbody>
            </table>
            {pagina && pagina.total > pagina.items.length && (
              <p className="small text-muted">
                Se muestran {pagina.items.length} de {pagina.total}. Refiná la búsqueda para ver el resto.
              </p>
            )}
          </div>
        )}
      </Box>
    </div>
  )
}

function ErrorDeCampo({ id, error, campo }: { id: string; error: ErrorDePersonal | null; campo: CampoConError }) {
  if (error?.campo !== campo) return null

  return (
    <div id={id} role="alert" className="invalid-feedback d-block">
      {error.mensaje}
    </div>
  )
}

function ModalDePersonal({
  valor,
  opcionesUsuario,
  opcionesCliente,
  alBuscarUsuarios: buscarUsuarios,
  alBuscarClientes: buscarClientes,
  errorReferencias,
  guardando,
  bloqueado,
  error,
  focoDeReserva,
  onCambio,
  onElegirVinculo,
  onGuardar,
  onCancelar,
}: {
  valor: Formulario
  opcionesUsuario: OpcionDeVinculo[]
  opcionesCliente: OpcionDeVinculo[]
  alBuscarUsuarios: (texto: string) => Promise<OpcionDeVinculo[]>
  alBuscarClientes: (texto: string) => Promise<OpcionDeVinculo[]>
  errorReferencias: string
  guardando: boolean
  bloqueado: boolean
  error: ErrorDePersonal | null
  focoDeReserva: React.RefObject<HTMLElement | null>
  onCambio: (f: Formulario) => void
  onElegirVinculo: (tipo: 'usuario' | 'cliente', opcion: OpcionDeVinculo | null) => void
  onGuardar: () => void
  onCancelar: () => void
}) {
  const esNuevo = valor.id === null

  const pie = (
    <>
      <button type="button" className="btn btn-outline-secondary" onClick={onCancelar} disabled={bloqueado}>
        Cancelar
      </button>
      <button type="submit" form={ID_FORMULARIO_PERSONAL} className="btn btn-success" disabled={bloqueado}>
        {guardando ? 'Guardando…' : 'Guardar'}
      </button>
    </>
  )

  return (
    <Modal
      titulo={esNuevo ? 'Nuevo personal' : 'Editar personal'}
      tamano="xl"
      desplazable
      ocupado={bloqueado}
      focoDeReserva={focoDeReserva}
      pie={pie}
      onCerrar={onCancelar}
    >
      <ErrorDeFormulario mensaje={error?.campo === 'general' ? error.mensaje : ''} />
      {errorReferencias && <div className="alert alert-warning py-2 px-3 small">{errorReferencias}</div>}
      <form
        id={ID_FORMULARIO_PERSONAL}
        className="row g-3"
        autoComplete="off"
        noValidate
        onSubmit={(e) => {
          e.preventDefault()
          onGuardar()
        }}
      >
        <div className="col-md-6">
          <label className="form-label" htmlFor="per-nombre">
            Nombre
          </label>
          <input
            id="per-nombre"
            autoFocus
            className={`form-control${error?.campo === 'nombre' ? ' is-invalid' : ''}`}
            maxLength={150}
            value={valor.nombre}
            aria-invalid={error?.campo === 'nombre' || undefined}
            aria-describedby={error?.campo === 'nombre' ? 'per-nombre-error' : undefined}
            onChange={(e) => onCambio({ ...valor, nombre: e.target.value })}
            disabled={bloqueado}
          />
          <ErrorDeCampo id="per-nombre-error" error={error} campo="nombre" />
        </div>

        <div className="col-md-3">
          <label className="form-label" htmlFor="per-valor-hora">
            Valor hora
          </label>
          <CampoImporte
            id="per-valor-hora"
            className={`form-control${error?.campo === 'valorHora' ? ' is-invalid' : ''}`}
            valor={valor.valorHora}
            aria-invalid={error?.campo === 'valorHora' || undefined}
            aria-describedby={error?.campo === 'valorHora' ? 'per-valor-hora-error' : undefined}
            onChange={(valorHora) => onCambio({ ...valor, valorHora })}
            disabled={bloqueado}
          />
          <ErrorDeCampo id="per-valor-hora-error" error={error} campo="valorHora" />
        </div>

        <div className="col-md-3">
          <label className="form-label" htmlFor="per-documento">
            Documento
          </label>
          <input
            id="per-documento"
            className="form-control"
            maxLength={30}
            value={valor.documento}
            onChange={(e) => onCambio({ ...valor, documento: e.target.value })}
            disabled={bloqueado}
          />
        </div>

        <div className="col-md-4">
          <label className="form-label" htmlFor="per-telefono">
            Teléfono
          </label>
          <input
            id="per-telefono"
            className="form-control"
            maxLength={50}
            value={valor.telefono}
            onChange={(e) => onCambio({ ...valor, telefono: e.target.value })}
            disabled={bloqueado}
          />
        </div>

        <CampoDeVinculo
          id="per-usuario"
          etiqueta="Usuario vinculado"
          opciones={opcionesUsuario}
          valor={valor.idUsuario}
          error={error?.campo === 'idUsuario' ? error : null}
          bloqueado={bloqueado}
          alBuscar={buscarUsuarios}
          onElegir={(opcion) => onElegirVinculo('usuario', opcion)}
        />

        <CampoDeVinculo
          id="per-cliente"
          etiqueta="Cliente vinculado"
          opciones={opcionesCliente}
          valor={valor.idCliente}
          error={error?.campo === 'idCliente' ? error : null}
          bloqueado={bloqueado}
          alBuscar={buscarClientes}
          onElegir={(opcion) => onElegirVinculo('cliente', opcion)}
        />

        <div className="col-12">
          <label className="form-label" htmlFor="per-observaciones">
            Observaciones
          </label>
          <textarea
            id="per-observaciones"
            className="form-control"
            rows={3}
            value={valor.observaciones}
            onChange={(e) => onCambio({ ...valor, observaciones: e.target.value })}
            disabled={bloqueado}
          />
        </div>
      </form>
    </Modal>
  )
}

/** Selector de un vínculo opcional: combobox con búsqueda (el mismo que usa el POS para el cliente)
 * más un botón para quitar el vínculo, porque el combobox por sí solo no permite dejarlo vacío. */
function CampoDeVinculo({
  id,
  etiqueta,
  opciones,
  valor,
  error,
  bloqueado,
  alBuscar,
  onElegir,
}: {
  id: string
  etiqueta: string
  opciones: OpcionDeVinculo[]
  valor: number | null
  error: ErrorDePersonal | null
  bloqueado: boolean
  alBuscar: (texto: string) => Promise<OpcionDeVinculo[]>
  onElegir: (opcion: OpcionDeVinculo | null) => void
}) {
  return (
    <div className="col-md-4">
      <label className="form-label" htmlFor={id}>
        {etiqueta}
      </label>
      <div className="d-flex gap-2">
        <div className="flex-grow-1" style={{ minWidth: 0 }}>
          <SelectorConBusqueda<OpcionDeVinculo>
            id={id}
            nombreAccesible={etiqueta}
            className={error ? 'is-invalid' : undefined}
            opciones={opciones}
            valor={valor}
            placeholder="Sin vincular"
            disabled={bloqueado}
            alBuscar={alBuscar}
            onChange={onElegir}
          />
        </div>
        {valor !== null && (
          <button
            type="button"
            className="btn btn-outline-secondary text-nowrap"
            aria-label={`Quitar ${etiqueta.toLowerCase()}`}
            onClick={() => onElegir(null)}
            disabled={bloqueado}
          >
            Quitar
          </button>
        )}
      </div>
      {error && (
        <div role="alert" className="invalid-feedback d-block">
          {error.mensaje}
        </div>
      )}
    </div>
  )
}

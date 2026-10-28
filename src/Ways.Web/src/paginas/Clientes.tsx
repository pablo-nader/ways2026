import { useCallback, useEffect, useRef, useState } from 'react'
import { Link } from 'react-router'
import { ErrorApi } from '../api/cliente'
import { clienteDeCatalogosFiscales } from '../api/catalogos'
import { clienteDeClientes } from '../api/clientes'
import type { ClienteListado, CondicionFiscalListado, ListaPrecioAsignable, PaginaDe } from '../api/tipos'
import { Box } from '../componentes/Box'
import { Cargando } from '../componentes/Cargando'
import { ConfirmacionDeBaja } from '../componentes/ConfirmacionDeBaja'
import { BotonIcono } from '../componentes/BotonIcono'
import { aAlta, aFormulario, formularioVacio, type Formulario } from './clientes/clienteFormulario'
import { ModalDeCliente } from './clientes/ModalDeCliente'

/**
 * ABM dedicado de clientes (design decision 1: no la máquina genérica de catálogos). La fila
 * Consumidor Final (`numero = 1`) se muestra siempre, pero sus acciones de editar/eliminar
 * están deshabilitadas del lado del cliente (defensa en profundidad sobre el guard real, que
 * vive en `ServicioDeClientes`/`ReglaDeClientes`).
 */
export function Clientes() {
  const [pagina, setPagina] = useState<PaginaDe<ClienteListado> | null>(null)
  const [condiciones, setCondiciones] = useState<CondicionFiscalListado[]>([])
  const [listas, setListas] = useState<ListaPrecioAsignable[]>([])
  const [busqueda, setBusqueda] = useState('')
  const [cargando, setCargando] = useState(true)
  const [error, setError] = useState('')
  const [aviso, setAviso] = useState('')
  const [formulario, setFormulario] = useState<Formulario | null>(null)
  const [errorFormulario, setErrorFormulario] = useState('')
  const [guardando, setGuardando] = useState(false)
  /** Baja pendiente de confirmación, con el botón que la pidió (capturado en su `onClick`). */
  const [baja, setBaja] = useState<{ cliente: ClienteListado; disparador: HTMLElement } | null>(null)
  const [errorDeBaja, setErrorDeBaja] = useState('')
  const [eliminando, setEliminando] = useState(false)
  /** Espejo síncrono de "hay una baja en vuelo": dos clics en el mismo tick no emiten dos DELETE. */
  const eliminandoRef = useRef(false)
  /** Espejo síncrono de "hay un guardado en vuelo": un doble clic en el mismo tick no emite dos escrituras. */
  const guardandoRef = useRef(false)
  /** Destino de foco al cerrar el modal cuando su disparador ya no existe (la fila de "Editar" se
   * reemplaza por "Cargando…" durante el refresco): el botón de alta, que es estable. */
  const agregarRef = useRef<HTMLButtonElement>(null)
  // Último término pedido: el refresco posterior a una escritura recarga con ese, no con el texto de la caja sin enviar.
  const terminoVigente = useRef('')

  const cargar = useCallback(async (termino: string) => {
    terminoVigente.current = termino
    setCargando(true)
    setError('')
    try {
      setPagina(await clienteDeClientes.listar(termino, false))
    } catch (e) {
      setError(e instanceof ErrorApi ? e.message : 'No se pudieron cargar los clientes.')
    } finally {
      setCargando(false)
    }
  }, [])

  useEffect(() => {
    void cargar('')
    clienteDeCatalogosFiscales.condicionesFiscales().then(setCondiciones).catch(() => setCondiciones([]))
    clienteDeClientes.listasDePrecioAsignables().then(setListas).catch(() => setListas([]))
  }, [cargar])

  const idListaPorDefecto = listas.find((l) => l.esDefault)?.id ?? listas[0]?.id ?? ''

  async function guardar() {
    if (!formulario || guardandoRef.current) return

    guardandoRef.current = true
    setGuardando(true)
    setError('')
    setErrorFormulario('')
    setAviso('')

    try {
      const datos = aAlta(formulario)

      if (formulario.id === null) {
        await clienteDeClientes.crear(datos)
        setAviso(`Cliente "${formulario.nombre}" creado.`)
      } else {
        await clienteDeClientes.actualizar(formulario.id, datos)
        setAviso(`Cliente "${formulario.nombre}" actualizado.`)
      }

      // El modal sigue montado durante el refresco y se cierra en el mismo lote que libera `guardando`:
      // así el disparador ya está habilitado cuando el modal restaura el foco.
      await cargar(terminoVigente.current)
      setFormulario(null)
    } catch (e) {
      setErrorFormulario(e instanceof ErrorApi ? e.message : 'No se pudo guardar.')
    } finally {
      guardandoRef.current = false
      setGuardando(false)
    }
  }

  function abrirFormulario(valor: Formulario) {
    setFormulario(valor)
    setAviso('')
    setError('')
    setErrorFormulario('')
  }

  function pedirBaja(c: ClienteListado, disparador: HTMLElement) {
    setErrorDeBaja('')
    setBaja({ cliente: c, disparador })
  }

  function cancelarBaja() {
    setErrorDeBaja('')
    setBaja(null)
  }

  async function confirmarBaja() {
    if (baja === null || eliminandoRef.current) return
    eliminandoRef.current = true
    const c = baja.cliente

    setError('')
    setAviso('')
    setErrorDeBaja('')
    setEliminando(true)
    try {
      try {
        await clienteDeClientes.eliminar(c.id)
      } catch (e) {
        setErrorDeBaja(e instanceof ErrorApi ? e.message : 'No se pudo dar de baja.')
        return
      }

      setAviso(`Cliente "${c.nombre}" dado de baja.`)
      setBaja(null)
      // `cargar` reporta su propio fallo en `error`: la baja ya se confirmó y no se informa como rechazada.
      await cargar(terminoVigente.current)
    } finally {
      eliminandoRef.current = false
      setEliminando(false)
    }
  }

  const herramientas = (
    <nav className="p-2 d-flex gap-2">
      <input
        type="search"
        className="form-control form-control-sm"
        placeholder="Buscar por nombre, apellido, razón social o documento…"
        value={busqueda}
        onChange={(e) => setBusqueda(e.target.value)}
        onKeyDown={(e) => e.key === 'Enter' && cargar(busqueda)}
      />
      <button
        type="button"
        className="btn btn-sm btn-outline-secondary"
        onClick={() => cargar(busqueda)}
      >
        Buscar
      </button>
      <BotonIcono
        ref={agregarRef}
        icono="agregar"
        className="text-nowrap"
        disabled={guardando}
        onClick={() => abrirFormulario(formularioVacio(idListaPorDefecto))}
      />
    </nav>
  )

  return (
    <div className="container-fluid py-4">
      <Box titulo="Clientes" variante="inverse" herramientas={herramientas}>
        {error && <div className="alert alert-danger">{error}</div>}
        {aviso && <div className="alert alert-success">{aviso}</div>}

        {formulario && (
          <ModalDeCliente
            valor={formulario}
            condiciones={condiciones}
            listas={listas}
            guardando={guardando}
            error={errorFormulario}
            focoDeReserva={agregarRef}
            onCambio={setFormulario}
            onGuardar={guardar}
            onCancelar={() => setFormulario(null)}
          />
        )}

        {baja && (
          <ConfirmacionDeBaja
            titulo={`al cliente "${baja.cliente.nombre}"`}
            ocupado={eliminando}
            disparador={baja.disparador}
            error={errorDeBaja || null}
            onConfirmar={() => void confirmarBaja()}
            onCancelar={cancelarBaja}
          />
        )}

        {cargando ? (
          <Cargando />
        ) : (
          <div className="table-responsive">
            <table className="table table-striped table-hover table-bordered align-middle">
              <thead>
                <tr>
                  <th>N°</th>
                  <th>Nombre</th>
                  <th>Documento</th>
                  <th>Teléfono</th>
                  <th>Email</th>
                  <th>Estado</th>
                  <th className="text-end">Acciones</th>
                </tr>
              </thead>
              <tbody>
                {pagina?.items.map((c) => (
                  <tr key={c.id}>
                    <td>{String(c.numero).padStart(4, '0')}</td>
                    <td>
                      {[c.nombre, c.apellido].filter(Boolean).join(' ')}
                      {c.razonSocial && <div className="text-muted small">{c.razonSocial}</div>}
                      {c.esConsumidorFinal && (
                        <span className="badge text-bg-secondary ms-1">Protegido</span>
                      )}
                    </td>
                    <td>
                      {c.tipoDocumento && c.numeroDocumento ? `${c.tipoDocumento}: ${c.numeroDocumento}` : '—'}
                    </td>
                    <td>{c.telefono ?? c.celular ?? '—'}</td>
                    <td>{c.email ?? '—'}</td>
                    <td>
                      <span className={`badge ${c.activo ? 'text-bg-success' : 'text-bg-secondary'}`}>
                        {c.activo ? 'Activo' : 'Inactivo'}
                      </span>
                    </td>
                    <td className="text-end text-nowrap">
                      {/* stage-7-cuenta-corriente (Slice 5, design: Web Composition): entrada al
                          estado de cuenta — el cliente lo identifica la URL, la fila lo pasa por
                          state para no pagar un GET extra por el nombre a mostrar. */}
                      <Link
                        className="btn btn-sm btn-outline-secondary me-1"
                        to={`/clientes/${c.id}/cuenta-corriente`}
                        state={{ cliente: c }}
                      >
                        Estado de cuenta
                      </Link>
                      <BotonIcono
                        icono="editar"
                        className="me-1"
                        disabled={c.esConsumidorFinal || guardando}
                        title={c.esConsumidorFinal ? 'El Consumidor Final no se puede editar.' : undefined}
                        onClick={() => abrirFormulario(aFormulario(c))}
                      />
                      <BotonIcono
                        icono="eliminar"
                        disabled={c.esConsumidorFinal}
                        title={c.esConsumidorFinal ? 'El Consumidor Final no se puede eliminar.' : undefined}
                        onClick={(evento) => pedirBaja(c, evento.currentTarget)}
                      />
                    </td>
                  </tr>
                ))}
                {pagina?.items.length === 0 && (
                  <tr>
                    <td colSpan={7} className="text-center text-muted py-4">
                      No hay clientes que coincidan con la búsqueda.
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

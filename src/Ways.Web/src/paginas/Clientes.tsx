import { useCallback, useEffect, useRef, useState } from 'react'
import { Link } from 'react-router'
import { ErrorApi } from '../api/cliente'
import { clienteDeCatalogosFiscales } from '../api/catalogos'
import {
  clienteDeClientes,
  esColumnaOrdenableDeClientes,
  FILTROS_DE_CLIENTES_VACIOS,
  type ColumnaOrdenableDeClientes,
  type ConsultaDeClientes,
  type FiltrosDeClientes,
} from '../api/clientes'
import { ultimaPaginaDePadron } from '../api/padron'
import type { ClienteListado, CondicionFiscalListado, ListaPrecioAsignable, PaginaDe } from '../api/tipos'
import { Box } from '../componentes/Box'
import { Cargando } from '../componentes/Cargando'
import { ConfirmacionDeBaja } from '../componentes/ConfirmacionDeBaja'
import { BotonIcono } from '../componentes/BotonIcono'
import { EncabezadoOrdenable } from '../componentes/grilla'
import { FilaDeFiltrosDePadron, type CeldaDeFiltroDePadron } from '../componentes/padron/FilaDeFiltrosDePadron'
import { PaginadorDePadron } from '../componentes/padron/PaginadorDePadron'
import { usePadronEnServidor } from '../componentes/padron/usePadronEnServidor'
import { aAlta, aFormulario, formularioVacio, type Formulario } from './clientes/clienteFormulario'
import { ModalDeCliente } from './clientes/ModalDeCliente'
import { toast } from '../toasts/toasts'

const CELDAS_DE_FILTRO: readonly CeldaDeFiltroDePadron<FiltrosDeClientes>[] = [
  { tipo: 'entero', clave: 'numero', etiqueta: 'N°' },
  { tipo: 'texto', clave: 'nombre', etiqueta: 'nombre' },
  { tipo: 'texto', clave: 'documento', etiqueta: 'número de documento' },
  { tipo: 'texto', clave: 'telefono', etiqueta: 'teléfono' },
  { tipo: 'texto', clave: 'email', etiqueta: 'email' },
  {
    tipo: 'opciones',
    clave: 'activo',
    etiqueta: 'estado',
    opciones: [
      { valor: 'true', etiqueta: 'Activo' },
      { valor: 'false', etiqueta: 'Inactivo' },
    ],
  },
  { tipo: 'acciones' },
]

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
  /** Texto de la caja de búsqueda general, que se aplica con Enter o con Buscar. */
  const [textoDeBusqueda, setTextoDeBusqueda] = useState('')
  const [cargando, setCargando] = useState(true)
  /** Hasta la primera respuesta la pantalla es un "Cargando…"; después la tabla y sus filtros no se desmontan. */
  const [iniciando, setIniciando] = useState(true)
  const [error, setError] = useState('')
  const [formulario, setFormulario] = useState<Formulario | null>(null)
  const [errorFormulario, setErrorFormulario] = useState('')
  const [guardando, setGuardando] = useState(false)
  /** Baja pendiente de confirmación, con el botón que la pidió (capturado en su `onClick`). */
  const [baja, setBaja] = useState<{ cliente: ClienteListado; disparador: HTMLElement } | null>(null)
  const [errorDeBaja, setErrorDeBaja] = useState('')
  const [eliminando, setEliminando] = useState(false)
  /** Espejo síncrono de "hay una baja en vuelo": dos clics en el mismo tick no emiten dos DELETE. */
  const eliminandoRef = useRef(false)
  /** Espejo síncrono de "hay una escritura en vuelo" (guardado o baja): un doble clic en el mismo tick no emite dos
   * escrituras. */
  const guardandoRef = useRef(false)
  /** Espejo de `bloqueado` (escritura en vuelo o puerta de baja abierta) para el temporizador de los filtros: un filtro
   * tipeado antes espera a que termine. */
  const bloqueadoRef = useRef(false)
  /** Destino de foco al cerrar el modal cuando su disparador ya no existe (la fila de "Editar" se
   * reemplaza por "Cargando…" durante el refresco): el botón de alta, que es estable. */
  const agregarRef = useRef<HTMLButtonElement>(null)
  /** Cada lectura del listado acuña una generación: una respuesta tardía de una consulta que el operador ya cambió se descarta. */
  const generacion = useRef(0)

  const padron = usePadronEnServidor<FiltrosDeClientes, ColumnaOrdenableDeClientes>(FILTROS_DE_CLIENTES_VACIOS, {
    esColumnaOrdenable: esColumnaOrdenableDeClientes,
    ocupado: () => guardandoRef.current || bloqueadoRef.current,
  })
  const { irAPagina } = padron

  const cargar = useCallback(
    async (token: number, consulta: ConsultaDeClientes) => {
      setCargando(true)
      setError('')
      try {
        const resultado = await clienteDeClientes.listarGrilla(consulta)
        if (generacion.current !== token) return

        // Una baja puede vaciar la última página: se pide la última válida en vez de mostrar "Página 3 de 2".
        const ultima = ultimaPaginaDePadron(resultado.total, resultado.tamanio)
        if (resultado.pagina > ultima) {
          irAPagina(ultima)

          return
        }

        setPagina(resultado)
      } catch (e) {
        if (generacion.current !== token) return
        // Sin filas: el encabezado ya declara el orden pedido y no puede quedar sobre las del orden anterior.
        setPagina(null)
        setError(e instanceof ErrorApi ? e.message : 'No se pudieron cargar los clientes.')
      } finally {
        if (generacion.current === token) {
          setCargando(false)
          setIniciando(false)
        }
      }
    },
    [irAPagina],
  )

  // Se vuelve a pedir el listado cada vez que cambia la consulta aplicada (filtros, orden, página o tamaño).
  useEffect(() => {
    void cargar(++generacion.current, padron.consulta)
  }, [cargar, padron.consulta])

  useEffect(() => {
    clienteDeCatalogosFiscales.condicionesFiscales().then(setCondiciones).catch(() => setCondiciones([]))
    clienteDeClientes.listasDePrecioAsignables().then(setListas).catch(() => setListas([]))
  }, [])

  const idListaPorDefecto = listas.find((l) => l.esDefault)?.id ?? listas[0]?.id ?? ''

  async function guardar() {
    if (!formulario || guardandoRef.current) return

    guardandoRef.current = true
    setGuardando(true)
    setError('')
    setErrorFormulario('')

    try {
      const datos = aAlta(formulario)

      if (formulario.id === null) {
        await clienteDeClientes.crear(datos)
        toast.exito(`Cliente "${formulario.nombre}" creado.`)
      } else {
        await clienteDeClientes.actualizar(formulario.id, datos)
        toast.exito(`Cliente "${formulario.nombre}" actualizado.`)
      }

      // El modal sigue montado durante el refresco y se cierra en el mismo lote que libera `guardando`:
      // así el disparador ya está habilitado cuando el modal restaura el foco.
      await cargar(++generacion.current, padron.consultaRef.current)
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

    guardandoRef.current = true
    setError('')
    setErrorDeBaja('')
    setEliminando(true)
    try {
      try {
        await clienteDeClientes.eliminar(c.id)
      } catch (e) {
        setErrorDeBaja(e instanceof ErrorApi ? e.message : 'No se pudo dar de baja.')
        return
      }

      toast.exito(`Cliente "${c.nombre}" dado de baja.`)
      setBaja(null)
      // `cargar` reporta su propio fallo en `error`: la baja ya se confirmó y no se informa como rechazada.
      await cargar(++generacion.current, padron.consultaRef.current)
    } finally {
      guardandoRef.current = false
      eliminandoRef.current = false
      setEliminando(false)
    }
  }

  /** Los filtros, el orden y el pager quedan inertes mientras hay una escritura en vuelo o la confirmación de baja abierta. */
  const bloqueado = guardando || eliminando || baja !== null
  useEffect(() => {
    bloqueadoRef.current = bloqueado
  })

  const herramientas = (
    <nav className="p-2 d-flex gap-2">
      <input
        type="search"
        className="form-control form-control-sm"
        placeholder="Buscar por nombre, apellido, razón social o documento…"
        value={textoDeBusqueda}
        disabled={bloqueado}
        onChange={(e) => setTextoDeBusqueda(e.target.value)}
        onKeyDown={(e) => e.key === 'Enter' && padron.cambiarFiltros({ busqueda: textoDeBusqueda }, true)}
      />
      <button
        type="button"
        className="btn btn-sm btn-outline-secondary"
        disabled={bloqueado}
        onClick={() => padron.cambiarFiltros({ busqueda: textoDeBusqueda }, true)}
      >
        Buscar
      </button>
      <BotonIcono
        ref={agregarRef}
        icono="agregar"
        className="text-nowrap"
        disabled={bloqueado}
        onClick={() => abrirFormulario(formularioVacio(idListaPorDefecto))}
      />
    </nav>
  )

  return (
    <div className="container-fluid py-4">
      <Box titulo="Clientes" variante="inverse" herramientas={herramientas}>
        {error && <div className="alert alert-danger">{error}</div>}

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

        {iniciando ? (
          <Cargando />
        ) : (
          <>
          <div className="table-responsive">
            <table className="table table-striped table-hover table-bordered align-middle">
              <thead>
                <tr>
                  <EncabezadoOrdenable columna="numero" orden={padron.consulta.orden} onAlternar={padron.alternarOrden}>
                    N°
                  </EncabezadoOrdenable>
                  <EncabezadoOrdenable columna="nombre" orden={padron.consulta.orden} onAlternar={padron.alternarOrden}>
                    Nombre
                  </EncabezadoOrdenable>
                  <EncabezadoOrdenable columna="documento" orden={padron.consulta.orden} onAlternar={padron.alternarOrden}>
                    Documento
                  </EncabezadoOrdenable>
                  <EncabezadoOrdenable columna="telefono" orden={padron.consulta.orden} onAlternar={padron.alternarOrden}>
                    Teléfono
                  </EncabezadoOrdenable>
                  <EncabezadoOrdenable columna="email" orden={padron.consulta.orden} onAlternar={padron.alternarOrden}>
                    Email
                  </EncabezadoOrdenable>
                  <EncabezadoOrdenable columna="estado" orden={padron.consulta.orden} onAlternar={padron.alternarOrden}>
                    Estado
                  </EncabezadoOrdenable>
                  <th scope="col" className="text-end">
                    Acciones
                  </th>
                </tr>
                <FilaDeFiltrosDePadron
                  celdas={CELDAS_DE_FILTRO}
                  valores={padron.borrador}
                  onCambio={padron.cambiarFiltros}
                  onLimpiar={() => {
                    setTextoDeBusqueda('')
                    padron.limpiar()
                  }}
                  hayFiltros={padron.hayFiltros}
                  deshabilitado={bloqueado}
                />
              </thead>
              <tbody style={cargando ? { opacity: 0.6 } : undefined}>
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
                        disabled={c.esConsumidorFinal || bloqueado}
                        title={c.esConsumidorFinal ? 'El Consumidor Final no se puede editar.' : undefined}
                        onClick={() => abrirFormulario(aFormulario(c))}
                      />
                      <BotonIcono
                        icono="eliminar"
                        disabled={c.esConsumidorFinal || bloqueado}
                        title={c.esConsumidorFinal ? 'El Consumidor Final no se puede eliminar.' : undefined}
                        onClick={(evento) => pedirBaja(c, evento.currentTarget)}
                      />
                    </td>
                  </tr>
                ))}
                {pagina?.items.length === 0 && (
                  <tr>
                    <td colSpan={7} className="text-center text-muted py-4">
                      No hay clientes que coincidan con los filtros.
                    </td>
                  </tr>
                )}
              </tbody>
            </table>
          </div>
          {pagina && (
            <PaginadorDePadron
              pagina={pagina.pagina}
              tamanio={pagina.tamanio}
              total={pagina.total}
              unidad="cliente"
              deshabilitado={cargando || bloqueado}
              onCambiarPagina={padron.cambiarPagina}
              onCambiarTamanio={padron.cambiarTamanio}
            />
          )}
          </>
        )}
      </Box>
    </div>
  )
}

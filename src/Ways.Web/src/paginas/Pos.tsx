import { useCallback, useEffect, useRef, useState } from 'react'
import { clienteDeArticulos } from '../api/articulos'
import { reducirCarrito, type AccionCarrito, type LineaCarrito } from '../api/carrito'
import { ErrorApi } from '../api/cliente'
import { clienteDeClientes } from '../api/clientes'
import { clienteDeOfertas } from '../api/ofertas'
import { clienteDeOrganizacion } from '../api/organizacion'
import type { ClienteListado, PuntoVentaListado, ResultadoDeResolucion } from '../api/tipos'
import { aLineaDeCarritoDesdeEscaneo, aLineasDeResolucion, calcularSubtotalPrevia, indexarResolucionPorArticulo, previaDeLinea } from '../api/ventas'
import { Box } from '../componentes/Box'

const CLAVE_PUNTO_VENTA = 'ways.pos.idPuntoVenta'

function leerPuntoVentaGuardado(): number | null {
  try {
    const crudo = localStorage.getItem(CLAVE_PUNTO_VENTA)
    return crudo ? Number(crudo) : null
  } catch {
    return null
  }
}

function guardarPuntoVentaSeleccionado(id: number) {
  try {
    localStorage.setItem(CLAVE_PUNTO_VENTA, String(id))
  } catch {
    // localStorage puede no estar disponible (modo privado del navegador) — la selección
    // simplemente no persiste entre sesiones, el resto de la pantalla sigue funcionando.
  }
}

function fusionarOpcionesCliente(opciones: ClienteListado[], seleccionado: ClienteListado | null): ClienteListado[] {
  if (!seleccionado) return opciones
  return opciones.some((c) => c.id === seleccionado.id) ? opciones : [seleccionado, ...opciones]
}

function etiquetaDeCliente(c: ClienteListado): string {
  const nombreCompleto = c.razonSocial ?? [c.nombre, c.apellido].filter(Boolean).join(' ')
  return `#${c.numero} — ${nombreCompleto}`
}

function formatearMoneda(valor: number): string {
  return valor.toLocaleString('es-AR', { minimumFractionDigits: 2, maximumFractionDigits: 2 })
}

/**
 * Pantalla del POS (stage-5-pos-ventas, Slice 6, design: POS Screen Composition) — escaneo +
 * carrito + selección de punto de venta/cliente. El checkout (pago, ticket, `POST
 * /api/ventas`) queda deshabilitado/stubbed a propósito: esa parte la wirea la Slice 7 sobre
 * este mismo archivo (Slice 4, el endpoint real, sigue en review). Precedente de forma:
 * `Articulos.tsx`/`Ofertas.tsx` tras sus rondas de judgment-day.
 */
export function Pos() {
  const [puntosVenta, setPuntosVenta] = useState<PuntoVentaListado[] | null>(null)
  const [idPuntoVenta, setIdPuntoVenta] = useState<number | ''>('')
  const [errorPuntosVenta, setErrorPuntosVenta] = useState('')

  const [opcionesClientes, setOpcionesClientes] = useState<ClienteListado[]>([])
  const [clienteSeleccionado, setClienteSeleccionado] = useState<ClienteListado | null>(null)
  const [terminoCliente, setTerminoCliente] = useState('')
  const [buscandoClientes, setBuscandoClientes] = useState(false)
  const [errorClientes, setErrorClientes] = useState('')
  const generacionClientesRef = useRef(0)

  const [lineas, setLineas] = useState<LineaCarrito[]>([])
  const [precios, setPrecios] = useState<Record<number, ResultadoDeResolucion>>({})
  const [resolviendo, setResolviendo] = useState(false)
  const [avisoPrecios, setAvisoPrecios] = useState('')
  const generacionResolucionRef = useRef(0)
  const ultimaAccionEsEdicionRef = useRef(false)
  const [cantidadesEnEdicion, setCantidadesEnEdicion] = useState<Record<number, string>>({})

  const [entradaEscaneo, setEntradaEscaneo] = useState('')
  const [escaneando, setEscaneando] = useState(false)
  const [errorEscaneo, setErrorEscaneo] = useState('')
  const tokenEscaneoRef = useRef(0)

  const puntoVentaSeleccionada = puntosVenta?.find((p) => p.id === idPuntoVenta) ?? null

  // Carga inicial: puntos de venta (para el selector explícito de la operación, proposal
  // decisión 3 — sin sesión de "punto de venta actual" en el servidor) y clientes (para
  // encontrar el Consumidor Final por defecto, spec: "Omitted idCliente defaults to Consumidor
  // Final"). Cada uno con su propio try/catch: que uno falle no bloquea al otro.
  useEffect(() => {
    let vigente = true

    clienteDeOrganizacion
      .listarPuntosVenta()
      .then((lista) => {
        if (!vigente) return
        setPuntosVenta(lista)
        const guardado = leerPuntoVentaGuardado()
        const porDefecto = lista.find((p) => p.id === guardado) ?? lista[0] ?? null
        setIdPuntoVenta(porDefecto ? porDefecto.id : '')
      })
      .catch((e) => {
        if (!vigente) return
        setPuntosVenta([])
        setErrorPuntosVenta(
          e instanceof ErrorApi ? e.message : 'No se pudieron cargar los puntos de venta. Seleccioná uno para operar.',
        )
      })

    const generacionClientes = (generacionClientesRef.current += 1)

    clienteDeClientes
      .listar('', false)
      .then((pagina) => {
        if (!vigente || generacionClientesRef.current !== generacionClientes) return
        setOpcionesClientes(pagina.items)
        const consumidorFinal = pagina.items.find((c) => c.esConsumidorFinal) ?? null
        setClienteSeleccionado(consumidorFinal)
      })
      .catch((e) => {
        if (!vigente || generacionClientesRef.current !== generacionClientes) return
        setErrorClientes(e instanceof ErrorApi ? e.message : 'No se pudieron cargar los clientes.')
      })

    return () => {
      vigente = false
    }
  }, [])

  // react-async-state regla 2/3/4: cada mutación del carrito (o un cambio de cliente/punto de
  // venta, de los que depende el lote) dispara una nueva resolución de precios; una respuesta
  // de una resolución anterior nunca puede pisar la de la más reciente (design: POS Screen
  // Composition, regla 2 — "generacionResolucionRef gates every /resolver response").
  useEffect(() => {
    const generacion = (generacionResolucionRef.current += 1)

    if (lineas.length === 0 || !clienteSeleccionado || !puntoVentaSeleccionada) {
      setPrecios({})
      setAvisoPrecios('')
      return
    }

    let vigente = true
    setResolviendo(true)
    setAvisoPrecios('')

    // Una edición de cantidad se debounce (el usuario suele seguir tipeando); un escaneo
    // dispara la resolución de inmediato, es una única mutación discreta.
    const demora = ultimaAccionEsEdicionRef.current ? 250 : 0
    const idTimeout = setTimeout(() => {
      clienteDeOfertas
        .resolver(aLineasDeResolucion(lineas, clienteSeleccionado.idListaPrecio, puntoVentaSeleccionada.idEmpresa))
        .then((resultados) => {
          if (!vigente || generacionResolucionRef.current !== generacion) return
          setPrecios(indexarResolucionPorArticulo(resultados))
        })
        .catch(() => {
          if (!vigente || generacionResolucionRef.current !== generacion) return
          setAvisoPrecios('No se pudo calcular la vista previa de precios. El total se confirma recién al cobrar.')
        })
        .finally(() => {
          if (!vigente || generacionResolucionRef.current !== generacion) return
          setResolviendo(false)
        })
    }, demora)

    return () => {
      vigente = false
      clearTimeout(idTimeout)
    }
  }, [lineas, clienteSeleccionado, puntoVentaSeleccionada])

  const mutarCarrito = useCallback((accion: AccionCarrito) => {
    ultimaAccionEsEdicionRef.current = accion.tipo === 'editarCantidad'
    setLineas((prev) => reducirCarrito(prev, accion))
  }, [])

  /** Texto crudo del input de cantidad de una línea: mientras el usuario está editando (input
   * en `cantidadesEnEdicion`) se muestra tal cual se tipeó, incluso si todavía no es un número
   * completo (ej. "1." antes del dígito decimal) — spec: no perder el punto decimal a mitad de
   * tipeo. */
  function textoCantidad(l: LineaCarrito): string {
    return cantidadesEnEdicion[l.idArticulo] ?? String(l.cantidad)
  }

  function cambiarCantidad(idArticulo: number, texto: string) {
    setCantidadesEnEdicion((prev) => ({ ...prev, [idArticulo]: texto }))
    const cantidad = Number(texto)
    if (texto.trim() !== '' && Number.isFinite(cantidad) && cantidad > 0) {
      mutarCarrito({ tipo: 'editarCantidad', idArticulo, cantidad })
    }
  }

  function confirmarCantidad(idArticulo: number) {
    setCantidadesEnEdicion((prev) => {
      const { [idArticulo]: _omitido, ...resto } = prev
      return resto
    })
  }

  async function escanear() {
    if (escaneando) return
    const entrada = entradaEscaneo.trim()
    if (!entrada) return

    const token = (tokenEscaneoRef.current += 1)
    setEscaneando(true)
    setErrorEscaneo('')
    try {
      const articulo = await clienteDeArticulos.escanear(entrada)
      if (tokenEscaneoRef.current !== token) return
      const { linea, cantidad } = aLineaDeCarritoDesdeEscaneo(articulo)
      mutarCarrito({ tipo: 'escanear', linea, cantidad })
      setEntradaEscaneo('')
    } catch (e) {
      if (tokenEscaneoRef.current !== token) return
      setErrorEscaneo(e instanceof ErrorApi ? e.message : 'No se pudo resolver el código escaneado.')
    } finally {
      if (tokenEscaneoRef.current === token) setEscaneando(false)
    }
  }

  async function buscarClientes() {
    if (buscandoClientes) return
    const generacion = (generacionClientesRef.current += 1)
    setBuscandoClientes(true)
    setErrorClientes('')
    try {
      const pagina = await clienteDeClientes.listar(terminoCliente, false)
      if (generacionClientesRef.current !== generacion) return
      setOpcionesClientes(pagina.items)
    } catch (e) {
      if (generacionClientesRef.current === generacion) {
        setErrorClientes(e instanceof ErrorApi ? e.message : 'No se pudieron buscar clientes.')
      }
    } finally {
      if (generacionClientesRef.current === generacion) setBuscandoClientes(false)
    }
  }

  function cambiarPuntoVenta(id: number) {
    setIdPuntoVenta(id)
    guardarPuntoVentaSeleccionado(id)
  }

  const subtotalPrevia = calcularSubtotalPrevia(lineas, precios)

  return (
    <div className="container-fluid py-4">
      <div className="row g-3">
        <div className="col-lg-8">
          <Box titulo="Carrito">
            {errorEscaneo && <div className="alert alert-danger rounded-0 py-1 px-2 small">{errorEscaneo}</div>}
            {avisoPrecios && <div className="alert alert-warning rounded-0 py-1 px-2 small">{avisoPrecios}</div>}

            <div className="input-group mb-3">
              <input
                type="text"
                className="form-control rounded-0"
                placeholder="Escanear o tipear un código (ej. 3*7790001234567)"
                aria-label="Código escaneado"
                value={entradaEscaneo}
                disabled={escaneando}
                onChange={(e) => setEntradaEscaneo(e.target.value)}
                onKeyDown={(e) => e.key === 'Enter' && (e.preventDefault(), escanear())}
                autoFocus
              />
              <button type="button" className="btn btn-primary rounded-0" disabled={escaneando} onClick={escanear}>
                {escaneando ? 'Buscando…' : 'Agregar'}
              </button>
            </div>

            <div className="table-responsive">
              <table className="table table-striped table-hover table-bordered align-middle">
                <thead>
                  <tr>
                    <th>Código</th>
                    <th>Artículo</th>
                    <th style={{ width: 110 }}>Cantidad</th>
                    <th className="text-end">Precio unit.</th>
                    <th className="text-end">Total</th>
                    <th className="text-end">Acciones</th>
                  </tr>
                </thead>
                <tbody>
                  {lineas.map((l) => {
                    const resultado = precios[l.idArticulo]
                    const previa = previaDeLinea(l, resultado)
                    const tieneDescuento = previa.descuentoUnitario > 0 && resultado?.precioOriginal != null
                    return (
                      <tr key={l.idArticulo}>
                        <td>{l.codigoBarra ?? l.codigoInterno}</td>
                        <td>{l.nombre}</td>
                        <td>
                          <input
                            type="number"
                            step="0.001"
                            min="0.001"
                            className="form-control form-control-sm rounded-0"
                            aria-label={`Cantidad de ${l.nombre}`}
                            value={textoCantidad(l)}
                            onChange={(e) => cambiarCantidad(l.idArticulo, e.target.value)}
                            onBlur={() => confirmarCantidad(l.idArticulo)}
                          />
                        </td>
                        <td className="text-end">
                          {previa.precioUnitario === null ? (
                            '—'
                          ) : (
                            <>
                              {tieneDescuento && (
                                <div className="text-decoration-line-through text-muted small">
                                  ${formatearMoneda(resultado.precioOriginal as number)}
                                </div>
                              )}
                              <div>
                                ${formatearMoneda(previa.precioUnitario)}
                                {tieneDescuento && resultado.aplicadas.length > 0 && (
                                  <span
                                    className="badge bg-success ms-1"
                                    title={resultado.aplicadas.map((a) => a.nombre).join(', ')}
                                  >
                                    {resultado.aplicadas[0].nombre}
                                  </span>
                                )}
                              </div>
                            </>
                          )}
                        </td>
                        <td className="text-end">{previa.total === null ? '—' : `$${formatearMoneda(previa.total)}`}</td>
                        <td className="text-end">
                          <button
                            type="button"
                            className="btn btn-sm btn-outline-danger rounded-0"
                            onClick={() => mutarCarrito({ tipo: 'quitarLinea', idArticulo: l.idArticulo })}
                          >
                            Quitar
                          </button>
                        </td>
                      </tr>
                    )
                  })}
                  {lineas.length === 0 && (
                    <tr>
                      <td colSpan={6} className="text-center text-muted py-4">
                        Escaneá o tipeá un código para empezar la venta.
                      </td>
                    </tr>
                  )}
                </tbody>
              </table>
            </div>

            {lineas.length > 0 && (
              <button type="button" className="btn btn-outline-secondary btn-sm rounded-0" onClick={() => mutarCarrito({ tipo: 'vaciar' })}>
                Vaciar carrito
              </button>
            )}
          </Box>
        </div>

        <div className="col-lg-4">
          <Box titulo="Datos de la venta">
            {errorPuntosVenta && <div className="alert alert-danger rounded-0 py-1 px-2 small">{errorPuntosVenta}</div>}

            <div className="mb-3">
              <label className="form-label" htmlFor="pos-punto-venta">
                Punto de venta
              </label>
              <select
                id="pos-punto-venta"
                className="form-select rounded-0"
                value={idPuntoVenta}
                disabled={puntosVenta === null}
                onChange={(e) => cambiarPuntoVenta(Number(e.target.value))}
              >
                {puntosVenta === null && <option value="">Cargando…</option>}
                {puntosVenta !== null && puntosVenta.length === 0 && <option value="">Sin puntos de venta disponibles</option>}
                {puntosVenta?.map((p) => (
                  <option key={p.id} value={p.id}>
                    {p.nombre}
                  </option>
                ))}
              </select>
            </div>

            {errorClientes && <div className="alert alert-danger rounded-0 py-1 px-2 small">{errorClientes}</div>}

            <div className="mb-2">
              <label className="form-label" htmlFor="pos-cliente">
                Cliente
              </label>
              <select
                id="pos-cliente"
                className="form-select rounded-0"
                value={clienteSeleccionado?.id ?? ''}
                onChange={(e) => {
                  const encontrado = fusionarOpcionesCliente(opcionesClientes, clienteSeleccionado).find(
                    (c) => c.id === Number(e.target.value),
                  )
                  setClienteSeleccionado(encontrado ?? null)
                }}
              >
                {fusionarOpcionesCliente(opcionesClientes, clienteSeleccionado).map((c) => (
                  <option key={c.id} value={c.id}>
                    {etiquetaDeCliente(c)}
                  </option>
                ))}
              </select>
            </div>

            <div className="input-group input-group-sm mb-3">
              <input
                type="search"
                className="form-control rounded-0"
                placeholder="Buscar otro cliente…"
                aria-label="Buscar cliente"
                value={terminoCliente}
                disabled={buscandoClientes}
                onChange={(e) => setTerminoCliente(e.target.value)}
                onKeyDown={(e) => e.key === 'Enter' && (e.preventDefault(), buscarClientes())}
              />
              <button type="button" className="btn btn-outline-primary rounded-0" disabled={buscandoClientes} onClick={buscarClientes}>
                {buscandoClientes ? 'Buscando…' : 'Buscar'}
              </button>
            </div>

            <hr />

            <div className="d-flex justify-content-between mb-3">
              <strong>Total previo</strong>
              <strong>
                {resolviendo ? 'Calculando…' : subtotalPrevia === null ? '—' : `$${formatearMoneda(subtotalPrevia)}`}
              </strong>
            </div>

            <button type="button" className="btn btn-success w-100 rounded-0" disabled>
              Cobrar (disponible en la próxima entrega)
            </button>
          </Box>
        </div>
      </div>
    </div>
  )
}

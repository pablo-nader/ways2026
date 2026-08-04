import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { clienteDeArticulos } from '../api/articulos'
import { reducirCarrito, type AccionCarrito, type LineaCarrito } from '../api/carrito'
import { clienteDeCatalogo } from '../api/catalogos'
import { api, ErrorApi } from '../api/cliente'
import { clienteDeClientes } from '../api/clientes'
import { clienteDeOfertas } from '../api/ofertas'
import { clienteDeOrganizacion } from '../api/organizacion'
import {
  aPagosDeVenta,
  calcularExcedente,
  calcularFaltante,
  filaPagoVacia,
  filasAPagosConVuelto,
  medioDisponibleParaCliente,
  validarPagosLocal,
  type FilaPago,
} from '../api/pagos'
import type {
  ClienteListado,
  ComprobanteEmitido,
  MedioPagoAlta,
  MedioPagoListado,
  ParametroResuelto,
  PuntoVentaListado,
  ResultadoDeResolucion,
} from '../api/tipos'
import { aLineaDeCarritoDesdeEscaneo, aLineasDeResolucion, aSolicitudDeVenta, calcularSubtotalPrevia, clienteDeVentas, indexarResolucionPorArticulo, previaDeLinea } from '../api/ventas'
import { Box } from '../componentes/Box'

const CLAVE_PUNTO_VENTA = 'ways.pos.idPuntoVenta'

/** Piso de cantidad por línea, compartido entre el guard de edición y los atributos
 * `min`/`step` del input — evita que ambos se desincronicen (ej. el guard aceptando
 * cantidades que el input ya no permite tipear). */
const CANTIDAD_MINIMA = 0.001

const clienteMediosPago = clienteDeCatalogo<MedioPagoListado, MedioPagoAlta>('medios-pago')

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

function formatearFechaHora(iso: string): string {
  return new Date(iso).toLocaleString('es-AR')
}

/** Etiqueta de un campo de una fila de pago: el nombre del medio solo no alcanza (dos filas
 * pueden compartir medio, ej. un split de efectivo) — se le suma `idFila` para que cada input
 * tenga un `aria-label` unívoco en pantalla. */
function etiquetaDeCampoFila(prefijo: string, medioDeFila: MedioPagoListado | null, idFila: number): string {
  return `${prefijo} de ${medioDeFila?.nombre ?? 'medio de pago'} (fila ${idFila})`
}

/**
 * Pantalla del POS (stage-5-pos-ventas, Slice 7, design: POS Screen Composition) — escaneo +
 * carrito + selección de punto de venta/cliente (Slice 6) + panel de pagos, checkout (`POST
 * /api/ventas`) y ticket (Slice 7, esta entrega). Precedente de forma: `Articulos.tsx`/`Ofertas.tsx`
 * tras sus rondas de judgment-day.
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

  const [medios, setMedios] = useState<MedioPagoListado[] | null>(null)
  const [errorMedios, setErrorMedios] = useState('')

  const [parametros, setParametros] = useState<{ toleranciaPago: number; vueltoMaximo: number } | null>(null)
  const [errorParametros, setErrorParametros] = useState('')
  const generacionParametrosRef = useRef(0)

  const proximaFilaPagoIdRef = useRef(1)
  const [filasPago, setFilasPago] = useState<FilaPago[]>(() => [filaPagoVacia(proximaFilaPagoIdRef.current++)])

  // react-async-state regla 9: mientras el checkout está en vuelo, TODO lo que podría
  // superponerse (escaneo, edición de carrito, cliente/punto de venta, filas de pago) queda
  // inerte — `cobrandoRef` es el guard de reentrancia de primera línea (un doble click en el
  // mismo tick le gana al re-render que deshabilita el botón), `cobrando` es lo que deshabilita
  // los controles en pantalla.
  const [cobrando, setCobrando] = useState(false)
  const cobrandoRef = useRef(false)
  const [errorCobro, setErrorCobro] = useState('')

  const [ventaEmitida, setVentaEmitida] = useState<{ comprobante: ComprobanteEmitido; cliente: ClienteListado } | null>(null)

  const puntoVentaSeleccionada = puntosVenta?.find((p) => p.id === idPuntoVenta) ?? null

  const medioPorId = useMemo(() => {
    const indice: Record<number, MedioPagoListado> = {}
    for (const m of medios ?? []) indice[m.id] = m
    return indice
  }, [medios])

  // Carga inicial: puntos de venta (para el selector explícito de la operación, proposal
  // decisión 3 — sin sesión de "punto de venta actual" en el servidor), clientes (para
  // encontrar el Consumidor Final por defecto, spec: "Omitted idCliente defaults to Consumidor
  // Final") y medios de pago (panel de pagos, Slice 7). Cada uno con su propio try/catch: que
  // uno falle no bloquea a los otros.
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

    clienteMediosPago
      .listar(false)
      .then((lista) => {
        if (!vigente) return
        setMedios(lista)
      })
      .catch((e) => {
        if (!vigente) return
        setMedios([])
        setErrorMedios(e instanceof ErrorApi ? e.message : 'No se pudieron cargar los medios de pago. No se puede cobrar.')
      })

    return () => {
      vigente = false
    }
  }, [])

  // react-async-state regla 2: cada cambio de punto de venta dispara la resolución de
  // tolerancia_pago/vuelto_maximo (ADR-13: punto de venta > empresa > default) — una respuesta
  // desactualizada nunca puede pisar la más reciente.
  useEffect(() => {
    if (!puntoVentaSeleccionada) {
      setParametros(null)
      setErrorParametros('')
      return
    }

    const generacion = (generacionParametrosRef.current += 1)
    let vigente = true

    Promise.all([
      api.get<ParametroResuelto>(
        `/parametros/tolerancia_pago?idEmpresa=${puntoVentaSeleccionada.idEmpresa}&idPuntoVenta=${puntoVentaSeleccionada.id}`,
      ),
      api.get<ParametroResuelto>(
        `/parametros/vuelto_maximo?idEmpresa=${puntoVentaSeleccionada.idEmpresa}&idPuntoVenta=${puntoVentaSeleccionada.id}`,
      ),
    ])
      .then(([tolerancia, vuelto]) => {
        if (!vigente || generacionParametrosRef.current !== generacion) return
        setParametros({ toleranciaPago: Number(tolerancia.valor), vueltoMaximo: Number(vuelto.valor) })
        setErrorParametros('')
      })
      .catch((e) => {
        if (!vigente || generacionParametrosRef.current !== generacion) return
        setParametros(null)
        setErrorParametros(
          e instanceof ErrorApi ? e.message : 'No se pudieron cargar los parámetros de pago. No se puede cobrar.',
        )
      })

    return () => {
      vigente = false
    }
  }, [puntoVentaSeleccionada])

  // react-async-state regla 2/3/4: cada mutación del carrito (o un cambio de cliente/punto de
  // venta, de los que depende el lote) dispara una nueva resolución de precios; una respuesta
  // de una resolución anterior nunca puede pisar la de la más reciente (design: POS Screen
  // Composition, regla 2 — "generacionResolucionRef gates every /resolver response").
  useEffect(() => {
    const generacion = (generacionResolucionRef.current += 1)

    // Una edición de cantidad se debounce (el usuario suele seguir tipeando); un escaneo
    // dispara la resolución de inmediato, es una única mutación discreta. La bandera se
    // consume acá mismo, ANTES del guard de precondiciones, para que se resetee en cada
    // corrida del efecto sin importar si esta corrida llega a resolver o no — así una edición
    // hecha mientras cliente/punto de venta todavía cargan no queda pendiente y termina
    // heredada por una corrida posterior no relacionada.
    const demora = ultimaAccionEsEdicionRef.current ? 250 : 0
    ultimaAccionEsEdicionRef.current = false

    if (lineas.length === 0 || !clienteSeleccionado || !puntoVentaSeleccionada) {
      setPrecios({})
      setAvisoPrecios('')
      // El bump de generación que hace `cobrar()` al terminar puede huerfanar una resolución
      // en vuelo (su `finally` se salta porque la generación ya no coincide): sin este reset,
      // esta corrida temprana (ej. tras vaciar el carrito post-cobro) deja `resolviendo` en
      // `true` para siempre.
      setResolviendo(false)
      return
    }

    let vigente = true
    setResolviendo(true)
    setAvisoPrecios('')

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

  // El Consumidor Final nunca puede pagar con cuenta corriente (spec: comprobantes-venta /
  // Cuenta Corriente Payment Gating) — si el cajero cambia de cliente a mitad de armar el pago
  // y ya había elegido un medio de cuenta corriente en alguna fila, esa fila queda sin medio en
  // vez de quedar en un estado que el servidor va a rechazar igual.
  useEffect(() => {
    if (!clienteSeleccionado?.esConsumidorFinal) return
    setFilasPago((prev) => {
      let cambio = false
      const siguiente = prev.map((f) => {
        if (f.idMedioPago === '') return f
        const medio = medioPorId[f.idMedioPago]
        if (medio?.comportamiento !== 'CuentaCorriente') return f
        cambio = true
        return { ...f, idMedioPago: '' as const, vueltoManual: '' }
      })
      return cambio ? siguiente : prev
    })
  }, [clienteSeleccionado, medioPorId])

  const mutarCarrito = useCallback((accion: AccionCarrito) => {
    if (cobrandoRef.current) return
    ultimaAccionEsEdicionRef.current = accion.tipo === 'editarCantidad'
    setLineas((prev) => reducirCarrito(prev, accion))

    // El mapa de ediciones en curso es un override por fila: si la fila desaparece (quitar,
    // vaciar) o su cantidad se recalcula por fuera de la edición manual (un escaneo que suma
    // sobre la línea existente), el override queda desactualizado y debe limpiarse acá mismo
    // — no puede depender de que el blur del input dispare antes que la próxima mutación.
    const limpiarFila = (idArticulo: number) =>
      setCantidadesEnEdicion((prev) => {
        if (!(idArticulo in prev)) return prev
        const { [idArticulo]: _omitido, ...resto } = prev
        return resto
      })

    switch (accion.tipo) {
      case 'quitarLinea':
        limpiarFila(accion.idArticulo)
        break
      case 'escanear':
        limpiarFila(accion.linea.idArticulo)
        break
      case 'vaciar':
        setCantidadesEnEdicion({})
        break
      case 'editarCantidad':
        break
      default: {
        const _exhaustivo: never = accion
        void _exhaustivo
      }
    }
  }, [])

  /** Texto crudo del input de cantidad de una línea: mientras el usuario está editando (input
   * en `cantidadesEnEdicion`) se muestra tal cual se tipeó, incluso si todavía no es un número
   * completo (ej. "1." antes del dígito decimal) — spec: no perder el punto decimal a mitad de
   * tipeo. */
  function textoCantidad(l: LineaCarrito): string {
    return cantidadesEnEdicion[l.idArticulo] ?? String(l.cantidad)
  }

  function cambiarCantidad(idArticulo: number, texto: string) {
    if (cobrandoRef.current) return
    setCantidadesEnEdicion((prev) => ({ ...prev, [idArticulo]: texto }))
    const cantidad = Number(texto)
    if (texto.trim() === '' || !Number.isFinite(cantidad) || cantidad < CANTIDAD_MINIMA) return

    // Un estado intermedio del input (ej. "1." tipeando hacia "1.5") puede parsear al mismo
    // valor ya comprometido en la línea (Number("1.") === 1) — despachar en ese caso dispara
    // una resolución de precios redundante. Solo se despacha cuando el valor parseado difiere
    // de la cantidad comprometida.
    const lineaActual = lineas.find((l) => l.idArticulo === idArticulo)
    if (lineaActual && lineaActual.cantidad === cantidad) return

    mutarCarrito({ tipo: 'editarCantidad', idArticulo, cantidad })
  }

  function confirmarCantidad(idArticulo: number) {
    if (cobrandoRef.current) return
    setCantidadesEnEdicion((prev) => {
      const { [idArticulo]: _omitido, ...resto } = prev
      return resto
    })
  }

  async function escanear() {
    if (escaneando || cobrandoRef.current) return
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
    if (buscandoClientes || cobrandoRef.current) return
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
    if (cobrandoRef.current) return
    setIdPuntoVenta(id)
    guardarPuntoVentaSeleccionado(id)
  }

  function cambiarCliente(id: number) {
    if (cobrandoRef.current) return
    const encontrado = fusionarOpcionesCliente(opcionesClientes, clienteSeleccionado).find((c) => c.id === id)
    setClienteSeleccionado(encontrado ?? null)
  }

  function agregarFilaPago() {
    if (cobrandoRef.current) return
    const id = proximaFilaPagoIdRef.current++
    setFilasPago((prev) => [...prev, filaPagoVacia(id)])
  }

  function quitarFilaPago(id: number) {
    if (cobrandoRef.current) return
    setFilasPago((prev) => prev.filter((f) => f.id !== id))
  }

  function cambiarMedioDeFila(id: number, idMedioPago: number | '') {
    if (cobrandoRef.current) return
    setFilasPago((prev) => prev.map((f) => (f.id === id ? { ...f, idMedioPago, vueltoManual: '' } : f)))
  }

  function cambiarImporteDeFila(id: number, importe: string) {
    if (cobrandoRef.current) return
    setFilasPago((prev) => prev.map((f) => (f.id === id ? { ...f, importe } : f)))
  }

  function cambiarReferenciaDeFila(id: number, referencia: string) {
    if (cobrandoRef.current) return
    setFilasPago((prev) => prev.map((f) => (f.id === id ? { ...f, referencia } : f)))
  }

  function cambiarVueltoDeFila(id: number, vueltoManual: string) {
    if (cobrandoRef.current) return
    setFilasPago((prev) => prev.map((f) => (f.id === id ? { ...f, vueltoManual } : f)))
  }

  function nuevaVenta() {
    if (cobrandoRef.current) return
    setVentaEmitida(null)
    setErrorCobro('')
  }

  const subtotalPrevia = calcularSubtotalPrevia(lineas, precios)
  const totalActual = subtotalPrevia ?? 0
  const pagosConVuelto = filasAPagosConVuelto(filasPago, medioPorId, totalActual)
  const faltante = calcularFaltante(totalActual, pagosConVuelto)
  const excedente = calcularExcedente(totalActual, pagosConVuelto)

  const rechazoLocal =
    subtotalPrevia === null || !clienteSeleccionado || !parametros
      ? null
      : validarPagosLocal({
          total: totalActual,
          pagos: pagosConVuelto,
          toleranciaPago: parametros.toleranciaPago,
          vueltoMaximo: parametros.vueltoMaximo,
          esConsumidorFinal: clienteSeleccionado.esConsumidorFinal,
          saldoCliente: clienteSeleccionado.saldo,
          limiteCredito: clienteSeleccionado.limiteCredito,
          creditoIlimitado: clienteSeleccionado.creditoIlimitado,
        })

  // react-async-state regla 7: si medios de pago o parámetros no cargaron, "Cobrar" queda
  // efectivamente deshabilitado — no solo un aviso decorativo.
  const precondicionesListas =
    lineas.length > 0 &&
    clienteSeleccionado !== null &&
    puntoVentaSeleccionada !== null &&
    medios !== null &&
    errorMedios === '' &&
    parametros !== null &&
    errorParametros === '' &&
    subtotalPrevia !== null &&
    !resolviendo

  const puedeCobrar = precondicionesListas && !cobrando && rechazoLocal === null

  async function cobrar() {
    // react-async-state regla 9: guard de reentrancia de primera línea — un doble click en el
    // mismo tick le gana al re-render que deshabilita el botón.
    if (cobrandoRef.current) return
    if (!puedeCobrar || !clienteSeleccionado || !puntoVentaSeleccionada) return

    const miGeneracion = (generacionResolucionRef.current += 1)
    cobrandoRef.current = true
    setCobrando(true)
    setErrorCobro('')

    try {
      const solicitud = aSolicitudDeVenta({
        idPuntoVenta: puntoVentaSeleccionada.id,
        idCliente: clienteSeleccionado.id,
        codigoTipoComprobante: 'TX',
        idComprobanteAsociado: null,
        lineas,
        pagos: aPagosDeVenta(pagosConVuelto),
        direccionEntrega: null,
        observaciones: null,
      })

      const emitido = await clienteDeVentas.emitir(solicitud)
      if (generacionResolucionRef.current !== miGeneracion) return

      setVentaEmitida({ comprobante: emitido, cliente: clienteSeleccionado })
      setLineas([])
      setPrecios({})
      setCantidadesEnEdicion({})
      setFilasPago([filaPagoVacia(proximaFilaPagoIdRef.current++)])
    } catch (e) {
      if (generacionResolucionRef.current !== miGeneracion) return
      setErrorCobro(e instanceof ErrorApi ? e.message : 'No se pudo registrar la venta.')
    } finally {
      if (generacionResolucionRef.current === miGeneracion) {
        cobrandoRef.current = false
        setCobrando(false)
      }
    }
  }

  if (ventaEmitida) {
    const { comprobante, cliente } = ventaEmitida
    return (
      <div className="container-fluid py-4" key={comprobante.id}>
        <div className="row g-3">
          <div className="col-12">
            <Box titulo={`Venta ${comprobante.numeroVisible}`} variante="success">
              <p className="text-muted mb-3">
                {formatearFechaHora(comprobante.fecha)} — {etiquetaDeCliente(cliente)}
              </p>

              <div className="table-responsive">
                <table className="table table-striped table-bordered align-middle">
                  <thead>
                    <tr>
                      <th>Artículo</th>
                      <th style={{ width: 100 }}>Cantidad</th>
                      <th className="text-end">Precio unit.</th>
                      <th className="text-end">Descuento</th>
                      <th className="text-end">Total</th>
                    </tr>
                  </thead>
                  <tbody>
                    {comprobante.items.map((item) => (
                      <tr key={item.orden}>
                        <td>{item.descripcion}</td>
                        <td>{item.cantidad}</td>
                        <td className="text-end">${formatearMoneda(item.precioUnitario)}</td>
                        <td className="text-end">
                          {item.descuento > 0 ? (
                            <span className="badge bg-success">-${formatearMoneda(item.descuento)}</span>
                          ) : (
                            '—'
                          )}
                        </td>
                        <td className="text-end">${formatearMoneda(item.total)}</td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>

              <div className="row g-3">
                <div className="col-md-6">
                  <h6>Pagos</h6>
                  <ul className="list-unstyled mb-0">
                    {comprobante.pagos.map((pago, indice) => (
                      <li key={indice}>
                        {medioPorId[pago.idMedioPago]?.nombre ?? `Medio #${pago.idMedioPago}`}: $
                        {formatearMoneda(pago.importe)}
                        {pago.referencia && ` (ref. ${pago.referencia})`}
                        {pago.vuelto > 0 && ` — vuelto $${formatearMoneda(pago.vuelto)}`}
                      </li>
                    ))}
                  </ul>
                </div>
                <div className="col-md-6 text-md-end">
                  <div>Subtotal: ${formatearMoneda(comprobante.subtotal)}</div>
                  <div>Descuento: ${formatearMoneda(comprobante.descuentoTotal)}</div>
                  <div className="fs-5">
                    <strong>Total: ${formatearMoneda(comprobante.total)}</strong>
                  </div>
                </div>
              </div>

              <button type="button" className="btn btn-primary mt-4 rounded-0" onClick={nuevaVenta}>
                Nueva venta
              </button>
            </Box>
          </div>
        </div>
      </div>
    )
  }

  return (
    <div className="container-fluid py-4" key="venta-en-curso">
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
                disabled={escaneando || cobrando}
                onChange={(e) => setEntradaEscaneo(e.target.value)}
                onKeyDown={(e) => e.key === 'Enter' && (e.preventDefault(), escanear())}
                autoFocus
              />
              <button type="button" className="btn btn-primary rounded-0" disabled={escaneando || cobrando} onClick={escanear}>
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
                            step={CANTIDAD_MINIMA}
                            min={CANTIDAD_MINIMA}
                            className="form-control form-control-sm rounded-0"
                            aria-label={`Cantidad de ${l.nombre}`}
                            value={textoCantidad(l)}
                            disabled={cobrando}
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
                                    {resultado.aplicadas.length === 1
                                      ? resultado.aplicadas[0].nombre
                                      : `${resultado.aplicadas[0].nombre} +${resultado.aplicadas.length - 1}`}
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
                            disabled={cobrando}
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
              <button
                type="button"
                className="btn btn-outline-secondary btn-sm rounded-0"
                disabled={cobrando}
                onClick={() => mutarCarrito({ tipo: 'vaciar' })}
              >
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
                disabled={puntosVenta === null || cobrando}
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
                disabled={cobrando}
                onChange={(e) => cambiarCliente(Number(e.target.value))}
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
                disabled={buscandoClientes || cobrando}
                onChange={(e) => setTerminoCliente(e.target.value)}
                onKeyDown={(e) => e.key === 'Enter' && (e.preventDefault(), buscarClientes())}
              />
              <button
                type="button"
                className="btn btn-outline-primary rounded-0"
                disabled={buscandoClientes || cobrando}
                onClick={buscarClientes}
              >
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

            {errorMedios && <div className="alert alert-danger rounded-0 py-1 px-2 small">{errorMedios}</div>}
            {errorParametros && <div className="alert alert-danger rounded-0 py-1 px-2 small">{errorParametros}</div>}
            {errorCobro && <div className="alert alert-danger rounded-0 py-1 px-2 small">{errorCobro}</div>}

            <h6>Pagos</h6>
            {filasPago.map((fila) => {
              const medioDeFila = fila.idMedioPago === '' ? null : (medioPorId[fila.idMedioPago] ?? null)
              const pagoDeFila = pagosConVuelto.find((p) => p.idFila === fila.id) ?? null
              const vueltoMostrado = fila.vueltoManual !== '' ? fila.vueltoManual : String(pagoDeFila?.vuelto ?? 0)

              return (
                <div className="row g-2 mb-2 align-items-center" key={fila.id}>
                  <div className="col-4">
                    <select
                      className="form-select form-select-sm rounded-0"
                      aria-label="Medio de pago"
                      value={fila.idMedioPago}
                      disabled={cobrando || medios === null}
                      onChange={(e) => cambiarMedioDeFila(fila.id, e.target.value === '' ? '' : Number(e.target.value))}
                    >
                      <option value="">Elegir medio…</option>
                      {(medios ?? [])
                        .filter((m) => medioDisponibleParaCliente(m, clienteSeleccionado?.esConsumidorFinal ?? false))
                        .map((m) => (
                          <option key={m.id} value={m.id}>
                            {m.nombre}
                          </option>
                        ))}
                    </select>
                  </div>
                  <div className="col-3">
                    <input
                      type="number"
                      step="0.01"
                      min="0"
                      className="form-control form-control-sm rounded-0"
                      aria-label={etiquetaDeCampoFila('Importe', medioDeFila, fila.id)}
                      value={fila.importe}
                      disabled={cobrando}
                      onChange={(e) => cambiarImporteDeFila(fila.id, e.target.value)}
                    />
                  </div>
                  <div className="col-3">
                    <input
                      type="text"
                      className="form-control form-control-sm rounded-0"
                      aria-label={etiquetaDeCampoFila('Referencia', medioDeFila, fila.id)}
                      placeholder={medioDeFila?.requiereReferencia ? 'Referencia (requerida)' : 'Referencia'}
                      value={fila.referencia}
                      disabled={cobrando || !medioDeFila?.requiereReferencia}
                      onChange={(e) => cambiarReferenciaDeFila(fila.id, e.target.value)}
                    />
                  </div>
                  <div className="col-2 d-flex align-items-center gap-1">
                    <input
                      type="number"
                      step="0.01"
                      min="0"
                      className="form-control form-control-sm rounded-0"
                      aria-label={etiquetaDeCampoFila('Vuelto', medioDeFila, fila.id)}
                      value={vueltoMostrado}
                      disabled={cobrando || !medioDeFila?.admiteVuelto}
                      onChange={(e) => cambiarVueltoDeFila(fila.id, e.target.value)}
                    />
                    {filasPago.length > 1 && (
                      <button
                        type="button"
                        className="btn btn-sm btn-outline-danger rounded-0"
                        disabled={cobrando}
                        aria-label="Quitar medio de pago"
                        onClick={() => quitarFilaPago(fila.id)}
                      >
                        ×
                      </button>
                    )}
                  </div>
                </div>
              )
            })}

            <button
              type="button"
              className="btn btn-outline-secondary btn-sm rounded-0 mb-3"
              disabled={cobrando}
              onClick={agregarFilaPago}
            >
              + Agregar medio de pago
            </button>

            <div className="d-flex justify-content-between small">
              <span>Falta</span>
              <span>${formatearMoneda(faltante)}</span>
            </div>
            <div className="d-flex justify-content-between small mb-2">
              <span>Vuelto</span>
              <span>${formatearMoneda(excedente)}</span>
            </div>

            {rechazoLocal && <div className="alert alert-warning rounded-0 py-1 px-2 small">{rechazoLocal.mensaje}</div>}

            <button type="button" className="btn btn-success w-100 rounded-0" disabled={!puedeCobrar} onClick={cobrar}>
              {cobrando ? 'Cobrando…' : 'Cobrar'}
            </button>
          </Box>
        </div>
      </div>
    </div>
  )
}

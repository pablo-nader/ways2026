import { useEffect, useRef, useState } from 'react'
import { clienteDeArticulos } from '../api/articulos'
import { ErrorApi } from '../api/cliente'
import { clienteDeClientes } from '../api/clientes'
import { clienteDeOfertas } from '../api/ofertas'
import { clienteDeOrganizacion } from '../api/organizacion'
import type { ArticuloEscaneado, ListaPrecioAsignable, PuntoVentaListado, ResultadoDeResolucion } from '../api/tipos'

const CLAVE_PUNTO_VENTA = 'ways.consultaPrecios.idPuntoVenta'
const CLAVE_LISTA_PRECIO = 'ways.consultaPrecios.idListaPrecio'

/**
 * Ventana de inactividad antes de volver a la pantalla idle (design decisión 14, mutation
 * targets 35/36) — exportada para que el test consuma la MISMA constante que el componente en
 * vez de un número que puede divergir sin que ningún test lo note.
 */
export const MS_DE_RESET = 20_000

function leerNumeroGuardado(clave: string): number | null {
  try {
    const crudo = localStorage.getItem(clave)
    return crudo ? Number(crudo) : null
  } catch {
    return null
  }
}

function guardarNumeroSeleccionado(clave: string, id: number) {
  try {
    localStorage.setItem(clave, String(id))
  } catch {
    // localStorage puede no estar disponible (modo privado del navegador) — la selección
    // simplemente no persiste entre sesiones, el resto de la pantalla sigue funcionando.
  }
}

function formatearMoneda(valor: number): string {
  const signo = valor < 0 ? '-' : ''
  return `${signo}$${Math.abs(valor).toLocaleString('es-AR', { minimumFractionDigits: 2, maximumFractionDigits: 2 })}`
}

/**
 * Estado de pantalla derivado de la resolución — nunca un tercer estado indefinido entre
 * "no encontrado" y "resuelto" (design decisión 6 restated para la consulta de un solo artículo,
 * consulta-de-precios/spec.md:57-72). `$0` no es un valor representable: `sin_precio` es su propio
 * caso, no un `resuelto` con precio cero.
 */
export type ResultadoDeConsulta =
  | { tipo: 'no_encontrado' }
  | { tipo: 'sin_precio'; nombre: string; codigoInterno: string }
  | { tipo: 'resuelto'; nombre: string; codigoInterno: string; precioOriginal: number; precioFinal: number; conOferta: boolean }

/**
 * `ArticuloEscaneado` + `ResultadoDeResolucion` → el estado de pantalla, pura (web-descriptor-tests).
 * `PrecioOriginal`/`PrecioFinal` nulos ⇒ `sin_precio` (nunca `$0`, mutation target 37). El tachado
 * es `aplicadas.length > 0`, el mismo criterio que `HojaDeEtiquetas` (nunca
 * `precioOriginal !== precioFinal`, que producción jamás emite en ese par).
 */
export function aResultadoDeConsulta(articulo: ArticuloEscaneado, resolucion: ResultadoDeResolucion): ResultadoDeConsulta {
  if (resolucion.precioOriginal === null || resolucion.precioFinal === null) {
    return { tipo: 'sin_precio', nombre: articulo.nombre, codigoInterno: articulo.codigoInterno }
  }

  return {
    tipo: 'resuelto',
    nombre: articulo.nombre,
    codigoInterno: articulo.codigoInterno,
    precioOriginal: resolucion.precioOriginal,
    precioFinal: resolucion.precioFinal,
    conOferta: resolucion.aplicadas.length > 0,
  }
}

/**
 * Consulta de precios del salón (stage-18-etiquetas-y-consulta, Slice 4, design decisión 11):
 * `autoFocus` + `Enter` (`Pos.tsx:1068-1078`), exactamente DOS llamadas por escaneo
 * (`GET /api/articulos/escaneo` → identidad, `POST /api/ofertas/resolver` @ `cantidad = 1` →
 * precio), CERO escrituras, CERO estado persistido más allá de los selectores de PV/lista (mismo
 * criterio que `Pos.tsx`). La pantalla no lee ningún claim de rol ni muestra identidad de usuario
 * (OD2) — el gate de acceso vive enteramente en la ruta (`App.tsx`).
 */
export function ConsultaPrecios() {
  const [puntosVenta, setPuntosVenta] = useState<PuntoVentaListado[] | null>(null)
  const [idPuntoVenta, setIdPuntoVenta] = useState<number | ''>('')
  const [errorPuntosVenta, setErrorPuntosVenta] = useState('')

  const [listas, setListas] = useState<ListaPrecioAsignable[] | null>(null)
  const [idListaPrecio, setIdListaPrecio] = useState<number | ''>('')
  const [errorListas, setErrorListas] = useState('')

  const [entrada, setEntrada] = useState('')
  const [buscando, setBuscando] = useState(false)
  const [error, setError] = useState('')
  const [resultado, setResultado] = useState<ResultadoDeConsulta | null>(null)

  // react-async-state: un contador de generación, nunca un id de timer en un ref crudo — un
  // segundo escaneo (o el propio reset) cancela ESTRUCTURALMENTE cualquier resolución en vuelo de
  // la corrida anterior (mutation target 37).
  const generacionRef = useRef(0)
  const inputRef = useRef<HTMLInputElement | null>(null)

  useEffect(() => {
    let vigente = true

    clienteDeOrganizacion
      .listarPuntosVenta()
      .then((lista) => {
        if (!vigente) return
        setPuntosVenta(lista)
        const guardado = leerNumeroGuardado(CLAVE_PUNTO_VENTA)
        const porDefecto = lista.find((p) => p.id === guardado) ?? lista[0] ?? null
        setIdPuntoVenta(porDefecto ? porDefecto.id : '')
      })
      .catch(() => {
        if (!vigente) return
        setPuntosVenta([])
        setErrorPuntosVenta('No se pudieron cargar los puntos de venta.')
      })

    clienteDeClientes
      .listasDePrecioAsignables()
      .then((lista) => {
        if (!vigente) return
        setListas(lista)
        const guardado = leerNumeroGuardado(CLAVE_LISTA_PRECIO)
        const porDefecto = lista.find((l) => l.id === guardado) ?? lista.find((l) => l.esDefault) ?? lista[0] ?? null
        setIdListaPrecio(porDefecto ? porDefecto.id : '')
      })
      .catch(() => {
        if (!vigente) return
        setListas([])
        setErrorListas('No se pudieron cargar las listas de precio.')
      })

    return () => {
      vigente = false
    }
  }, [])

  // Idle-reset (design decisión 14): efecto keyed en LA RESOLUCIÓN (`resultado`), `setTimeout`
  // DENTRO del efecto, `clearTimeout` en el cleanup — el mismo patrón de `CompraEditor.tsx:90-110`/
  // `Existencias.tsx:58-79`. Un escaneo nuevo que resuelve ANTES de los ~20s reemplaza `resultado`
  // y React limpia el temporizador viejo estructuralmente (mutation target 35 — nunca hace falta
  // cancelarlo a mano). Si en cambio el reset dispara ANTES de que una corrida lenta termine, la
  // bomba de generación acá es lo que evita que esa resolución tardía repinte el precio del
  // cliente ya despedido (mutation target 37) — sin la bomba, una resolución vieja que llega
  // después del reset pasaría el guard de generación por coincidencia.
  useEffect(() => {
    if (!resultado) return

    const temporizador = setTimeout(() => {
      generacionRef.current += 1
      setResultado(null)
      inputRef.current?.focus()
    }, MS_DE_RESET)

    return () => clearTimeout(temporizador)
  }, [resultado])

  function cambiarPuntoVenta(id: number) {
    setIdPuntoVenta(id)
    guardarNumeroSeleccionado(CLAVE_PUNTO_VENTA, id)
  }

  function cambiarListaPrecio(id: number) {
    setIdListaPrecio(id)
    guardarNumeroSeleccionado(CLAVE_LISTA_PRECIO, id)
  }

  const puntoVentaSeleccionado = puntosVenta?.find((p) => p.id === idPuntoVenta) ?? null
  const selectoresListos = puntoVentaSeleccionado !== null && idListaPrecio !== ''

  async function consultar() {
    // Primera línea de reentrancia (react-async-state regla 9): un doble Enter/click en el mismo
    // tick no dispara una segunda corrida.
    if (buscando) return
    const codigo = entrada.trim()
    if (!codigo || !selectoresListos) return

    // `resultado` NO se limpia acá: el efecto de reset está keyed en la RESOLUCIÓN (design
    // decisión 14), no en el intento de escaneo — el resultado anterior sigue en pantalla hasta
    // que el nuevo llega (o hasta que el propio reset lo apaga). Si esta corrida es lenta y el
    // reset dispara mientras tanto, la bomba de generación de ese reset es lo que evita que esta
    // respuesta tardía repinte el precio del cliente ya despedido (mutation target 37).
    const generacion = (generacionRef.current += 1)
    setBuscando(true)
    setError('')

    try {
      const articulo = await clienteDeArticulos.escanear(codigo)
      if (generacionRef.current !== generacion) return

      const idEmpresa = puntoVentaSeleccionado.idEmpresa
      const [resolucion] = await clienteDeOfertas.resolver([{ idArticulo: articulo.idArticulo, idEmpresa, idListaPrecio, cantidad: 1 }])
      if (generacionRef.current !== generacion) return

      setResultado(aResultadoDeConsulta(articulo, resolucion))
      setEntrada('')
    } catch (e) {
      if (generacionRef.current !== generacion) return

      if (e instanceof ErrorApi && e.estado === 404) {
        setResultado({ tipo: 'no_encontrado' })
        setEntrada('')
      } else {
        setError(e instanceof ErrorApi ? e.message : 'No se pudo resolver el código escaneado.')
      }
    } finally {
      if (generacionRef.current === generacion) {
        setBuscando(false)
        inputRef.current?.focus()
      }
    }
  }

  return (
    <div className="container py-4">
      <h1 className="h3 mb-4">Consulta de precios</h1>

      <div className="row g-3 mb-4">
        <div className="col-12 col-md-6">
          <label className="form-label" htmlFor="consulta-precios-punto-venta">
            Punto de venta
          </label>
          <select
            id="consulta-precios-punto-venta"
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
          {errorPuntosVenta && <div className="form-text text-danger">{errorPuntosVenta}</div>}
        </div>

        <div className="col-12 col-md-6">
          <label className="form-label" htmlFor="consulta-precios-lista">
            Lista de precio
          </label>
          <select
            id="consulta-precios-lista"
            className="form-select rounded-0"
            value={idListaPrecio}
            disabled={listas === null}
            onChange={(e) => cambiarListaPrecio(Number(e.target.value))}
          >
            {listas === null && <option value="">Cargando…</option>}
            {listas !== null && listas.length === 0 && <option value="">Sin listas de precio disponibles</option>}
            {listas?.map((l) => (
              <option key={l.id} value={l.id}>
                {l.nombre}
              </option>
            ))}
          </select>
          {errorListas && <div className="form-text text-danger">{errorListas}</div>}
        </div>
      </div>

      <div className="input-group input-group-lg mb-4">
        <input
          ref={inputRef}
          type="text"
          className="form-control rounded-0"
          placeholder="Escaneá un código de barras…"
          aria-label="Código escaneado"
          value={entrada}
          disabled={buscando || !selectoresListos}
          onChange={(e) => setEntrada(e.target.value)}
          onKeyDown={(e) => e.key === 'Enter' && (e.preventDefault(), consultar())}
          autoFocus
        />
        <button type="button" className="btn btn-primary rounded-0" disabled={buscando || !selectoresListos} onClick={consultar}>
          {buscando ? 'Buscando…' : 'Consultar'}
        </button>
      </div>

      {error && (
        <div className="alert alert-danger rounded-0" role="alert">
          {error}
        </div>
      )}

      {resultado?.tipo === 'no_encontrado' && (
        <div className="text-center py-5" data-testid="resultado-no-encontrado">
          <p className="display-5 text-muted mb-0">No encontrado</p>
        </div>
      )}

      {resultado?.tipo === 'sin_precio' && (
        <div className="text-center py-5" data-testid="resultado-sin-precio">
          <p className="fs-3 mb-2">{resultado.nombre}</p>
          <p className="text-muted mb-3">{resultado.codigoInterno}</p>
          <p className="display-5 text-muted mb-0">Consultá en caja</p>
        </div>
      )}

      {resultado?.tipo === 'resuelto' && (
        <div className="text-center py-5" data-testid="resultado-resuelto">
          <p className="fs-3 mb-2">{resultado.nombre}</p>
          <p className="text-muted mb-3">{resultado.codigoInterno}</p>
          {resultado.conOferta && (
            <p className="fs-3 text-muted mb-1" style={{ textDecoration: 'line-through' }} data-testid="precio-original-tachado">
              {formatearMoneda(resultado.precioOriginal)}
            </p>
          )}
          <p className="display-1 fw-bold mb-0" data-testid="precio-final">
            {formatearMoneda(resultado.precioFinal)}
          </p>
        </div>
      )}
    </div>
  )
}

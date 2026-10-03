import { useEffect, useId, useRef, useState } from 'react'
import { Link, Outlet, useLocation, useNavigate } from 'react-router'
import type { Location } from 'react-router'
import { puedeOperarPos } from '../api/tipos'
import type { PuntoVentaListado, UsuarioAutenticado } from '../api/tipos'
import { useAuth } from '../auth/useAuth'
import { ProveedorDeBorradoresDeTicket } from '../pos/BorradorDeTicketContext'
import { colorDePuntoVenta } from '../puntoVenta/colorDePuntoVenta'
import { usePuntoVenta } from '../puntoVenta/usePuntoVenta'
import { BotonDeTema } from '../tema/BotonDeTema'
import { MenuDesplegable } from './MenuDesplegable'
import { construirMenu, esRutaActiva } from './menu'

const RUTA_DE_CAMBIO_DE_PUNTO_VENTA = '/punto-de-venta'

// El botón principal anuncia la ruta actual solo con `aria-current`: la clase `active` de
// Bootstrap lo pintaría como "presionado".
function claseDeEnlace(principal: boolean | undefined, activo: boolean) {
  if (principal) return 'btn btn-success fw-bold me-2'
  return activo ? 'nav-link active' : 'nav-link'
}

type PropsDeInsignia = {
  usuario: UsuarioAutenticado
  puntoVenta: PuntoVentaListado | null
  puntosVenta: PuntoVentaListado[]
  ubicacion: Location
}

function InsigniaDePuntoVenta({ usuario, puntoVenta, puntosVenta, ubicacion }: PropsDeInsignia) {
  if (!puedeOperarPos(usuario.rolId)) return null

  if (!puntoVenta) {
    return <span className="text-warning small">Sin punto de venta</span>
  }

  if (puntosVenta.length <= 1) {
    return <span className="text-body small">Punto de venta: {puntoVenta.nombre}</span>
  }

  if (ubicacion.pathname === RUTA_DE_CAMBIO_DE_PUNTO_VENTA) {
    return (
      <span className="text-body small" aria-current="page">
        Punto de venta: {puntoVenta.nombre}
      </span>
    )
  }

  return (
    <Link
      to={RUTA_DE_CAMBIO_DE_PUNTO_VENTA}
      state={{ desde: ubicacion }}
      className="btn btn-outline-secondary btn-sm"
      aria-label={`Punto de venta ${puntoVenta.nombre}, cambiar`}
    >
      {puntoVenta.nombre}
    </Link>
  )
}

export function Layout() {
  const { usuario, cerrarSesion } = useAuth()
  const { puntoVenta, puntosVenta } = usePuntoVenta()
  const navegar = useNavigate()
  const ubicacion = useLocation()
  const { pathname } = ubicacion
  const idColapso = useId()
  const [colapsoAbierto, setColapsoAbierto] = useState(false)
  const [grupoAbierto, setGrupoAbierto] = useState<string | null>(null)
  const saliendoRef = useRef(false)

  function dejarBarraEnReposo() {
    setGrupoAbierto(null)
    setColapsoAbierto(false)
  }

  // Cualquier navegación deja la barra en reposo: sin grupo desplegado ni menú móvil abierto.
  useEffect(() => {
    dejarBarraEnReposo()
  }, [pathname])

  async function salir() {
    if (saliendoRef.current) return
    saliendoRef.current = true
    try {
      await cerrarSesion()
      navegar('/login', { replace: true })
    } finally {
      saliendoRef.current = false
    }
  }

  const menu = usuario ? construirMenu(usuario) : []

  return (
    <div id="wrap">
      <div id="top">
        <nav className={`ways-nav ${colorDePuntoVenta(puntoVenta, puntosVenta)}`} />

        <nav className="navbar navbar-expand-lg bg-body border-top-0 ways-navbar">
          <div className="container">
            <Link className="navbar-brand ways-brand" to="/" aria-label="Ways, ir al inicio">
              Ways
            </Link>

            <button
              type="button"
              className="navbar-toggler"
              aria-expanded={colapsoAbierto}
              aria-controls={idColapso}
              aria-label="Abrir menú"
              onClick={() => setColapsoAbierto((previo) => !previo)}
            >
              <span className="navbar-toggler-icon" />
            </button>

            <div
              id={idColapso}
              className={colapsoAbierto ? 'collapse navbar-collapse show' : 'collapse navbar-collapse'}
            >
              <ul className="navbar-nav">
                {menu.map((entrada) => {
                  const activo = esRutaActiva(pathname, entrada)

                  if (entrada.tipo === 'grupo') {
                    return (
                      <MenuDesplegable
                        key={entrada.etiqueta}
                        grupo={entrada}
                        abierto={grupoAbierto === entrada.etiqueta}
                        activo={activo}
                        alAlternar={() =>
                          setGrupoAbierto((previo) => (previo === entrada.etiqueta ? null : entrada.etiqueta))
                        }
                        alCerrar={() => setGrupoAbierto((previo) => (previo === entrada.etiqueta ? null : previo))}
                      />
                    )
                  }

                  return (
                    <li className="nav-item" key={entrada.a}>
                      {/* Elegir la ruta ya activa no cambia `pathname`, así que el efecto no
                          alcanza para cerrar la barra: lo hace el propio clic. */}
                      <Link
                        to={entrada.a}
                        className={claseDeEnlace(entrada.principal, activo)}
                        aria-current={activo ? 'page' : undefined}
                        onClick={dejarBarraEnReposo}
                      >
                        {entrada.etiqueta}
                      </Link>
                    </li>
                  )
                })}
              </ul>
            </div>

            <div className="d-flex align-items-center gap-3">
              <span className="text-body small">
                {usuario?.usuario} <span className="text-body-secondary">· {usuario?.rol}</span>
              </span>
              {usuario && (
                <InsigniaDePuntoVenta
                  usuario={usuario}
                  puntoVenta={puntoVenta}
                  puntosVenta={puntosVenta}
                  ubicacion={ubicacion}
                />
              )}
              <BotonDeTema />
              <button
                type="button"
                className="btn btn-outline-secondary"
                title="Salir"
                onClick={salir}
              >
                Salir
              </button>
            </div>
          </div>
        </nav>
      </div>

      <div id="content">
        <div className="outer">
          <div className="inner">
            {/* stage-pos-adjustments: `Layout` es la instancia estable que React Router mantiene
                montada al navegar entre rutas hermanas (`/pos` → `/caja` → `/pos`) — el `Provider`
                acá adentro sobrevive a esa navegación, así que el borrador del ticket en curso
                también. */}
            <ProveedorDeBorradoresDeTicket>
              <Outlet />
            </ProveedorDeBorradoresDeTicket>
          </div>
        </div>
      </div>
    </div>
  )
}

import { useEffect, useId, useRef, useState } from 'react'
import { Link, Outlet, useLocation, useNavigate } from 'react-router'
import { useAuth } from '../auth/useAuth'
import { MenuDesplegable } from './MenuDesplegable'
import { construirMenu, esRutaActiva } from './menu'

function claseDeEnlace(principal: boolean | undefined, activo: boolean) {
  const base = principal ? 'btn btn-success rounded-0 fw-bold me-2' : 'nav-link'
  return activo ? `${base} active` : base
}

export function Layout() {
  const { usuario, cerrarSesion } = useAuth()
  const navegar = useNavigate()
  const { pathname } = useLocation()
  const idColapso = useId()
  const [colapsoAbierto, setColapsoAbierto] = useState(false)
  const [grupoAbierto, setGrupoAbierto] = useState<string | null>(null)
  const saliendoRef = useRef(false)

  // Cualquier navegación deja la barra en reposo: sin grupo desplegado ni menú móvil abierto.
  useEffect(() => {
    setGrupoAbierto(null)
    setColapsoAbierto(false)
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
    <div id="wrap" className="bg-dark dk">
      <div id="top">
        {/* Franja de color del punto de venta. Hoy es fija; cuando exista la tabla
            de puntos de venta vuelve a pintar según el local activo. */}
        <nav className="ways-nav color_1" />

        <nav className="navbar navbar-dark navbar-expand-lg bg-dark border-top-0">
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
                      <Link
                        to={entrada.a}
                        className={claseDeEnlace(entrada.principal, activo)}
                        aria-current={activo ? 'page' : undefined}
                      >
                        {entrada.etiqueta}
                      </Link>
                    </li>
                  )
                })}
              </ul>
            </div>

            <div className="d-flex align-items-center gap-3">
              <span className="text-light small">
                {usuario?.usuario} <span className="text-secondary">· {usuario?.rol}</span>
              </span>
              <button
                type="button"
                className="btn btn-danger rounded-0"
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
          <div className="inner bg-light lter">
            <Outlet />
          </div>
        </div>
      </div>
    </div>
  )
}

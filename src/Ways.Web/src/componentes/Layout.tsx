import { NavLink, Outlet, useNavigate } from 'react-router'
import { useAuth } from '../auth/useAuth'
import { DESCRIPTORES_DE_CATALOGO } from '../api/catalogos'
import {
  ROL,
  puedeAprovisionarTenants,
  puedeGestionarCatalogos,
  puedeGestionarUsuarios,
  puedeOperarPos,
  puedeVerAuditoria,
  puedeVerReportes,
} from '../api/tipos'

export function Layout() {
  const { usuario, cerrarSesion } = useAuth()
  const navegar = useNavigate()

  async function salir() {
    await cerrarSesion()
    navegar('/login', { replace: true })
  }

  return (
    <div id="wrap" className="bg-dark dk">
      <div id="top">
        {/* Franja de color del punto de venta. Hoy es fija; cuando exista la tabla
            de puntos de venta vuelve a pintar según el local activo. */}
        <nav className="ways-nav color_1" />

        <nav className="navbar navbar-dark navbar-expand-lg bg-dark border-top-0">
          <div className="container">
            <NavLink className="navbar-brand ways-brand" to="/">
              Ways
            </NavLink>

            <div className="collapse navbar-collapse show">
              <ul className="navbar-nav">
                <li className="nav-item">
                  <NavLink className="nav-link" to="/">
                    Inicio
                  </NavLink>
                </li>
                {usuario && puedeOperarPos(usuario.rolId) && (
                  <li className="nav-item">
                    <NavLink className="nav-link" to="/pos">
                      POS
                    </NavLink>
                  </li>
                )}
                {usuario && puedeOperarPos(usuario.rolId) && (
                  <li className="nav-item">
                    <NavLink className="nav-link" to="/caja">
                      Caja
                    </NavLink>
                  </li>
                )}
                {/* stage-8-compras-transferencias-inventario (Slice 5, design: Web Composition,
                    decisión 11): la lectura sigue Politicas.OperacionDePos, igual que la ruta —
                    nav y ruta comparten la misma política de lectura; la escritura queda oculta
                    dentro de la pantalla vía `puedeEscribir` (Admin-only, cosmético). */}
                {usuario && puedeOperarPos(usuario.rolId) && (
                  <li className="nav-item">
                    <NavLink className="nav-link" to="/compras">
                      Compras
                    </NavLink>
                  </li>
                )}
                {/* stage-17-presupuestos-y-remitos (Slice 7, design: Web composition): mismo gate
                    que /pos/-compras (Politicas.OperacionDePos) — nav y ruta comparten política. */}
                {usuario && puedeOperarPos(usuario.rolId) && (
                  <li className="nav-item">
                    <NavLink className="nav-link" to="/presupuestos">
                      Presupuestos
                    </NavLink>
                  </li>
                )}
                {/* stage-17-presupuestos-y-remitos (Slice 8, design: Web composition): mismo gate
                    que /presupuestos (Politicas.OperacionDePos) — nav y ruta comparten política. */}
                {usuario && puedeOperarPos(usuario.rolId) && (
                  <li className="nav-item">
                    <NavLink className="nav-link" to="/remitos">
                      Remitos
                    </NavLink>
                  </li>
                )}
                {/* stage-10-agregacion-dashboard (Slice 7, design: Web Composition): nav y ruta
                    comparten Politicas.LecturaDeReportes — Supervisor + Admin, ni Vendedor ni
                    Root. */}
                {usuario && puedeVerReportes(usuario.rolId) && (
                  <li className="nav-item">
                    <NavLink className="nav-link" to="/tablero">
                      Tablero
                    </NavLink>
                  </li>
                )}
                {/* stage-11-exportacion-reportes (Slices 6a/7, design: "nav entries + routes
                    gated like /tablero (puedeVerReportes) for cajas/tesorería"): mismo gate que
                    Tablero — nunca el cajero (/caja), esta es la vista de gestión. */}
                {usuario && puedeVerReportes(usuario.rolId) && (
                  <li className="nav-item">
                    <NavLink className="nav-link" to="/caja/historico">
                      Histórico de cajas
                    </NavLink>
                  </li>
                )}
                {usuario && puedeVerReportes(usuario.rolId) && (
                  <li className="nav-item">
                    <NavLink className="nav-link" to="/caja/tesoreria">
                      Tesorería
                    </NavLink>
                  </li>
                )}
                {/* stage-11-exportacion-reportes (Slice 9, droppable a Etapa 13): mismo gate que
                    Tablero/Histórico de cajas/Tesorería. */}
                {usuario && puedeVerReportes(usuario.rolId) && (
                  <li className="nav-item">
                    <NavLink className="nav-link" to="/reportes/existencias">
                      Existencias
                    </NavLink>
                  </li>
                )}
                {/* stage-12-lotes-vencimientos (Slice 15): mismo gate que Existencias
                    (puedeVerReportes). */}
                {usuario && puedeVerReportes(usuario.rolId) && (
                  <li className="nav-item">
                    <NavLink className="nav-link" to="/reportes/stock/vencimientos">
                      Vencimientos
                    </NavLink>
                  </li>
                )}
                {/* stage-13-stock-inteligente (Slice 6): mismo gate que Vencimientos
                    (puedeVerReportes). */}
                {usuario && puedeVerReportes(usuario.rolId) && (
                  <li className="nav-item">
                    <NavLink className="nav-link" to="/reportes/stock/reposicion">
                      Reposición
                    </NavLink>
                  </li>
                )}
                {/* stage-14-auditoria-trazabilidad (Slice 7, design decisión 17): Admin-only —
                    nav y ruta comparten Politicas.LecturaDeAuditoria (puedeVerAuditoria), NUNCA
                    apilada sobre puedeVerReportes (Supervisor queda afuera acá, a diferencia de
                    Tablero/Histórico de cajas/etc.). */}
                {usuario && puedeVerAuditoria(usuario.rolId) && (
                  <li className="nav-item">
                    <NavLink className="nav-link" to="/auditoria">
                      Auditoría
                    </NavLink>
                  </li>
                )}
                {usuario && puedeGestionarUsuarios(usuario.rolId) && (
                  <li className="nav-item">
                    <NavLink className="nav-link" to="/usuarios">
                      Usuarios
                    </NavLink>
                  </li>
                )}
                {usuario && puedeGestionarCatalogos(usuario.rolId) && (
                  <>
                    <li className="nav-item">
                      <NavLink className="nav-link" to="/clientes">
                        Clientes
                      </NavLink>
                    </li>
                    <li className="nav-item">
                      <NavLink className="nav-link" to="/proveedores">
                        Proveedores
                      </NavLink>
                    </li>
                    <li className="nav-item">
                      <NavLink className="nav-link" to="/articulos">
                        Artículos
                      </NavLink>
                    </li>
                    {/* stage-8-compras-transferencias-inventario (Slice 6, design: Web
                        Composition, decisión 11): pantallas de escritura pura, sin contraparte
                        de lectura — nav y ruta Admin-only end a end, mismo criterio que
                        /proveedores/-articulos. */}
                    <li className="nav-item">
                      <NavLink className="nav-link" to="/stock/transferencias">
                        Transferencias
                      </NavLink>
                    </li>
                    <li className="nav-item">
                      <NavLink className="nav-link" to="/stock/conteo">
                        Conteo de inventario
                      </NavLink>
                    </li>
                    <li className="nav-item">
                      <NavLink className="nav-link" to="/listas-precio">
                        Listas de precio
                      </NavLink>
                    </li>
                    <li className="nav-item">
                      <NavLink className="nav-link" to="/ofertas">
                        Ofertas
                      </NavLink>
                    </li>
                    {Object.values(DESCRIPTORES_DE_CATALOGO).map((d) => (
                      <li className="nav-item" key={d.recurso}>
                        <NavLink className="nav-link" to={`/catalogos/${d.recurso}`}>
                          {d.titulo}
                        </NavLink>
                      </li>
                    ))}
                    <li className="nav-item">
                      <NavLink className="nav-link" to="/catalogos/categorias">
                        Categorías
                      </NavLink>
                    </li>
                    <li className="nav-item">
                      <NavLink className="nav-link" to="/parametros">
                        Parámetros
                      </NavLink>
                    </li>
                    <li className="nav-item">
                      <NavLink className="nav-link" to="/catalogos-fiscales">
                        Catálogos fiscales
                      </NavLink>
                    </li>
                  </>
                )}
                {usuario && (usuario.rolId === ROL.Root || usuario.rolId === ROL.Admin) && (
                  <>
                    <li className="nav-item">
                      <NavLink className="nav-link" to="/organizacion/empresas">
                        Empresas
                      </NavLink>
                    </li>
                    <li className="nav-item">
                      <NavLink className="nav-link" to="/organizacion/puntos-venta">
                        Puntos de venta
                      </NavLink>
                    </li>
                  </>
                )}
                {usuario && puedeAprovisionarTenants(usuario.rolId) && (
                  <>
                    <li className="nav-item">
                      <NavLink className="nav-link" to="/organizacion/tenants">
                        Tenants
                      </NavLink>
                    </li>
                    <li className="nav-item">
                      <NavLink className="nav-link" to="/organizacion/nuevo-tenant">
                        Nuevo tenant
                      </NavLink>
                    </li>
                  </>
                )}
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

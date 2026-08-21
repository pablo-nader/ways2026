import { BrowserRouter, Navigate, Route, Routes } from 'react-router'
import { AuthProvider } from './auth/AuthContext'
import { RutaProtegida } from './auth/RutaProtegida'
import { Layout } from './componentes/Layout'
import { Articulos } from './paginas/Articulos'
import { Auditoria } from './paginas/Auditoria'
import { Caja } from './paginas/Caja'
import { CajaZ } from './paginas/CajaZ'
import { CierreDeCaja } from './paginas/CierreDeCaja'
import { Categorias } from './paginas/Categorias'
import { CatalogosFiscales } from './paginas/CatalogosFiscales'
import { Clientes } from './paginas/Clientes'
import { Compras } from './paginas/Compras'
import { CompraEditor } from './paginas/CompraEditor'
import { ConteoDeInventario } from './paginas/ConteoDeInventario'
import { CuentaCorriente } from './paginas/CuentaCorriente'
import { CuentaCorrienteDeProveedor } from './paginas/CuentaCorrienteDeProveedor'
import { Empresas } from './paginas/Empresas'
import { Existencias } from './paginas/Existencias'
import { HistoricoDeCajas } from './paginas/HistoricoDeCajas'
import { Inicio } from './paginas/Inicio'
import { Login } from './paginas/Login'
import { NuevoTenant } from './paginas/NuevoTenant'
import { Ofertas } from './paginas/Ofertas'
import { OrdenDeCompra } from './paginas/OrdenDeCompra'
import { OrdenesDeCompra } from './paginas/OrdenesDeCompra'
import { PaginaCatalogo } from './paginas/PaginaCatalogo'
import { Parametros } from './paginas/Parametros'
import { Pos } from './paginas/Pos'
import { Presupuesto } from './paginas/Presupuesto'
import { Presupuestos } from './paginas/Presupuestos'
import { Proveedores } from './paginas/Proveedores'
import { PuntosVenta } from './paginas/PuntosVenta'
import { FacturarRemitos } from './paginas/FacturarRemitos'
import { Remito } from './paginas/Remito'
import { Remitos } from './paginas/Remitos'
import { Reposicion } from './paginas/Reposicion'
import { RutaCatalogo } from './paginas/RutaCatalogo'
import { Tablero } from './paginas/Tablero'
import { Tenants } from './paginas/Tenants'
import { Tesoreria } from './paginas/Tesoreria'
import { Transferencias } from './paginas/Transferencias'
import { Usuarios } from './paginas/Usuarios'
import { Vencimientos } from './paginas/Vencimientos'
import { descriptorListasPrecio } from './api/catalogos'
import { ROL } from './api/tipos'

export function App() {
  return (
    <BrowserRouter>
      <AuthProvider>
        <Routes>
          {/* Única ruta sin sesión. */}
          <Route path="/login" element={<Login />} />

          <Route
            element={
              <RutaProtegida>
                <Layout />
              </RutaProtegida>
            }
          >
            <Route path="/" element={<Inicio />} />

            {/* stage-10-agregacion-dashboard (Slice 7, design: Web Composition, spec tablero):
                G1 parity — ventas y gastos de los últimos 7 días por defecto, ticket promedio.
                Politicas.LecturaDeReportes del lado del servidor: Supervisor + Admin, ni
                Vendedor ni Root. */}
            <Route
              path="/tablero"
              element={
                <RutaProtegida rolesPermitidos={[ROL.Supervisor, ROL.Admin]}>
                  <Tablero />
                </RutaProtegida>
              }
            />

            {/* stage-5-pos-ventas (Slice 6, design: POS Screen Composition): pantalla dedicada,
                admite Vendedor + Supervisor + Admin (Politicas.OperacionDePos) — no solo Admin
                como el resto del ABM. Ruta propia, no la máquina genérica de catálogos. */}
            <Route
              path="/pos"
              element={
                <RutaProtegida rolesPermitidos={[ROL.Vendedor, ROL.Supervisor, ROL.Admin]}>
                  <Pos />
                </RutaProtegida>
              }
            />
            {/* stage-6-turnos-caja (Slice 6, design: Web Composition): turno de caja del punto
                de venta — apertura, movimientos y resumen parcial. Misma política de rol que
                /pos (Politicas.OperacionDePos): Vendedor + Supervisor + Admin. */}
            <Route
              path="/caja"
              element={
                <RutaProtegida rolesPermitidos={[ROL.Vendedor, ROL.Supervisor, ROL.Admin]}>
                  <Caja />
                </RutaProtegida>
              }
            />
            {/* stage-6-turnos-caja (Slice 7, design: Web Composition): cierre de turno — el
                turno lo trae la URL (`?idTurno=`), se navega acá desde el panel del turno
                abierto en /caja. Misma política de rol. */}
            <Route
              path="/caja/cierre"
              element={
                <RutaProtegida rolesPermitidos={[ROL.Vendedor, ROL.Supervisor, ROL.Admin]}>
                  <CierreDeCaja />
                </RutaProtegida>
              }
            />
            {/* stage-11-exportacion-reportes (Slice 6b, design: Web Composition): Caja Z —
                mismo gate que /caja (Politicas.OperacionDePos), el cajero lee su propio cierre. */}
            <Route
              path="/caja/turnos/:id/z"
              element={
                <RutaProtegida rolesPermitidos={[ROL.Vendedor, ROL.Supervisor, ROL.Admin]}>
                  <CajaZ />
                </RutaProtegida>
              }
            />
            {/* stage-11-exportacion-reportes (Slice 6a, design: Web Composition, spec
                historico-de-cajas: Role Split): G2 — mismo gate que /tablero
                (Politicas.LecturaDeReportes), vista de gestión sobre turnos ajenos. */}
            <Route
              path="/caja/historico"
              element={
                <RutaProtegida rolesPermitidos={[ROL.Supervisor, ROL.Admin]}>
                  <HistoricoDeCajas />
                </RutaProtegida>
              }
            />
            {/* stage-11-exportacion-reportes (Slice 7, design: Web Composition, spec tesoreria):
                G3 — mismo gate que /tablero (Politicas.LecturaDeReportes). */}
            <Route
              path="/caja/tesoreria"
              element={
                <RutaProtegida rolesPermitidos={[ROL.Supervisor, ROL.Admin]}>
                  <Tesoreria />
                </RutaProtegida>
              }
            />
            {/* stage-11-exportacion-reportes (Slice 9, design: Web Composition, droppable a
                Etapa 13): mismo gate que /tablero (Politicas.LecturaDeReportes) — vista de
                gestión, no el balance del POS (Politicas.OperacionDePos de GET /api/stock). */}
            <Route
              path="/reportes/existencias"
              element={
                <RutaProtegida rolesPermitidos={[ROL.Supervisor, ROL.Admin]}>
                  <Existencias />
                </RutaProtegida>
              }
            />
            {/* stage-12-lotes-vencimientos (Slice 15, design: Web Composition): mismo gate que
                /reportes/existencias (Politicas.LecturaDeReportes) — vista de gestión sobre
                lotes con saldo positivo, no el picker del POS (Politicas.OperacionDePos de
                GET /api/stock/lotes). */}
            <Route
              path="/reportes/stock/vencimientos"
              element={
                <RutaProtegida rolesPermitidos={[ROL.Supervisor, ROL.Admin]}>
                  <Vencimientos />
                </RutaProtegida>
              }
            />
            {/* stage-13-stock-inteligente (Slice 6, design: "Reposicion.tsx — grouped by
                proveedor"): mismo gate que /reportes/stock/vencimientos
                (Politicas.LecturaDeReportes) — vista de gestión, agrupada por proveedor
                habitual. */}
            <Route
              path="/reportes/stock/reposicion"
              element={
                <RutaProtegida rolesPermitidos={[ROL.Supervisor, ROL.Admin]}>
                  <Reposicion />
                </RutaProtegida>
              }
            />
            {/* stage-14-auditoria-trazabilidad (Slice 7, design decisión 17): Admin-only —
                Politicas.LecturaDeAuditoria NO se apila sobre LecturaDeReportes, es su propio
                gate, mismo criterio admin-only que /clientes/-proveedores más abajo. */}
            <Route
              path="/auditoria"
              element={
                <RutaProtegida rolesPermitidos={[ROL.Admin]}>
                  <Auditoria />
                </RutaProtegida>
              }
            />
            <Route
              path="/usuarios"
              element={
                <RutaProtegida rolesPermitidos={[ROL.Root, ROL.Admin]}>
                  <Usuarios />
                </RutaProtegida>
              }
            />

            {/* Entidad dedicada (stage-2-clientes-proveedores, design decision 1): árbol
                propio, no la máquina genérica de catálogos. */}
            <Route
              path="/clientes"
              element={
                <RutaProtegida rolesPermitidos={[ROL.Admin]}>
                  <Clientes />
                </RutaProtegida>
              }
            />

            {/* stage-7-cuenta-corriente (Slice 5, design: Web Composition): estado de cuenta +
                pago a cuenta — Politicas.OperacionDePos (todo rol opera), a diferencia de
                /clientes que es admin-only. Entrada desde una fila de Clientes.tsx. */}
            <Route
              path="/clientes/:id/cuenta-corriente"
              element={
                <RutaProtegida rolesPermitidos={[ROL.Vendedor, ROL.Supervisor, ROL.Admin]}>
                  <CuentaCorriente />
                </RutaProtegida>
              }
            />

            {/* Entidad dedicada (stage-2-clientes-proveedores, design decision 1): árbol
                propio, no la máquina genérica de catálogos — mismo criterio que /clientes. */}
            <Route
              path="/proveedores"
              element={
                <RutaProtegida rolesPermitidos={[ROL.Admin]}>
                  <Proveedores />
                </RutaProtegida>
              }
            />

            {/* stage-15-cc-proveedores-ledger (Slice 6, design: Web Composition): estado de
                cuenta del proveedor — Politicas.OperacionDePos (todo rol opera), a diferencia de
                /proveedores que es admin-only. Entrada desde ResumenSaldoDeProveedor (panel de
                Proveedores.tsx y header filtrado de Compras.tsx). */}
            <Route
              path="/proveedores/:id/cuenta-corriente"
              element={
                <RutaProtegida rolesPermitidos={[ROL.Vendedor, ROL.Supervisor, ROL.Admin]}>
                  <CuentaCorrienteDeProveedor />
                </RutaProtegida>
              }
            />

            {/* Entidad dedicada (stage-3-articulos-y-precios, design decision 1): árbol
                propio, no la máquina genérica de catálogos — mismo criterio que /clientes. */}
            <Route
              path="/articulos"
              element={
                <RutaProtegida rolesPermitidos={[ROL.Admin]}>
                  <Articulos />
                </RutaProtegida>
              }
            />

            {/* stage-8-compras-transferencias-inventario (Slice 5, design: Web Composition,
                decisión 11): árbol propio, no la máquina genérica de catálogos — mismo criterio
                que /proveedores/-articulos. La lectura (listado + detalle) sigue
                Politicas.OperacionDePos, igual que /clientes/:id/cuenta-corriente — solo la
                escritura (borrador/confirmar/anular/aplicar precio) es Admin-only, cosmético acá
                y real en `GestionDeCatalogo` del lado del servidor (`puedeEscribir` en cada
                pantalla oculta esas acciones para el resto de los roles). */}
            <Route
              path="/compras"
              element={
                <RutaProtegida rolesPermitidos={[ROL.Vendedor, ROL.Supervisor, ROL.Admin]}>
                  <Compras />
                </RutaProtegida>
              }
            />
            <Route
              path="/compras/:id"
              element={
                <RutaProtegida rolesPermitidos={[ROL.Vendedor, ROL.Supervisor, ROL.Admin]}>
                  <CompraEditor />
                </RutaProtegida>
              }
            />

            {/* stage-16-ordenes-de-compra (Slice 6, design: Web composition, decisión 16): mismo
                gate de lectura que /compras (Politicas.OperacionDePos) — la escritura
                (borrador/enviar/cerrar/anular) es Admin-only, cosmético acá y real en
                GestionDeCatalogo del lado del servidor (puedeEscribir oculta esas acciones para
                el resto de los roles, mismo criterio que /compras). */}
            <Route
              path="/ordenes-compra"
              element={
                <RutaProtegida rolesPermitidos={[ROL.Vendedor, ROL.Supervisor, ROL.Admin]}>
                  <OrdenesDeCompra />
                </RutaProtegida>
              }
            />
            <Route
              path="/ordenes-compra/:id"
              element={
                <RutaProtegida rolesPermitidos={[ROL.Vendedor, ROL.Supervisor, ROL.Admin]}>
                  <OrdenDeCompra />
                </RutaProtegida>
              }
            />

            {/* stage-17-presupuestos-y-remitos (Slice 7, design: Web composition, decisión 17):
                mismo gate que /pos (Politicas.OperacionDePos) para lectura Y escritura — un
                Vendedor puede quotear igual que puede vender, sin la distinción admin-only de
                /ordenes-compra. */}
            <Route
              path="/presupuestos"
              element={
                <RutaProtegida rolesPermitidos={[ROL.Vendedor, ROL.Supervisor, ROL.Admin]}>
                  <Presupuestos />
                </RutaProtegida>
              }
            />
            <Route
              path="/presupuestos/nuevo"
              element={
                <RutaProtegida rolesPermitidos={[ROL.Vendedor, ROL.Supervisor, ROL.Admin]}>
                  <Presupuesto />
                </RutaProtegida>
              }
            />
            <Route
              path="/presupuestos/:id"
              element={
                <RutaProtegida rolesPermitidos={[ROL.Vendedor, ROL.Supervisor, ROL.Admin]}>
                  <Presupuesto />
                </RutaProtegida>
              }
            />

            {/* stage-17-presupuestos-y-remitos (Slice 8, design: Web composition, decisión 17):
                mismo gate que /presupuestos (Politicas.OperacionDePos) — un Vendedor despacha
                remitos igual que quotea. `/remitos/facturacion` va ANTES de `/remitos/:id` — un
                literal más específico gana sobre el parámetro en react-router, pero declararlo
                primero es el mismo criterio defensivo que el resto de las rutas con hijos. */}
            <Route
              path="/remitos"
              element={
                <RutaProtegida rolesPermitidos={[ROL.Vendedor, ROL.Supervisor, ROL.Admin]}>
                  <Remitos />
                </RutaProtegida>
              }
            />
            <Route
              path="/remitos/facturacion"
              element={
                <RutaProtegida rolesPermitidos={[ROL.Vendedor, ROL.Supervisor, ROL.Admin]}>
                  <FacturarRemitos />
                </RutaProtegida>
              }
            />
            <Route
              path="/remitos/nuevo"
              element={
                <RutaProtegida rolesPermitidos={[ROL.Vendedor, ROL.Supervisor, ROL.Admin]}>
                  <Remito />
                </RutaProtegida>
              }
            />
            <Route
              path="/remitos/:id"
              element={
                <RutaProtegida rolesPermitidos={[ROL.Vendedor, ROL.Supervisor, ROL.Admin]}>
                  <Remito />
                </RutaProtegida>
              }
            />

            {/* stage-8-compras-transferencias-inventario (Slice 6, design: Web Composition): a
                diferencia de /compras, estas dos pantallas son puro formulario de escritura, sin
                contraparte de lectura — Admin-only end a end, mismo nav que /proveedores. */}
            <Route
              path="/stock/transferencias"
              element={
                <RutaProtegida rolesPermitidos={[ROL.Admin]}>
                  <Transferencias />
                </RutaProtegida>
              }
            />
            <Route
              path="/stock/conteo"
              element={
                <RutaProtegida rolesPermitidos={[ROL.Admin]}>
                  <ConteoDeInventario />
                </RutaProtegida>
              }
            />

            {/* stage-3-articulos-y-precios (Slice 6, design: ABM Composition): ruta propia
                (no /catalogos/:recurso) — reusa la máquina genérica directamente, mismo
                criterio que /catalogos/categorias, para no competir con el switch de
                RutaCatalogo ni con GET /api/listas-precio (selector de solo lectura, stage 2). */}
            <Route
              path="/listas-precio"
              element={
                <RutaProtegida rolesPermitidos={[ROL.Admin]}>
                  <PaginaCatalogo definicion={descriptorListasPrecio} />
                </RutaProtegida>
              }
            />

            {/* Entidad dedicada (stage-4-ofertas, Slice 4, design decision 9): árbol propio,
                no la máquina genérica de catálogos — depende solo del CRUD de la Slice 2. */}
            <Route
              path="/ofertas"
              element={
                <RutaProtegida rolesPermitidos={[ROL.Admin]}>
                  <Ofertas />
                </RutaProtegida>
              }
            />

            {/* Escape hatch de ADR-11: árbol propio, no la máquina genérica. react-router
                prioriza segmentos literales sobre ":recurso" sin importar el orden de
                declaración, pero queda declarada antes por legibilidad. */}
            <Route
              path="/catalogos/categorias"
              element={
                <RutaProtegida rolesPermitidos={[ROL.Admin]}>
                  <Categorias />
                </RutaProtegida>
              }
            />
            {/* Los 4 catálogos restantes pasan por la máquina genérica (ADR-11). */}
            <Route
              path="/catalogos/:recurso"
              element={
                <RutaProtegida rolesPermitidos={[ROL.Admin]}>
                  <RutaCatalogo />
                </RutaProtegida>
              }
            />
            <Route
              path="/catalogos-fiscales"
              element={
                <RutaProtegida rolesPermitidos={[ROL.Admin]}>
                  <CatalogosFiscales />
                </RutaProtegida>
              }
            />
            <Route
              path="/parametros"
              element={
                <RutaProtegida rolesPermitidos={[ROL.Admin]}>
                  <Parametros />
                </RutaProtegida>
              }
            />
            <Route
              path="/organizacion/tenants"
              element={
                <RutaProtegida rolesPermitidos={[ROL.Root]}>
                  <Tenants />
                </RutaProtegida>
              }
            />
            <Route
              path="/organizacion/nuevo-tenant"
              element={
                <RutaProtegida rolesPermitidos={[ROL.Root]}>
                  <NuevoTenant />
                </RutaProtegida>
              }
            />
            <Route
              path="/organizacion/empresas"
              element={
                <RutaProtegida rolesPermitidos={[ROL.Root, ROL.Admin]}>
                  <Empresas />
                </RutaProtegida>
              }
            />
            <Route
              path="/organizacion/puntos-venta"
              element={
                <RutaProtegida rolesPermitidos={[ROL.Root, ROL.Admin]}>
                  <PuntosVenta />
                </RutaProtegida>
              }
            />
          </Route>

          <Route path="*" element={<Navigate to="/" replace />} />
        </Routes>
      </AuthProvider>
    </BrowserRouter>
  )
}

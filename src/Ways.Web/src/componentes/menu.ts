import { DESCRIPTORES_DE_CATALOGO } from '../api/catalogos'
import type { UsuarioAutenticado } from '../api/tipos'
import {
  ROL,
  puedeAprovisionarTenants,
  puedeGestionarCatalogos,
  puedeGestionarUsuarios,
  puedeOperarPos,
  puedeVerAuditoria,
  puedeVerReportes,
} from '../api/tipos'

export type EnlaceDeMenu = {
  tipo: 'enlace'
  etiqueta: string
  a: string
  /** La acción principal de la barra: se pinta como botón, no como link. */
  principal?: boolean
  /** Sin declarar, el enlace reclama `a` y todo lo que cuelga de ella (ver `esRutaActiva`). */
  rutasActivas?: string[]
}
export type SeccionDeMenu = { titulo?: string; enlaces: EnlaceDeMenu[] }
export type GrupoDeMenu = { tipo: 'grupo'; etiqueta: string; secciones: SeccionDeMenu[] }
export type EntradaDeMenu = EnlaceDeMenu | GrupoDeMenu

type Permiso = (rolId: number) => boolean

type EnlaceDelModelo = { permiso: Permiso; enlace: EnlaceDeMenu }
type SeccionDelModelo = { titulo?: string; enlaces: EnlaceDelModelo[] }
type GrupoDelModelo = { etiqueta: string; secciones: SeccionDelModelo[] }
type EntradaDelModelo = EnlaceDelModelo | GrupoDelModelo

function enlace(
  permiso: Permiso,
  etiqueta: string,
  a: string,
  extra: Omit<EnlaceDeMenu, 'tipo' | 'etiqueta' | 'a'> = {},
): EnlaceDelModelo {
  return { permiso, enlace: { tipo: 'enlace', etiqueta, a, ...extra } }
}

/** Mismo criterio que el gate de Empresas/Puntos de venta que hoy tiene `Layout`. */
const rootOAdmin: Permiso = (rolId) => rolId === ROL.Root || rolId === ROL.Admin

/** El menú completo, en el orden en que se muestra; cada enlace lleva su permiso y
 * `construirMenu` deja solo lo que el rol puede ver. */
const MODELO: EntradaDelModelo[] = [
  enlace(puedeOperarPos, 'Vender', '/pos', { principal: true }),
  enlace(puedeOperarPos, 'Caja', '/caja', { rutasActivas: ['/caja', '/caja/cierre', '/caja/turnos'] }),
  {
    etiqueta: 'Ventas',
    secciones: [
      {
        enlaces: [
          enlace(puedeOperarPos, 'Presupuestos', '/presupuestos'),
          enlace(puedeOperarPos, 'Remitos', '/remitos'),
          enlace(puedeOperarPos, 'Consulta de precios', '/consulta-precios'),
        ],
      },
    ],
  },
  {
    etiqueta: 'Compras',
    secciones: [
      {
        enlaces: [
          enlace(puedeOperarPos, 'Compras', '/compras'),
          enlace(puedeOperarPos, 'Órdenes de compra', '/ordenes-compra'),
        ],
      },
    ],
  },
  {
    etiqueta: 'Reportes',
    secciones: [
      {
        enlaces: [
          enlace(puedeVerReportes, 'Tablero', '/tablero'),
          enlace(puedeVerReportes, 'Histórico de cajas', '/caja/historico'),
          enlace(puedeVerReportes, 'Tesorería', '/caja/tesoreria'),
          enlace(puedeVerReportes, 'Existencias', '/reportes/existencias'),
          enlace(puedeVerReportes, 'Vencimientos', '/reportes/stock/vencimientos'),
          enlace(puedeVerReportes, 'Reposición', '/reportes/stock/reposicion'),
        ],
      },
    ],
  },
  {
    etiqueta: 'Administración',
    secciones: [
      {
        titulo: 'Catálogo',
        enlaces: [
          enlace(puedeGestionarCatalogos, 'Artículos', '/articulos'),
          enlace(puedeGestionarCatalogos, 'Categorías', '/catalogos/categorias'),
          ...Object.values(DESCRIPTORES_DE_CATALOGO).map((descriptor) =>
            enlace(puedeGestionarCatalogos, descriptor.titulo, `/catalogos/${descriptor.recurso}`),
          ),
          enlace(puedeGestionarCatalogos, 'Listas de precio', '/listas-precio'),
          enlace(puedeGestionarCatalogos, 'Ofertas', '/ofertas'),
          enlace(puedeGestionarCatalogos, 'Catálogos fiscales', '/catalogos-fiscales'),
        ],
      },
      {
        titulo: 'Terceros',
        enlaces: [
          enlace(puedeGestionarCatalogos, 'Clientes', '/clientes'),
          enlace(puedeGestionarCatalogos, 'Proveedores', '/proveedores'),
        ],
      },
      {
        titulo: 'Stock',
        enlaces: [
          enlace(puedeGestionarCatalogos, 'Transferencias', '/stock/transferencias'),
          enlace(puedeGestionarCatalogos, 'Conteo de inventario', '/stock/conteo'),
        ],
      },
      {
        titulo: 'Configuración',
        enlaces: [
          enlace(puedeGestionarCatalogos, 'Parámetros', '/parametros'),
          enlace(puedeGestionarUsuarios, 'Usuarios', '/usuarios'),
          enlace(puedeVerAuditoria, 'Auditoría', '/auditoria'),
        ],
      },
      {
        titulo: 'Organización',
        enlaces: [
          enlace(rootOAdmin, 'Empresas', '/organizacion/empresas'),
          enlace(rootOAdmin, 'Puntos de venta', '/organizacion/puntos-venta'),
          enlace(puedeAprovisionarTenants, 'Tenants', '/organizacion/tenants'),
          enlace(puedeAprovisionarTenants, 'Nuevo tenant', '/organizacion/nuevo-tenant'),
        ],
      },
    ],
  },
]

/** Entradas visibles para el usuario, en orden; un grupo sin enlaces visibles no se devuelve y
 * una sección vacía tampoco. */
export function construirMenu(usuario: UsuarioAutenticado): EntradaDeMenu[] {
  const visible = (candidato: EnlaceDelModelo) => candidato.permiso(usuario.rolId)
  const entradas: EntradaDeMenu[] = []

  for (const entrada of MODELO) {
    if ('enlace' in entrada) {
      if (visible(entrada)) entradas.push(entrada.enlace)
      continue
    }
    const secciones = entrada.secciones
      .map((seccion) => ({
        titulo: seccion.titulo,
        enlaces: seccion.enlaces.filter(visible).map((candidato) => candidato.enlace),
      }))
      .filter((seccion) => seccion.enlaces.length > 0)
    if (secciones.length > 0) entradas.push({ tipo: 'grupo', etiqueta: entrada.etiqueta, secciones })
  }

  return entradas
}

/**
 * Un enlace sin `rutasActivas` reclama su ruta y todo lo que cuelga de ella (`/articulos/5`).
 * Con `rutasActivas` declaradas reclama solo su propia ruta exacta más los subárboles listados:
 * así `/caja/historico` y `/caja/tesoreria` quedan para Reportes aunque cuelguen de `/caja`, y
 * para cualquier ruta hay a lo sumo una entrada de primer nivel activa.
 */
export function esRutaActiva(pathname: string, entrada: EntradaDeMenu): boolean {
  if (entrada.tipo === 'grupo') {
    return entrada.secciones.some((seccion) => seccion.enlaces.some((enlace) => esRutaActiva(pathname, enlace)))
  }
  if (!entrada.rutasActivas) return estaBajo(pathname, entrada.a)
  return entrada.rutasActivas.some((ruta) => (ruta === entrada.a ? pathname === ruta : estaBajo(pathname, ruta)))
}

function estaBajo(pathname: string, ruta: string) {
  return pathname === ruta || pathname.startsWith(`${ruta}/`)
}

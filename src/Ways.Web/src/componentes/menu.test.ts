import { describe, expect, it } from 'vitest'
import { ROL } from '../api/tipos'
import type { UsuarioAutenticado } from '../api/tipos'
import { construirMenu, esRutaActiva } from './menu'
import type { EnlaceDeMenu, EntradaDeMenu } from './menu'

function usuarioConRol(rolId: number): UsuarioAutenticado {
  return {
    id: 9,
    usuario: 'alguien',
    mail: 'alguien@ways.test',
    rolId,
    rol: 'Rol',
    ultimaConexion: null,
    idTenant: rolId === ROL.Root ? null : 1,
  }
}

const ROLES_OPERATIVOS: [string, number][] = [
  ['Admin', ROL.Admin],
  ['Supervisor', ROL.Supervisor],
  ['Vendedor', ROL.Vendedor],
]
const TODOS_LOS_ROLES: [string, number][] = [['Root', ROL.Root], ...ROLES_OPERATIVOS]

type Resumen = [string, string] | { grupo: string; secciones: { titulo?: string; enlaces: [string, string][] }[] }

function par(enlace: EnlaceDeMenu): [string, string] {
  return [enlace.etiqueta, enlace.a]
}

/** Proyección exacta de etiquetas, rutas, secciones y orden: cualquier alta, baja o reorden de
 * una entrada rompe la comparación. */
function resumir(entradas: EntradaDeMenu[]): Resumen[] {
  return entradas.map((entrada) =>
    entrada.tipo === 'enlace'
      ? par(entrada)
      : {
          grupo: entrada.etiqueta,
          secciones: entrada.secciones.map((seccion) => ({ titulo: seccion.titulo, enlaces: seccion.enlaces.map(par) })),
        },
  )
}

function etiquetas(entradas: EntradaDeMenu[]): string[] {
  return entradas.flatMap((entrada) =>
    entrada.tipo === 'enlace'
      ? [entrada.etiqueta]
      : [entrada.etiqueta, ...entrada.secciones.flatMap((seccion) => seccion.enlaces.map((enlace) => enlace.etiqueta))],
  )
}

function rutasHoja(entradas: EntradaDeMenu[]): string[] {
  return entradas.flatMap((entrada) =>
    entrada.tipo === 'enlace' ? [entrada.a] : entrada.secciones.flatMap((seccion) => seccion.enlaces.map((enlace) => enlace.a)),
  )
}

const VENDER: Resumen = ['Vender', '/pos']
const CAJA: Resumen = ['Caja', '/caja']
const VENTAS: Resumen = {
  grupo: 'Ventas',
  secciones: [
    {
      enlaces: [
        ['Presupuestos', '/presupuestos'],
        ['Remitos', '/remitos'],
        ['Consulta de precios', '/consulta-precios'],
      ],
    },
  ],
}
const COMPRAS: Resumen = {
  grupo: 'Compras',
  secciones: [
    {
      enlaces: [
        ['Compras', '/compras'],
        ['Órdenes de compra', '/ordenes-compra'],
      ],
    },
  ],
}
// stage-gastos-admin-retroactivos (PR3): "Gastos" es Admin-only (puedeGestionarCatalogos) dentro
// del MISMO grupo "Compras" que Vendedor/Supervisor sí ven — por eso es un fixture aparte, no un
// campo agregado a COMPRAS de arriba.
const COMPRAS_DE_ADMIN: Resumen = {
  grupo: 'Compras',
  secciones: [
    {
      enlaces: [
        ['Compras', '/compras'],
        ['Órdenes de compra', '/ordenes-compra'],
        ['Gastos', '/gastos'],
      ],
    },
  ],
}
const REPORTES: Resumen = {
  grupo: 'Reportes',
  secciones: [
    {
      enlaces: [
        ['Tablero', '/tablero'],
        ['Histórico de cajas', '/caja/historico'],
        ['Tesorería', '/caja/tesoreria'],
        ['Existencias', '/reportes/existencias'],
        ['Vencimientos', '/reportes/stock/vencimientos'],
        ['Reposición', '/reportes/stock/reposicion'],
        ['Artículos', '/reportes/articulos'],
        ['Libro IVA', '/reportes/libro-iva'],
      ],
    },
  ],
}
/** Lo mismo que `REPORTES` más la configuración de cuentas de tesorería, que es solo del Admin. */
const REPORTES_DE_ADMIN: Resumen = {
  grupo: 'Reportes',
  secciones: [
    {
      enlaces: [
        ['Tablero', '/tablero'],
        ['Histórico de cajas', '/caja/historico'],
        ['Tesorería', '/caja/tesoreria'],
        ['Cuentas de tesorería', '/caja/cuentas-tesoreria'],
        ['Existencias', '/reportes/existencias'],
        ['Vencimientos', '/reportes/stock/vencimientos'],
        ['Reposición', '/reportes/stock/reposicion'],
        ['Artículos', '/reportes/articulos'],
        ['Libro IVA', '/reportes/libro-iva'],
      ],
    },
  ],
}
const ADMINISTRACION_DE_ADMIN: Resumen = {
  grupo: 'Administración',
  secciones: [
    {
      titulo: 'Catálogo',
      enlaces: [
        ['Artículos', '/articulos'],
        ['Familias', '/familias'],
        ['Categorías', '/catalogos/categorias'],
        ['Áreas', '/catalogos/areas'],
        ['Marcas', '/catalogos/marcas'],
        ['Grupos', '/catalogos/grupos'],
        ['Medios de pago', '/catalogos/medios-pago'],
        ['Listas de precio', '/listas-precio'],
        ['Ofertas', '/ofertas'],
        ['Combos', '/combos'],
        ['Catálogos fiscales', '/catalogos-fiscales'],
      ],
    },
    {
      titulo: 'Terceros',
      enlaces: [
        ['Clientes', '/clientes'],
        ['Proveedores', '/proveedores'],
        ['Personal', '/personal'],
      ],
    },
    {
      titulo: 'Stock',
      enlaces: [
        ['Transferencias', '/stock/transferencias'],
        ['Conteo de inventario', '/stock/conteo'],
      ],
    },
    {
      titulo: 'Configuración',
      enlaces: [
        ['Parámetros', '/parametros'],
        ['Usuarios', '/usuarios'],
        ['Auditoría', '/auditoria'],
      ],
    },
    {
      titulo: 'Organización',
      enlaces: [
        ['Empresas', '/organizacion/empresas'],
        ['Puntos de venta', '/organizacion/puntos-venta'],
        ['Equipos POS', '/organizacion/equipos-pos'],
        ['Vitrina', '/organizacion/vitrina'],
      ],
    },
  ],
}
// Personal es de lectura para Supervisor: es lo único de Administración que ve.
const ADMINISTRACION_DE_SUPERVISOR: Resumen = {
  grupo: 'Administración',
  secciones: [{ titulo: 'Terceros', enlaces: [['Personal', '/personal']] }],
}
const ADMINISTRACION_DE_ROOT: Resumen = {
  grupo: 'Administración',
  secciones: [
    { titulo: 'Configuración', enlaces: [['Usuarios', '/usuarios']] },
    {
      titulo: 'Organización',
      enlaces: [
        ['Empresas', '/organizacion/empresas'],
        ['Puntos de venta', '/organizacion/puntos-venta'],
        ['Vitrina', '/organizacion/vitrina'],
        ['Tenants', '/organizacion/tenants'],
        ['Nuevo tenant', '/organizacion/nuevo-tenant'],
        ['Dominios', '/organizacion/dominios'],
      ],
    },
  ],
}

describe('construirMenu', () => {
  it('Vendedor: Vender, Caja, Ventas y Compras, sin Reportes ni Administración', () => {
    expect(resumir(construirMenu(usuarioConRol(ROL.Vendedor)))).toEqual([VENDER, CAJA, VENTAS, COMPRAS])
  })

  it('Supervisor: lo mismo que Vendedor más Reportes y Personal en solo lectura', () => {
    expect(resumir(construirMenu(usuarioConRol(ROL.Supervisor)))).toEqual([
      VENDER,
      CAJA,
      VENTAS,
      COMPRAS,
      REPORTES,
      ADMINISTRACION_DE_SUPERVISOR,
    ])
  })

  it('Admin: todo, con Administración completa salvo Tenants, Nuevo tenant y Dominios', () => {
    expect(resumir(construirMenu(usuarioConRol(ROL.Admin)))).toEqual([
      VENDER,
      CAJA,
      VENTAS,
      COMPRAS_DE_ADMIN,
      REPORTES_DE_ADMIN,
      ADMINISTRACION_DE_ADMIN,
    ])
  })

  it('Root: solo Administración, con Usuarios y toda la organización', () => {
    expect(resumir(construirMenu(usuarioConRol(ROL.Root)))).toEqual([ADMINISTRACION_DE_ROOT])
  })

  it.each(TODOS_LOS_ROLES)('%s: ninguna entrada se llama "Inicio" (el logo lleva al inicio)', (_nombre, rolId) => {
    expect(etiquetas(construirMenu(usuarioConRol(rolId)))).not.toContain('Inicio')
  })

  it.each(ROLES_OPERATIVOS)('%s: la única entrada principal es Vender → /pos', (_nombre, rolId) => {
    const principales = construirMenu(usuarioConRol(rolId)).filter((entrada) => entrada.tipo === 'enlace' && entrada.principal)

    expect(principales).toEqual([expect.objectContaining({ etiqueta: 'Vender', a: '/pos' })])
  })

  it('Root no tiene entrada principal: no opera el POS', () => {
    const principales = construirMenu(usuarioConRol(ROL.Root)).filter((entrada) => entrada.tipo === 'enlace' && entrada.principal)

    expect(principales).toEqual([])
  })

  it('Vendedor: no tiene Administración', () => {
    expect(construirMenu(usuarioConRol(ROL.Vendedor)).some((entrada) => entrada.etiqueta === 'Administración')).toBe(false)
  })

  it('Root no ve Personal: opera tenants, no un negocio', () => {
    expect(etiquetas(construirMenu(usuarioConRol(ROL.Root)))).not.toContain('Personal')
  })

  it('Caja declara exactamente las rutas que la dejan activa', () => {
    const caja = construirMenu(usuarioConRol(ROL.Vendedor)).find((entrada) => entrada.etiqueta === 'Caja')

    expect(caja).toEqual({
      tipo: 'enlace',
      etiqueta: 'Caja',
      a: '/caja',
      rutasActivas: ['/caja', '/caja/cierre', '/caja/turnos'],
    })
  })
})

describe('esRutaActiva', () => {
  const menuDeAdmin = construirMenu(usuarioConRol(ROL.Admin))
  const activas = (pathname: string) => menuDeAdmin.filter((entrada) => esRutaActiva(pathname, entrada)).map((entrada) => entrada.etiqueta)

  // Cláusula bajo prueba: con `rutasActivas` declaradas, la ruta propia del enlace matchea
  // exacta. Evidencia de mutación: reemplazarla por el prefijo de segmento hace fallar los dos
  // casos de `/caja/historico` y `/caja/tesoreria` (`['Caja', 'Reportes']`) y las dos corridas
  // de unicidad de Admin y Supervisor.
  const casos: [string, string[]][] = [
    ['/caja/historico', ['Reportes']],
    ['/caja/tesoreria', ['Reportes']],
    ['/caja/cuentas-tesoreria', ['Reportes']],
    ['/caja', ['Caja']],
    ['/caja/cierre', ['Caja']],
    ['/caja/turnos/7/z', ['Caja']],
    ['/pos', ['Vender']],
    ['/catalogos/marcas', ['Administración']],
    ['/articulos/5', ['Administración']],
    ['/familias/7', ['Administración']],
    ['/familias/nueva', ['Administración']],
    ['/personal', ['Administración']],
    ['/personal/3/cuenta', ['Administración']],
    ['/', []],
    ['/cajas', []],
  ]

  it.each(casos)('%s activa %j', (pathname, esperado) => {
    expect(activas(pathname)).toEqual(esperado)
  })

  it.each(TODOS_LOS_ROLES)('%s: cada ruta del menú activa exactamente una entrada de primer nivel', (_nombre, rolId) => {
    const menu = construirMenu(usuarioConRol(rolId))
    const rutas = rutasHoja(menu)

    expect(rutas.length).toBeGreaterThan(0)
    for (const ruta of rutas) {
      const entradasActivas = menu.filter((entrada) => esRutaActiva(ruta, entrada)).map((entrada) => entrada.etiqueta)
      expect(entradasActivas, ruta).toHaveLength(1)
    }
  })
})

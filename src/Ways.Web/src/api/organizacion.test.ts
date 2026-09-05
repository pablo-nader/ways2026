import { describe, expect, it } from 'vitest'
import {
  ETIQUETA_OPCION_PLATAFORMA,
  ETIQUETA_PLATAFORMA,
  ETIQUETA_SIN_DUENIO,
  etiquetaDeTenant,
  filtrarPorEmpresa,
  filtrarPorTenant,
  opcionesDeEmpresa,
  opcionesDeTenant,
  opcionesDeTenantAsignable,
  seleccionVigente,
  SIN_FILTRO,
  VALOR_SIN_TENANT,
  type FilaConEmpresa,
  type FilaConTenant,
} from './organizacion'
import type { EstadoTenant, TenantListado } from './tipos'

// stage-20-organizacion-relaciones-y-bajas, slice 2 (tarea 2.8) — `web-descriptor-tests`: los
// cinco helpers puros de `organizacion.ts` con un caso por rama, no un happy path.

function fila(idTenant: number | null, nombreTenant: string | null): FilaConTenant {
  return { idTenant, nombreTenant }
}

function filaPv(
  idTenant: number | null,
  nombreTenant: string | null,
  idEmpresa: number,
  razonSocialEmpresa: string | null,
): FilaConEmpresa {
  return { idTenant, nombreTenant, idEmpresa, razonSocialEmpresa }
}

describe('etiquetaDeTenant', () => {
  it('rinde el literal "Plataforma" cuando idTenant es null', () => {
    expect(etiquetaDeTenant(fila(null, null))).toBe(ETIQUETA_PLATAFORMA)
  })

  it('rinde el nombre del tenant cuando la cuenta tiene tenant', () => {
    expect(etiquetaDeTenant(fila(2, 'Comercio Sur'))).toBe('Comercio Sur')
  })

  /**
   * Cláusula bajo prueba: el discriminador de `etiquetaDeTenant` es `idTenant`, NO el nombre
   * (Reconciliación 9 / design D13). Un nombre nulo con idTenant presente es un HUÉRFANO — el
   * tenant dueño quedó dado de baja — y NO puede rendirse como personal de plataforma. Los dos
   * casos comparten `nombreTenant === null`, así que un helper que se apoyara en el nombre
   * devolvería "Plataforma" acá.
   */
  it('un huérfano (idTenant presente, nombre nulo) NO se rinde como plataforma', () => {
    expect(etiquetaDeTenant(fila(7, null))).toBe(ETIQUETA_SIN_DUENIO)
    expect(etiquetaDeTenant(fila(7, null))).not.toBe(ETIQUETA_PLATAFORMA)
  })

  /**
   * Cláusula bajo prueba: la etiqueta "Plataforma" la fabrica la WEB (design D14) y un tenant
   * puede llamarse literalmente así. Los dos rinden el mismo texto en la columna, pero son filas
   * distintas y el filtro las distingue por `idTenant` — ver la prueba de `opcionesDeTenant`.
   */
  it('un tenant llamado "Plataforma" rinde su propio nombre y no se confunde con la etiqueta', () => {
    const tenantHomonimo = fila(9, 'Plataforma')

    expect(etiquetaDeTenant(tenantHomonimo)).toBe('Plataforma')
    expect(tenantHomonimo.idTenant).not.toBeNull()
  })
})

describe('opcionesDeTenant', () => {
  it('deduplica por idTenant y ordena por etiqueta', () => {
    const opciones = opcionesDeTenant([
      fila(3, 'Zapatería Norte'),
      fila(2, 'Comercio Sur'),
      fila(3, 'Zapatería Norte'),
      fila(1, 'Almacén Este'),
    ])

    expect(opciones).toEqual([
      { valor: '1', etiqueta: 'Almacén Este' },
      { valor: '2', etiqueta: 'Comercio Sur' },
      { valor: '3', etiqueta: 'Zapatería Norte' },
    ])
  })

  it('agrega la opción de plataforma primero y solo si alguna fila no tiene tenant', () => {
    const conPlataforma = opcionesDeTenant([fila(2, 'Comercio Sur'), fila(null, null)])
    const sinPlataforma = opcionesDeTenant([fila(2, 'Comercio Sur')])

    expect(conPlataforma[0]).toEqual({ valor: VALOR_SIN_TENANT, etiqueta: ETIQUETA_OPCION_PLATAFORMA })
    expect(conPlataforma).toHaveLength(2)
    expect(sinPlataforma.map((o) => o.valor)).toEqual(['2'])
  })

  /**
   * Cláusula bajo prueba: `claveDeTenant` devuelve un token que ningún `String(idTenant)` puede
   * producir. Si la opción de plataforma compartiera clave con un tenant, elegir una filtraría la
   * otra. El caso adversario es un tenant llamado literalmente "Plataforma": mismas ganas de
   * confundirse en la etiqueta, claves obligatoriamente distintas.
   */
  it('un tenant llamado "Plataforma" y el personal de plataforma son dos opciones distinguibles', () => {
    const opciones = opcionesDeTenant([fila(9, 'Plataforma'), fila(null, null)])

    expect(opciones).toHaveLength(2)
    expect(opciones.map((o) => o.valor)).toEqual([VALOR_SIN_TENANT, '9'])
    expect(new Set(opciones.map((o) => o.etiqueta)).size).toBe(2)
    expect(opciones.find((o) => o.valor === '9')?.etiqueta).toBe('Plataforma')
  })

  it('desempata los huérfanos con su id para que no compartan etiqueta', () => {
    const opciones = opcionesDeTenant([fila(7, null), fila(8, null)])

    expect(opciones.map((o) => o.valor).sort()).toEqual(['7', '8'])
    expect(new Set(opciones.map((o) => o.etiqueta)).size).toBe(2)
  })

  /** Spec S5: las opciones salen de las filas ya cargadas, así que un dataset de un solo tenant
   * ofrece exactamente una opción — el nombre de otro tenant no puede aparecer. */
  it('un dataset de un solo tenant ofrece exactamente una opción', () => {
    expect(opcionesDeTenant([fila(1, 'Tenant Uno'), fila(1, 'Tenant Uno')])).toEqual([
      { valor: '1', etiqueta: 'Tenant Uno' },
    ])
  })

  /**
   * Cláusula bajo prueba: `desempatarHomonimos` dentro de `opcionesDeTenant`. `nombre` es texto
   * libre, así que dos tenants DISTINTOS pueden compartirlo: sin desempate las dos opciones son
   * byte a byte idénticas y el operador elige a ciegas. El desempate del huérfano no cubre este
   * caso — acá los dos nombres existen — así que es una cláusula propia.
   */
  it('desempata con el id a dos tenants distintos que comparten nombre', () => {
    const opciones = opcionesDeTenant([fila(2, 'Comercio Sur'), fila(3, 'Comercio Sur')])

    expect(opciones).toEqual([
      { valor: '2', etiqueta: 'Comercio Sur (tenant 2)' },
      { valor: '3', etiqueta: 'Comercio Sur (tenant 3)' },
    ])
    expect(new Set(opciones.map((o) => o.etiqueta)).size).toBe(2)
  })

  /** El desempate toca SOLO a las que colisionan: un nombre único no se ensucia con su id. */
  it('no le agrega el id a un tenant cuyo nombre no se repite', () => {
    const opciones = opcionesDeTenant([fila(2, 'Comercio Sur'), fila(3, 'Comercio Sur'), fila(4, 'Único')])

    expect(opciones.find((o) => o.valor === '4')).toEqual({ valor: '4', etiqueta: 'Único' })
  })

  /**
   * Cláusula bajo prueba (ronda 2, R2-6): la opción de plataforma entra al mapa de colisiones de
   * `desempatarHomonimos`. `ETIQUETA_OPCION_PLATAFORMA` es texto fijo y `nombre` es texto libre, así
   * que un tenant llamado LITERALMENTE "Plataforma (sin tenant)" produce la MISMA etiqueta. El caso
   * de arriba ("Plataforma" a secas) no lo cubre: ahí las dos etiquetas ya diferían por el sufijo.
   * Las claves quedan intactas — la del personal de plataforma sigue sin ser ningún `String(id)`.
   */
  it('un tenant llamado exactamente como la opción de plataforma no queda con etiqueta idéntica', () => {
    const opciones = opcionesDeTenant([fila(9, ETIQUETA_OPCION_PLATAFORMA), fila(null, null)])

    expect(opciones.map((o) => o.valor)).toEqual([VALOR_SIN_TENANT, '9'])
    expect(new Set(opciones.map((o) => o.etiqueta)).size).toBe(2)
    expect(opciones.find((o) => o.valor === '9')?.etiqueta).toBe(
      `${ETIQUETA_OPCION_PLATAFORMA} (tenant 9)`,
    )
    expect(opciones.find((o) => o.valor === VALOR_SIN_TENANT)?.etiqueta).toBe(
      `${ETIQUETA_OPCION_PLATAFORMA} (tenant ${VALOR_SIN_TENANT})`,
    )
  })

  it('sobre una lista vacía no ofrece ninguna opción', () => {
    expect(opcionesDeTenant([])).toEqual([])
  })
})

describe('opcionesDeTenantAsignable', () => {
  function tenant(id: number, nombre: string, estado: EstadoTenant = 'Activo'): TenantListado {
    return {
      id,
      nombre,
      estado,
      createdAt: '2026-01-01T10:00:00-03:00',
      cantidadEmpresas: 0,
      cantidadPuntosVenta: 0,
      cantidadUsuarios: 0,
    }
  }

  it('ordena por etiqueta y no toca el nombre de un tenant activo', () => {
    expect(opcionesDeTenantAsignable([tenant(3, 'Zapatería Norte'), tenant(2, 'Comercio Sur')])).toEqual([
      { valor: '2', etiqueta: 'Comercio Sur' },
      { valor: '3', etiqueta: 'Zapatería Norte' },
    ])
  })

  /**
   * Cláusula bajo prueba: la marca de estado. El servidor es la autoridad y `CrearAsync` NO mira
   * el estado del tenant destino, así que un tenant suspendido se sigue ofreciendo —el operador
   * puede pre-crear ahí a propósito— pero sin la marca el usuario creado adentro simplemente no
   * podría iniciar sesión y nada en la pantalla lo diría. Esa es la mitad VIVA de la cláusula.
   *
   * La fila `Baja` NO es una fila real del listado: un tenant en `Baja` está borrado lógicamente y
   * `GET /plataforma/tenants` no lo devuelve, así que esa rama es defensa en profundidad y el
   * fixture existe solo para ejercitarla. Se mantiene por si el listado dejara de filtrarlos.
   */
  it('marca en la etiqueta a los tenants que no están activos, sin sacarlos de la lista', () => {
    const opciones = opcionesDeTenantAsignable([
      tenant(2, 'Comercio Sur'),
      tenant(3, 'Almacén Este', 'Suspendido'),
      tenant(4, 'Kiosco Viejo', 'Baja'),
    ])

    expect(opciones).toEqual([
      { valor: '3', etiqueta: 'Almacén Este (suspendido)' },
      { valor: '2', etiqueta: 'Comercio Sur' },
      { valor: '4', etiqueta: 'Kiosco Viejo (baja)' },
    ])
  })

  it('desempata con el id a dos tenants asignables que comparten nombre', () => {
    const opciones = opcionesDeTenantAsignable([tenant(2, 'Comercio Sur'), tenant(3, 'Comercio Sur')])

    expect(opciones.map((o) => o.etiqueta)).toEqual(['Comercio Sur (tenant 2)', 'Comercio Sur (tenant 3)'])
  })
})

describe('seleccionVigente', () => {
  const opciones = [
    { valor: '2', etiqueta: 'Comercio Sur' },
    { valor: '3', etiqueta: 'Almacén Este' },
  ]

  it('respeta una selección que sigue estando entre las opciones', () => {
    expect(seleccionVigente(opciones, '3')).toBe('3')
  })

  it('apaga una selección que ya no está entre las opciones', () => {
    expect(seleccionVigente(opciones, '9')).toBe(SIN_FILTRO)
  })

  it('deja pasar el "sin filtro" sin buscarlo entre las opciones', () => {
    expect(seleccionVigente([], SIN_FILTRO)).toBe(SIN_FILTRO)
  })
})

describe('opcionesDeEmpresa', () => {
  const filas = [
    filaPv(1, 'Tenant Uno', 10, 'Este SRL'),
    filaPv(1, 'Tenant Uno', 11, 'Anexo SA'),
    filaPv(2, 'Tenant Dos', 20, 'Sur SRL'),
    filaPv(2, 'Tenant Dos', 20, 'Sur SRL'),
  ]

  it('sin tenant seleccionado ofrece todas las empresas, deduplicadas y ordenadas', () => {
    expect(opcionesDeEmpresa(filas)).toEqual([
      { valor: '11', etiqueta: 'Anexo SA' },
      { valor: '10', etiqueta: 'Este SRL' },
      { valor: '20', etiqueta: 'Sur SRL' },
    ])
  })

  /** Cláusula bajo prueba: el angostamiento por tenant de design D15 — elegir un tenant saca del
   * `<select>` de empresa a las empresas de los demás. */
  it('angosta las opciones al tenant seleccionado', () => {
    expect(opcionesDeEmpresa(filas, '1').map((o) => o.valor)).toEqual(['11', '10'])
    expect(opcionesDeEmpresa(filas, '2').map((o) => o.valor)).toEqual(['20'])
  })

  it('desempata las empresas sin razón social con su id', () => {
    const opciones = opcionesDeEmpresa([filaPv(1, 'Tenant Uno', 30, null), filaPv(1, 'Tenant Uno', 31, null)])

    expect(opciones.map((o) => o.valor).sort()).toEqual(['30', '31'])
    expect(new Set(opciones.map((o) => o.etiqueta)).size).toBe(2)
  })

  /** Cláusula bajo prueba: `desempatarHomonimos` en `opcionesDeEmpresa` — la misma de
   * `opcionesDeTenant`, sobre la razón social, que también es texto libre. */
  it('desempata con el id a dos empresas distintas que comparten razón social', () => {
    const opciones = opcionesDeEmpresa([
      filaPv(1, 'Tenant Uno', 30, 'Sur SRL'),
      filaPv(1, 'Tenant Uno', 31, 'Sur SRL'),
    ])

    expect(opciones).toEqual([
      { valor: '30', etiqueta: 'Sur SRL (empresa 30)' },
      { valor: '31', etiqueta: 'Sur SRL (empresa 31)' },
    ])
  })
})

describe('filtrarPorTenant', () => {
  const filas = [fila(1, 'Uno'), fila(2, 'Dos'), fila(null, null), fila(1, 'Uno')]

  it('sin selección devuelve la lista entera (identidad)', () => {
    expect(filtrarPorTenant(filas, SIN_FILTRO)).toEqual(filas)
  })

  it('con un id devuelve solo las filas de ese tenant', () => {
    expect(filtrarPorTenant(filas, '1')).toEqual([fila(1, 'Uno'), fila(1, 'Uno')])
  })

  /** Cláusula bajo prueba: la rama `VALOR_SIN_TENANT` compara contra `null`, no contra
   * `Number('sin-tenant')` — que sería `NaN` y no coincidiría con ninguna fila. */
  it('con el token de plataforma devuelve solo las filas sin tenant', () => {
    expect(filtrarPorTenant(filas, VALOR_SIN_TENANT)).toEqual([fila(null, null)])
  })

  it('no muta la lista original', () => {
    const original = [...filas]
    filtrarPorTenant(filas, '1')

    expect(filas).toEqual(original)
  })
})

describe('filtrarPorEmpresa', () => {
  const filas = [{ idEmpresa: 10 }, { idEmpresa: 11 }, { idEmpresa: 10 }]

  it('sin selección devuelve la lista entera (identidad)', () => {
    expect(filtrarPorEmpresa(filas, SIN_FILTRO)).toEqual(filas)
  })

  it('con un id devuelve solo las filas de esa empresa', () => {
    expect(filtrarPorEmpresa(filas, '10')).toEqual([{ idEmpresa: 10 }, { idEmpresa: 10 }])
  })
})

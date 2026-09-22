import { beforeEach, describe, expect, it, vi } from 'vitest'
import {
  CLAVE_SESION_DE_DISPOSITIVO_LOCAL,
  guardarSesionDeDispositivoLocal,
  leerSesionDeDispositivoLocal,
} from './sesionDeDispositivoLocal'
import type { DispositivoActual } from '../api/dispositivos'
import type { PuntoVentaListado, UsuarioAutenticado } from '../api/tipos'

const DISPOSITIVO: DispositivoActual = {
  id: 1,
  nombre: 'Caja 1',
  idPuntoVenta: 7,
  puntoVenta: { numero: 1, nombre: 'Local Centro' },
  empresa: { nombre: 'Almacén Demo' },
}

const USUARIO: UsuarioAutenticado = {
  id: 4,
  usuario: 'jperez',
  mail: 'jperez@ways.test',
  rolId: 4,
  rol: 'Vendedor',
  ultimaConexion: null,
  idTenant: 1,
}

const PUNTO_VENTA: PuntoVentaListado = {
  id: 7,
  idTenant: 1,
  idEmpresa: 3,
  nombre: 'Local Centro',
  domicilio: null,
  horario: null,
  whatsapp: null,
  instagram: null,
  facebook: null,
  web: null,
  nombreTenant: 'Tenant Demo',
  razonSocialEmpresa: 'Empresa Demo',
  modo: 'Web',
}

beforeEach(() => {
  localStorage.clear()
})

describe('leerSesionDeDispositivoLocal', () => {
  it('devuelve null si no hay nada guardado', () => {
    expect(leerSesionDeDispositivoLocal()).toBeNull()
  })

  it('round-trip: lo que guardarSesionDeDispositivoLocal persiste, leerSesionDeDispositivoLocal lo reconstruye igual', () => {
    guardarSesionDeDispositivoLocal({ dispositivo: DISPOSITIVO, usuario: USUARIO, puntoVenta: PUNTO_VENTA })

    expect(leerSesionDeDispositivoLocal()).toEqual({ dispositivo: DISPOSITIVO, usuario: USUARIO, puntoVenta: PUNTO_VENTA })
  })

  it('devuelve null (nunca lanza) con JSON corrupto en la clave', () => {
    localStorage.setItem(CLAVE_SESION_DE_DISPOSITIVO_LOCAL, '{esto no es json')

    expect(leerSesionDeDispositivoLocal()).toBeNull()
  })

  /** Cláusula bajo prueba: el chequeo `!guardada.dispositivo || !guardada.usuario ||
   * !guardada.puntoVenta` — a cualquiera de los tres le falta, se trata como "no hay nada", nunca
   * un shell a medio construir. Mutación probada a mano: comentar esa condición hace que este
   * test falle (el `null` faltante pasaría derecho) — revertido, vuelve a pasar. */
  it('devuelve null si al JSON guardado le falta cualquiera de los tres campos', () => {
    localStorage.setItem(CLAVE_SESION_DE_DISPOSITIVO_LOCAL, JSON.stringify({ dispositivo: DISPOSITIVO, usuario: USUARIO }))
    expect(leerSesionDeDispositivoLocal()).toBeNull()

    localStorage.setItem(CLAVE_SESION_DE_DISPOSITIVO_LOCAL, JSON.stringify({ usuario: USUARIO, puntoVenta: PUNTO_VENTA }))
    expect(leerSesionDeDispositivoLocal()).toBeNull()
  })

  it('nunca lanza aunque localStorage.getItem tire (modo privado/cuota agotada)', () => {
    const original = Storage.prototype.getItem
    Storage.prototype.getItem = vi.fn(() => {
      throw new Error('localStorage no disponible')
    })

    expect(() => leerSesionDeDispositivoLocal()).not.toThrow()
    expect(leerSesionDeDispositivoLocal()).toBeNull()

    Storage.prototype.getItem = original
  })
})

describe('guardarSesionDeDispositivoLocal', () => {
  it('nunca lanza aunque localStorage.setItem tire (modo privado/cuota agotada)', () => {
    const original = Storage.prototype.setItem
    Storage.prototype.setItem = vi.fn(() => {
      throw new Error('localStorage no disponible')
    })

    expect(() => guardarSesionDeDispositivoLocal({ dispositivo: DISPOSITIVO, usuario: USUARIO, puntoVenta: PUNTO_VENTA })).not.toThrow()

    Storage.prototype.setItem = original
  })

  it('un guardado nuevo reemplaza entero al anterior (nunca hace merge)', () => {
    guardarSesionDeDispositivoLocal({ dispositivo: DISPOSITIVO, usuario: USUARIO, puntoVenta: PUNTO_VENTA })
    const otroDispositivo: DispositivoActual = { ...DISPOSITIVO, id: 2, nombre: 'Caja 2' }
    guardarSesionDeDispositivoLocal({ dispositivo: otroDispositivo, usuario: USUARIO, puntoVenta: PUNTO_VENTA })

    expect(leerSesionDeDispositivoLocal()?.dispositivo).toEqual(otroDispositivo)
  })
})

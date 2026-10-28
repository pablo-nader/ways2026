import { describe, expect, it } from 'vitest'
import type { PuntoVentaListado } from '../../api/tipos'
import { aFormularioDePuntoVenta } from './puntoVentaFormulario'

function listado(sobrescribir: Partial<PuntoVentaListado> = {}): PuntoVentaListado {
  return {
    id: 100,
    idTenant: 2,
    idEmpresa: 20,
    nombre: 'PV Centro',
    domicilio: null,
    horario: null,
    whatsapp: null,
    instagram: null,
    facebook: null,
    web: null,
    nombreTenant: 'Comercio Sur',
    razonSocialEmpresa: 'Sur SRL',
    modo: 'Web',
    ...sobrescribir,
  }
}

describe('aFormularioDePuntoVenta', () => {
  it('los textos sin informar pasan a vacío y el envío ausente a falso', () => {
    expect(aFormularioDePuntoVenta(listado())).toEqual({
      id: 100,
      nombre: 'PV Centro',
      domicilio: '',
      horario: '',
      whatsapp: '',
      instagram: '',
      facebook: '',
      web: '',
      envioPorDefecto: false,
    })
  })

  it('conserva los valores informados', () => {
    const f = aFormularioDePuntoVenta(
      listado({ domicilio: 'Calle 1', horario: '9 a 18', whatsapp: '1155550000', web: 'https://x.test', envioPorDefecto: true }),
    )

    expect(f).toMatchObject({
      domicilio: 'Calle 1',
      horario: '9 a 18',
      whatsapp: '1155550000',
      web: 'https://x.test',
      envioPorDefecto: true,
    })
  })
})

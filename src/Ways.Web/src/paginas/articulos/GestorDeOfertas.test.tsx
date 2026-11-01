import { act, cleanup, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { ErrorApi } from '../../api/cliente'
import { ROL, type OfertaListado, type UsuarioAutenticado } from '../../api/tipos'
import { AuthContext } from '../../auth/AuthContext'

const listarMock = vi.fn()
const obtenerMock = vi.fn()
const crearMock = vi.fn()
const actualizarMock = vi.fn()
const eliminarMock = vi.fn()
const catalogosMock = vi.fn()

vi.mock('../../api/ofertas', async (importarOriginal) => {
  const original = await importarOriginal<typeof import('../../api/ofertas')>()

  return {
    ...original,
    clienteDeOfertas: {
      listar: (...args: unknown[]) => listarMock(...args),
      obtener: (...args: unknown[]) => obtenerMock(...args),
      crear: (...args: unknown[]) => crearMock(...args),
      actualizar: (...args: unknown[]) => actualizarMock(...args),
      eliminar: (...args: unknown[]) => eliminarMock(...args),
    },
  }
})

vi.mock('../ofertas/catalogos', () => ({
  cargarCatalogosDeOferta: (...args: unknown[]) => catalogosMock(...args),
}))

vi.mock('../../api/articulos', () => ({
  clienteDeArticulos: { obtener: vi.fn(), listar: vi.fn() },
}))

import { NOTA_DE_BAJA_LOGICA } from '../../componentes/ConfirmacionDeBaja'
import { GestorDeOfertas } from './GestorDeOfertas'

function oferta(sobrescribir: Partial<OfertaListado> = {}): OfertaListado {
  return {
    id: 1,
    nombre: 'Oferta',
    idEmpresa: null,
    idArticulo: null,
    idGrupo: null,
    idCategoria: null,
    idFamilia: null,
    fechaDesde: null,
    fechaHasta: null,
    horaDesde: null,
    horaHasta: null,
    diasSemana: [],
    cantidadMinima: null,
    soloMultiplos: false,
    precioUnitario: null,
    porcentaje: 10,
    importeFijo: null,
    prioridad: 0,
    acumulable: false,
    activo: true,
    disponibleOnline: true,
    idsListas: [],
    ...sobrescribir,
  }
}

const DIRECTA = oferta({ id: 10, nombre: 'Dos por uno', idArticulo: 7, porcentaje: 20, cantidadMinima: 2, soloMultiplos: true })
const DE_FAMILIA = oferta({ id: 11, nombre: 'Verano', idFamilia: 5, porcentaje: null, importeFijo: 150, activo: false })

function sesion(rolId: number | null) {
  const usuario: UsuarioAutenticado | null =
    rolId === null
      ? null
      : { id: 1, usuario: 'ana', mail: 'ana@comercio.test', rolId, rol: 'Rol', ultimaConexion: null, idTenant: 1 }

  return { usuario, cargando: false, iniciarSesion: vi.fn(), cerrarSesion: vi.fn() }
}

type Opciones = {
  rolId?: number | null
  familia?: { id: number; nombre: string | null } | null
  idArticulo?: number
  bloqueadoPorPadre?: boolean
  alDeEscribir?: (enCurso: boolean) => void
}

function elemento({
  rolId = ROL.Admin,
  familia = { id: 5, nombre: 'Sabores' },
  idArticulo = 7,
  bloqueadoPorPadre = false,
  alDeEscribir = () => {},
}: Opciones = {}) {
  return (
    <AuthContext.Provider value={sesion(rolId)}>
      <GestorDeOfertas
        idArticulo={idArticulo}
        nombreArticulo="Cola 2L"
        familia={familia}
        bloqueadoPorPadre={bloqueadoPorPadre}
        alDeEscribir={alDeEscribir}
      />
    </AuthContext.Provider>
  )
}

beforeEach(() => {
  listarMock.mockReset().mockResolvedValue([DIRECTA, DE_FAMILIA])
  obtenerMock.mockReset().mockImplementation(async (id: number) => (id === 10 ? DIRECTA : DE_FAMILIA))
  crearMock.mockReset().mockResolvedValue(oferta())
  actualizarMock.mockReset().mockResolvedValue(oferta())
  eliminarMock.mockReset().mockResolvedValue(undefined)
  catalogosMock.mockReset().mockResolvedValue({
    grupos: [],
    categorias: [],
    familias: [{ id: 5, nombre: 'Sabores', activo: true, cantidadArticulos: 3 }],
    empresas: [],
    listasPrecio: [],
    avisos: [],
  })
})

afterEach(() => {
  cleanup()
})

describe('GestorDeOfertas', () => {
  it('lista la oferta del artículo y la de su familia con alcance, beneficio, condición y estado', async () => {
    render(elemento())
    // Se espera el DATO (la oferta), no la tabla.
    await screen.findByText('Dos por uno')

    expect(listarMock).toHaveBeenCalledWith(false, 7)
    const filas = screen.getAllByRole('row').slice(1)
    expect(within(filas[0]).getByText('Este artículo')).toBeTruthy()
    expect(within(filas[0]).getByText('20% de descuento')).toBeTruthy()
    expect(within(filas[0]).getByText('Mínimo 2, solo múltiplos')).toBeTruthy()
    expect(within(filas[0]).getByText('Activa')).toBeTruthy()
    expect(within(filas[1]).getByText('Familia: Sabores')).toBeTruthy()
    expect(within(filas[1]).getByText('Inactiva')).toBeTruthy()
  })

  it('sin ofertas muestra el estado vacío', async () => {
    listarMock.mockResolvedValue([])
    render(elemento())

    expect(await screen.findByText('Este artículo ni su familia tienen ofertas.')).toBeTruthy()
  })

  it('sin ofertas y sin familia el estado vacío habla solo del artículo', async () => {
    listarMock.mockResolvedValue([])
    render(elemento({ familia: null }))

    expect(await screen.findByText('Este artículo no tiene ofertas.')).toBeTruthy()
  })

  it('si la lectura falla avisa con role="alert"', async () => {
    listarMock.mockRejectedValue(new Error('boom'))
    render(elemento())

    const alerta = await screen.findByRole('alert')
    expect(alerta.textContent).toBe('No se pudieron cargar las ofertas.')
  })

  it('no se muestra a quien no administra el catálogo y no lee nada', async () => {
    const { container } = render(elemento({ rolId: ROL.Vendedor }))

    expect(container.textContent).toBe('')
    expect(listarMock).not.toHaveBeenCalled()
  })

  it('el botón de la familia solo existe si el artículo tiene una', async () => {
    const { unmount } = render(elemento())
    await screen.findByText('Dos por uno')
    expect(screen.getByRole('button', { name: 'Nueva oferta para la familia Sabores' })).toBeTruthy()
    unmount()

    render(elemento({ familia: null }))
    await screen.findByText('Dos por uno')
    expect(screen.queryByRole('button', { name: /Nueva oferta para la familia/ })).toBeNull()
    expect(screen.getByRole('button', { name: 'Nueva oferta para este artículo' })).toBeTruthy()
  })

  it('editar abre el modal con el detalle de la oferta', async () => {
    const usuario = userEvent.setup()
    render(elemento())
    await screen.findByText('Dos por uno')

    await usuario.click(screen.getByRole('button', { name: 'Editar oferta Dos por uno' }))

    expect(await screen.findByDisplayValue('Dos por uno')).toBeTruthy()
    expect(obtenerMock).toHaveBeenCalledWith(10)
    expect(screen.getByText('Editar oferta', { selector: '.modal-title, h5, h1, h2' })).toBeTruthy()
  })

  it('guardar una edición actualiza y relee la lista', async () => {
    const usuario = userEvent.setup()
    render(elemento())
    await screen.findByText('Dos por uno')
    await usuario.click(screen.getByRole('button', { name: 'Editar oferta Dos por uno' }))
    await screen.findByDisplayValue('Dos por uno')
    listarMock.mockResolvedValue([oferta({ id: 10, nombre: 'Renombrada', idArticulo: 7 })])

    await usuario.click(screen.getByRole('button', { name: 'Guardar' }))

    await screen.findByText('Renombrada')
    expect(actualizarMock).toHaveBeenCalledTimes(1)
    expect(actualizarMock.mock.calls[0][0]).toBe(10)
    expect(listarMock).toHaveBeenCalledTimes(2)
    expect(screen.queryByDisplayValue('Dos por uno')).toBeNull()
  })

  it('eliminar pide confirmación, llama al DELETE y relee la lista', async () => {
    const usuario = userEvent.setup()
    render(elemento())
    await screen.findByText('Dos por uno')
    listarMock.mockResolvedValue([DE_FAMILIA])

    await usuario.click(screen.getByRole('button', { name: 'Eliminar oferta Dos por uno' }))
    expect(eliminarMock).not.toHaveBeenCalled()
    await usuario.click(await screen.findByRole('button', { name: 'Confirmar baja' }))

    await waitFor(() => expect(screen.queryByText('Dos por uno')).toBeNull())
    expect(eliminarMock).toHaveBeenCalledWith(10)
    expect(listarMock).toHaveBeenCalledTimes(2)
    expect(screen.getByText('Verano')).toBeTruthy()
  })

  it('si el DELETE falla muestra el motivo y conserva la oferta', async () => {
    const usuario = userEvent.setup()
    eliminarMock.mockRejectedValue(new ErrorApi(409, 'oferta_en_uso', 'La oferta está en uso.'))
    render(elemento())
    await screen.findByText('Dos por uno')

    await usuario.click(screen.getByRole('button', { name: 'Eliminar oferta Dos por uno' }))
    await usuario.click(await screen.findByRole('button', { name: 'Confirmar baja' }))

    expect(await screen.findByText('La oferta está en uso.')).toBeTruthy()
    expect(listarMock).toHaveBeenCalledTimes(1)
  })

  it('"Nueva oferta para este artículo" manda el artículo en el alta', async () => {
    const usuario = userEvent.setup()
    render(elemento())
    await screen.findByText('Dos por uno')

    await usuario.click(screen.getByRole('button', { name: 'Nueva oferta para este artículo' }))
    await usuario.type(await screen.findByLabelText('Nombre'), 'Promo')
    await usuario.type(screen.getByLabelText('Porcentaje (%)'), '15')
    await usuario.click(screen.getByRole('button', { name: 'Guardar' }))

    await waitFor(() => expect(crearMock).toHaveBeenCalledTimes(1))
    expect(crearMock.mock.calls[0][0]).toMatchObject({ nombre: 'Promo', idArticulo: 7, idFamilia: null, porcentaje: 15 })
  })

  it('"Nueva oferta para la familia" manda la familia y no el artículo', async () => {
    const usuario = userEvent.setup()
    render(elemento())
    await screen.findByText('Dos por uno')

    await usuario.click(screen.getByRole('button', { name: 'Nueva oferta para la familia Sabores' }))
    await usuario.type(await screen.findByLabelText('Nombre'), 'Promo familia')
    await usuario.type(screen.getByLabelText('Porcentaje (%)'), '5')
    await usuario.click(screen.getByRole('button', { name: 'Guardar' }))

    await waitFor(() => expect(crearMock).toHaveBeenCalledTimes(1))
    expect(crearMock.mock.calls[0][0]).toMatchObject({ nombre: 'Promo familia', idArticulo: null, idFamilia: 5, porcentaje: 5 })
  })

  it('con el padre bloqueado ninguna acción está habilitada', async () => {
    render(elemento({ bloqueadoPorPadre: true }))
    await screen.findByText('Dos por uno')

    expect((screen.getByRole('button', { name: 'Nueva oferta para este artículo' }) as HTMLButtonElement).disabled).toBe(true)
    expect((screen.getByRole('button', { name: 'Editar oferta Dos por uno' }) as HTMLButtonElement).disabled).toBe(true)
    expect((screen.getByRole('button', { name: 'Eliminar oferta Dos por uno' }) as HTMLButtonElement).disabled).toBe(true)
  })

  it('una respuesta tardía de otro artículo no pisa la lista del actual', async () => {
    let resolverViejo: (lista: OfertaListado[]) => void = () => {}
    listarMock.mockImplementation((_incluir: boolean, idArticulo: number) =>
      idArticulo === 7
        ? new Promise<OfertaListado[]>((resolver) => {
            resolverViejo = resolver
          })
        : Promise.resolve([oferta({ id: 20, nombre: 'Del nuevo', idArticulo: 8 })]),
    )
    const { rerender } = render(elemento({ idArticulo: 7 }))

    rerender(elemento({ idArticulo: 8 }))
    await screen.findByText('Del nuevo')
    await act(async () => {
      resolverViejo([oferta({ id: 10, nombre: 'Del viejo', idArticulo: 7 })])
    })

    expect(screen.queryByText('Del viejo')).toBeNull()
    expect(screen.getByText('Del nuevo')).toBeTruthy()
  })

  describe('avisos de catálogos', () => {
    const CATALOGOS_VACIOS = { grupos: [], categorias: [], familias: [], empresas: [], listasPrecio: [] }

    it('muestra los avisos al abrir y los reintenta en la próxima apertura; sin avisos cachea', async () => {
      const usuario = userEvent.setup()
      catalogosMock
        .mockReset()
        .mockResolvedValueOnce({ ...CATALOGOS_VACIOS, avisos: ['No se pudieron cargar los grupos.'] })
        .mockResolvedValue({ ...CATALOGOS_VACIOS, avisos: [] })
      render(elemento())
      await screen.findByText('Dos por uno')

      await usuario.click(screen.getByRole('button', { name: 'Nueva oferta para este artículo' }))
      expect(await screen.findByText('No se pudieron cargar los grupos.')).toBeTruthy()
      await usuario.click(screen.getByRole('button', { name: 'Cancelar' }))
      // Cerrado el modal, el aviso sigue visible en la sección.
      expect(screen.getByText('No se pudieron cargar los grupos.')).toBeTruthy()

      await usuario.click(screen.getByRole('button', { name: 'Nueva oferta para este artículo' }))
      await screen.findByLabelText('Nombre')
      expect(catalogosMock).toHaveBeenCalledTimes(2)
      expect(screen.queryByText('No se pudieron cargar los grupos.')).toBeNull()
      await usuario.click(screen.getByRole('button', { name: 'Cancelar' }))

      await usuario.click(screen.getByRole('button', { name: 'Nueva oferta para este artículo' }))
      await screen.findByLabelText('Nombre')
      expect(catalogosMock).toHaveBeenCalledTimes(2)
    })
  })

  describe('señal alDeEscribir', () => {
    function diferido<T>() {
      let resolver: (valor: T) => void = () => {}
      const promesa = new Promise<T>((r) => {
        resolver = r
      })

      return { promesa, resolver }
    }

    it('queda levantada durante la carga de apertura y el modal, y se suelta al cancelar', async () => {
      const usuario = userEvent.setup()
      const alDeEscribir = vi.fn()
      const catalogos = diferido<unknown>()
      catalogosMock.mockReset().mockReturnValue(catalogos.promesa)
      render(elemento({ alDeEscribir }))
      await screen.findByText('Dos por uno')

      await usuario.click(screen.getByRole('button', { name: 'Nueva oferta para este artículo' }))
      expect(alDeEscribir.mock.calls).toEqual([[true]])

      await act(async () => {
        catalogos.resolver({ ...{ grupos: [], categorias: [], familias: [], empresas: [], listasPrecio: [] }, avisos: [] })
      })
      await screen.findByLabelText('Nombre')
      expect(alDeEscribir.mock.calls).toEqual([[true]])

      await usuario.click(screen.getByRole('button', { name: 'Cancelar' }))
      expect(alDeEscribir.mock.calls).toEqual([[true], [false]])
    })

    it('si la apertura falla se suelta', async () => {
      const usuario = userEvent.setup()
      const alDeEscribir = vi.fn()
      catalogosMock.mockReset().mockRejectedValue(new ErrorApi(500, 'x', 'Falló la carga.'))
      render(elemento({ alDeEscribir }))
      await screen.findByText('Dos por uno')

      await usuario.click(screen.getByRole('button', { name: 'Nueva oferta para este artículo' }))

      expect(await screen.findByText('Falló la carga.')).toBeTruthy()
      expect(alDeEscribir.mock.calls).toEqual([[true], [false]])
    })

    it('se sostiene durante la recarga posterior al guardado y se suelta cuando aterriza', async () => {
      const usuario = userEvent.setup()
      const alDeEscribir = vi.fn()
      render(elemento({ alDeEscribir }))
      await screen.findByText('Dos por uno')
      await usuario.click(screen.getByRole('button', { name: 'Editar oferta Dos por uno' }))
      await screen.findByDisplayValue('Dos por uno')
      const recarga = diferido<OfertaListado[]>()
      listarMock.mockReturnValue(recarga.promesa)

      await usuario.click(screen.getByRole('button', { name: 'Guardar' }))
      await waitFor(() => expect(listarMock).toHaveBeenCalledTimes(2))
      expect(alDeEscribir.mock.calls).toEqual([[true]])

      await act(async () => {
        recarga.resolver([oferta({ id: 10, nombre: 'Renombrada', idArticulo: 7 })])
      })
      await screen.findByText('Renombrada')
      expect(alDeEscribir.mock.calls).toEqual([[true], [false]])
    })

    it('si el guardado falla el modal sigue abierto y la señal levantada hasta cancelar', async () => {
      const usuario = userEvent.setup()
      const alDeEscribir = vi.fn()
      actualizarMock.mockRejectedValue(new Error('boom'))
      render(elemento({ alDeEscribir }))
      await screen.findByText('Dos por uno')
      await usuario.click(screen.getByRole('button', { name: 'Editar oferta Dos por uno' }))
      await screen.findByDisplayValue('Dos por uno')

      await usuario.click(screen.getByRole('button', { name: 'Guardar' }))
      await waitFor(() => expect(actualizarMock).toHaveBeenCalledTimes(1))
      expect(alDeEscribir.mock.calls).toEqual([[true]])

      await usuario.click(screen.getByRole('button', { name: 'Cancelar' }))
      expect(alDeEscribir.mock.calls).toEqual([[true], [false]])
    })

    it('la baja la levanta al abrir la confirmación y la suelta tras la recarga', async () => {
      const usuario = userEvent.setup()
      const alDeEscribir = vi.fn()
      render(elemento({ alDeEscribir }))
      await screen.findByText('Dos por uno')

      await usuario.click(screen.getByRole('button', { name: 'Eliminar oferta Dos por uno' }))
      expect(alDeEscribir.mock.calls).toEqual([[true]])
      const recarga = diferido<OfertaListado[]>()
      listarMock.mockReturnValue(recarga.promesa)
      await usuario.click(await screen.findByRole('button', { name: 'Confirmar baja' }))
      await waitFor(() => expect(listarMock).toHaveBeenCalledTimes(2))
      expect(alDeEscribir.mock.calls).toEqual([[true]])

      await act(async () => {
        recarga.resolver([DE_FAMILIA])
      })
      await waitFor(() => expect(screen.queryByText('Dos por uno')).toBeNull())
      expect(alDeEscribir.mock.calls).toEqual([[true], [false]])
    })

    it('al desmontar con el modal abierto se suelta una sola vez', async () => {
      const usuario = userEvent.setup()
      const alDeEscribir = vi.fn()
      const { unmount } = render(elemento({ alDeEscribir }))
      await screen.findByText('Dos por uno')
      await usuario.click(screen.getByRole('button', { name: 'Nueva oferta para este artículo' }))
      await screen.findByLabelText('Nombre')

      unmount()

      expect(alDeEscribir.mock.calls).toEqual([[true], [false]])
    })

    it('al desmontar con la apertura en vuelo se suelta y la respuesta tardía no vuelve a levantar', async () => {
      const usuario = userEvent.setup()
      const alDeEscribir = vi.fn()
      const catalogos = diferido<unknown>()
      catalogosMock.mockReset().mockReturnValue(catalogos.promesa)
      const { unmount } = render(elemento({ alDeEscribir }))
      await screen.findByText('Dos por uno')
      await usuario.click(screen.getByRole('button', { name: 'Nueva oferta para este artículo' }))

      unmount()
      await act(async () => {
        catalogos.resolver({ grupos: [], categorias: [], familias: [], empresas: [], listasPrecio: [], avisos: [] })
      })

      expect(alDeEscribir.mock.calls).toEqual([[true], [false]])
    })
  })

  it('relee la lista cuando cambia la familia del artículo', async () => {
    const { rerender } = render(elemento({ familia: { id: 5, nombre: 'Sabores' } }))
    await screen.findByText('Dos por uno')
    expect(listarMock).toHaveBeenCalledTimes(1)

    rerender(elemento({ familia: { id: 6, nombre: 'Otra' } }))

    await waitFor(() => expect(listarMock).toHaveBeenCalledTimes(2))
    await screen.findByText('Dos por uno')
  })

  describe('oferta de familia', () => {
    const AVISO = 'Es una oferta de la familia Sabores: el cambio afecta a todos sus artículos.'

    it('el modal de edición avisa que afecta a todos los artículos de la familia', async () => {
      const usuario = userEvent.setup()
      render(elemento())
      await screen.findByText('Verano')

      await usuario.click(screen.getByRole('button', { name: 'Editar oferta Verano' }))

      expect(await screen.findByText(AVISO)).toBeTruthy()
    })

    it('la confirmación de baja avisa lo mismo; una oferta del artículo no', async () => {
      const usuario = userEvent.setup()
      render(elemento())
      await screen.findByText('Verano')

      await usuario.click(screen.getByRole('button', { name: 'Eliminar oferta Verano' }))
      expect(await screen.findByText(`${AVISO} ${NOTA_DE_BAJA_LOGICA}`)).toBeTruthy()
      await usuario.click(screen.getByRole('button', { name: 'Cancelar' }))
      await waitFor(() => expect(screen.queryByRole('button', { name: 'Confirmar baja' })).toBeNull())

      await usuario.click(screen.getByRole('button', { name: 'Eliminar oferta Dos por uno' }))
      await screen.findByRole('button', { name: 'Confirmar baja' })
      expect(screen.getByText(NOTA_DE_BAJA_LOGICA)).toBeTruthy()
      expect(screen.queryByText(AVISO, { exact: false })).toBeNull()
    })
  })
})

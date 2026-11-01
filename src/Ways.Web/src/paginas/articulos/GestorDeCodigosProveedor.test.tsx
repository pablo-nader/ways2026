import { act, cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { ErrorApi } from '../../api/cliente'
import type { CodigoProveedorListado, ProveedorListado } from '../../api/tipos'

const codigosProveedorMock = vi.fn()
const editarMock = vi.fn()
const eliminarMock = vi.fn()
const asociarMock = vi.fn()

vi.mock('../../api/articulos', () => ({
  clienteDeArticulos: {
    codigosProveedor: (...args: unknown[]) => codigosProveedorMock(...args),
    editarCodigoProveedor: (...args: unknown[]) => editarMock(...args),
    eliminarCodigoProveedor: (...args: unknown[]) => eliminarMock(...args),
    asociarCodigoProveedor: (...args: unknown[]) => asociarMock(...args),
  },
}))

import { GestorDeCodigosProveedor } from './GestorDeCodigosProveedor'

function proveedor(id: number, razonSocial: string): ProveedorListado {
  return { id, razonSocial, nombreFantasia: null } as ProveedorListado
}

const PROVEEDORES = [proveedor(1, 'Acme SA'), proveedor(2, 'Beta SRL')]

function codigo(idCodigoProveedor: number, idProveedor: number, texto: string): CodigoProveedorListado {
  return { idCodigoProveedor, idArticulo: 7, idProveedor, codigo: texto }
}

const INICIALES = [codigo(10, 1, 'AC-1'), codigo(11, 9, 'XX-9')]

function montar(props: { idProveedorHabitual?: number | ''; bloqueadoPorPadre?: boolean; alDeEscribir?: (b: boolean) => void } = {}) {
  return render(
    <GestorDeCodigosProveedor
      idArticulo={7}
      idProveedorHabitual={props.idProveedorHabitual ?? ''}
      proveedores={PROVEEDORES}
      bloqueadoPorPadre={props.bloqueadoPorPadre ?? false}
      alDeEscribir={props.alDeEscribir ?? (() => {})}
    />,
  )
}

async function esperarLaTabla() {
  // Se espera el DATO (el código), no la tabla, que aparece recién con la lista cargada.
  await screen.findByText('AC-1')
}

beforeEach(() => {
  codigosProveedorMock.mockReset().mockResolvedValue(INICIALES)
  editarMock.mockReset()
  eliminarMock.mockReset()
  asociarMock.mockReset()
})

afterEach(() => {
  cleanup()
})

describe('GestorDeCodigosProveedor', () => {
  it('lista los códigos con el nombre del proveedor o "(no disponible)"', async () => {
    montar()
    await esperarLaTabla()

    const filas = screen.getAllByRole('row').slice(1)
    expect(within(filas[0]).getByText('Acme SA')).toBeTruthy()
    expect(within(filas[1]).getByText('(no disponible)')).toBeTruthy()
    expect(within(filas[1]).getByText('XX-9')).toBeTruthy()
    expect(codigosProveedorMock).toHaveBeenCalledExactlyOnceWith(7)
  })

  it('edita un código con Enter, manda el recortado y recarga la lista', async () => {
    const user = userEvent.setup()
    editarMock.mockResolvedValue(codigo(10, 1, 'AC-2'))
    montar()
    await esperarLaTabla()
    codigosProveedorMock.mockResolvedValue([codigo(10, 1, 'AC-2'), INICIALES[1]])

    await user.click(screen.getByRole('button', { name: 'Editar código AC-1' }))
    const campo = screen.getByLabelText('Código de proveedor')
    await user.clear(campo)
    await user.type(campo, '  AC-2  {Enter}')

    await screen.findByText('AC-2')
    expect(editarMock).toHaveBeenCalledExactlyOnceWith(7, 10, 'AC-2')
    expect(screen.queryByLabelText('Código de proveedor')).toBeNull()
  })

  it('muestra el mensaje del 409 y deja la edición abierta', async () => {
    const user = userEvent.setup()
    editarMock.mockRejectedValue(new ErrorApi(409, 'codigo_proveedor_duplicado', 'dup'))
    montar()
    await esperarLaTabla()

    await user.click(screen.getByRole('button', { name: 'Editar código AC-1' }))
    const campo = screen.getByLabelText('Código de proveedor')
    await user.clear(campo)
    await user.type(campo, 'ZZ-1')
    await user.click(screen.getByRole('button', { name: 'Guardar' }))

    expect(await screen.findByText('Ese código ya lo tiene otro artículo de este proveedor.')).toBeTruthy()
    expect(screen.getByLabelText('Código de proveedor')).toBeTruthy()
  })

  it('un código vacío no se envía y avisa que es obligatorio', async () => {
    const user = userEvent.setup()
    montar()
    await esperarLaTabla()

    await user.click(screen.getByRole('button', { name: 'Editar código AC-1' }))
    await user.clear(screen.getByLabelText('Código de proveedor'))
    await user.click(screen.getByRole('button', { name: 'Guardar' }))

    expect(screen.getByText('El código es obligatorio.')).toBeTruthy()
    expect(editarMock).not.toHaveBeenCalled()
  })

  it('Cancelar y Escape descartan la edición sin escribir', async () => {
    const user = userEvent.setup()
    montar()
    await esperarLaTabla()

    await user.click(screen.getByRole('button', { name: 'Editar código AC-1' }))
    await user.click(screen.getByRole('button', { name: 'Cancelar' }))
    expect(screen.queryByLabelText('Código de proveedor')).toBeNull()

    await user.click(screen.getByRole('button', { name: 'Editar código AC-1' }))
    await user.type(screen.getByLabelText('Código de proveedor'), 'x{Escape}')
    expect(screen.queryByLabelText('Código de proveedor')).toBeNull()
    expect(screen.getByText('AC-1')).toBeTruthy()
    expect(editarMock).not.toHaveBeenCalled()
  })

  it('elimina solo tras confirmar y recarga la lista', async () => {
    const user = userEvent.setup()
    eliminarMock.mockResolvedValue(undefined)
    montar()
    await esperarLaTabla()
    codigosProveedorMock.mockResolvedValue([INICIALES[1]])

    await user.click(screen.getByRole('button', { name: 'Eliminar código AC-1' }))
    expect(eliminarMock).not.toHaveBeenCalled()
    await user.click(screen.getByRole('button', { name: 'Sí, eliminar' }))

    await waitFor(() => expect(screen.queryByText('AC-1')).toBeNull())
    expect(eliminarMock).toHaveBeenCalledExactlyOnceWith(7, 10)
  })

  it('"No" cancela la baja', async () => {
    const user = userEvent.setup()
    montar()
    await esperarLaTabla()

    await user.click(screen.getByRole('button', { name: 'Eliminar código AC-1' }))
    await user.click(screen.getByRole('button', { name: 'No' }))

    expect(screen.queryByText('¿Eliminar este código?')).toBeNull()
    expect(eliminarMock).not.toHaveBeenCalled()
  })

  it('agrega con el proveedor habitual por defecto y recarga la lista', async () => {
    const user = userEvent.setup()
    asociarMock.mockResolvedValue(codigo(12, 2, 'NEW-1'))
    montar({ idProveedorHabitual: 2 })
    await esperarLaTabla()
    codigosProveedorMock.mockResolvedValue([...INICIALES, codigo(12, 2, 'NEW-1')])

    expect((screen.getByLabelText('Proveedor del código nuevo') as HTMLSelectElement).value).toBe('2')
    await user.type(screen.getByLabelText('Código de proveedor nuevo'), ' NEW-1 ')
    await user.click(screen.getByRole('button', { name: 'Agregar código' }))

    await screen.findByText('NEW-1')
    expect(asociarMock).toHaveBeenCalledExactlyOnceWith(7, { idProveedor: 2, codigo: 'NEW-1' })
    expect((screen.getByLabelText('Código de proveedor nuevo') as HTMLInputElement).value).toBe('')
  })

  it('no habilita Agregar sin proveedor o sin código', async () => {
    const user = userEvent.setup()
    montar()
    await esperarLaTabla()

    const agregar = screen.getByRole('button', { name: 'Agregar código' }) as HTMLButtonElement
    await user.type(screen.getByLabelText('Código de proveedor nuevo'), 'A')
    expect(agregar.disabled).toBe(true)
    await user.selectOptions(screen.getByLabelText('Proveedor del código nuevo'), '1')
    expect(agregar.disabled).toBe(false)
  })

  it('dos clics sincrónicos en Guardar mandan una sola request', async () => {
    const user = userEvent.setup()
    let resolver: (c: CodigoProveedorListado) => void = () => {}
    editarMock.mockReturnValue(new Promise<CodigoProveedorListado>((r) => (resolver = r)))
    montar()
    await esperarLaTabla()

    await user.click(screen.getByRole('button', { name: 'Editar código AC-1' }))
    const campo = screen.getByLabelText('Código de proveedor')
    await user.clear(campo)
    await user.type(campo, 'AC-3')
    const guardar = screen.getByRole('button', { name: 'Guardar' })
    act(() => {
      guardar.click()
      guardar.click()
    })

    expect(editarMock).toHaveBeenCalledTimes(1)
    await act(async () => resolver(codigo(10, 1, 'AC-3')))
  })

  it('durante la escritura avisa al padre y deshabilita la sección', async () => {
    const user = userEvent.setup()
    const alDeEscribir = vi.fn()
    let resolver: () => void = () => {}
    eliminarMock.mockReturnValue(new Promise<void>((r) => (resolver = r)))
    montar({ alDeEscribir })
    await esperarLaTabla()

    await user.click(screen.getByRole('button', { name: 'Eliminar código AC-1' }))
    await user.click(screen.getByRole('button', { name: 'Sí, eliminar' }))

    expect(alDeEscribir).toHaveBeenLastCalledWith(true)
    expect((screen.getByRole('button', { name: 'Agregar código' }) as HTMLButtonElement).disabled).toBe(true)
    expect((screen.getByRole('button', { name: 'Sí, eliminar' }) as HTMLButtonElement).disabled).toBe(true)
    await act(async () => resolver())
    await waitFor(() => expect(alDeEscribir).toHaveBeenLastCalledWith(false))
  })

  it('descarta la respuesta vieja de una lectura superada por la recarga', async () => {
    const user = userEvent.setup()
    let resolverLecturaVieja: (l: CodigoProveedorListado[]) => void = () => {}
    codigosProveedorMock.mockReset()
    codigosProveedorMock.mockReturnValueOnce(new Promise<CodigoProveedorListado[]>((r) => (resolverLecturaVieja = r)))
    asociarMock.mockResolvedValue(codigo(12, 1, 'NEW-1'))
    codigosProveedorMock.mockResolvedValueOnce([codigo(12, 1, 'NEW-1')])
    montar({ idProveedorHabitual: 1 })

    // La carga inicial sigue pendiente: se cuela un alta cuya recarga termina primero.
    await act(async () => {})
    fireEvent.change(screen.getByLabelText('Código de proveedor nuevo'), { target: { value: 'NEW-1' } })
    await user.click(screen.getByRole('button', { name: 'Agregar código' }))
    await screen.findByText('NEW-1')

    await act(async () => resolverLecturaVieja([codigo(10, 1, 'VIEJO-1')]))

    expect(screen.queryByText('VIEJO-1')).toBeNull()
    expect(screen.getByText('NEW-1')).toBeTruthy()
  })

  it('si falla la recarga tras escribir, no informa la escritura como fallida', async () => {
    const user = userEvent.setup()
    eliminarMock.mockResolvedValue(undefined)
    montar()
    await esperarLaTabla()
    codigosProveedorMock.mockRejectedValue(new Error('red'))

    await user.click(screen.getByRole('button', { name: 'Eliminar código AC-1' }))
    await user.click(screen.getByRole('button', { name: 'Sí, eliminar' }))

    expect(await screen.findByText('Se guardó, pero no se pudo actualizar la lista.')).toBeTruthy()
  })

  it('con el padre bloqueado, no deja operar', async () => {
    montar({ bloqueadoPorPadre: true })
    await esperarLaTabla()

    expect((screen.getByRole('button', { name: 'Editar código AC-1' }) as HTMLButtonElement).disabled).toBe(true)
    expect((screen.getByRole('button', { name: 'Agregar código' }) as HTMLButtonElement).disabled).toBe(true)
  })
})

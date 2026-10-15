import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it, vi } from 'vitest'
import type { ArticuloEscaneado, UnidadVenta } from '../api/tipos'
import type { ComboParaCargar } from '../pos/cargaDeCombos'
import { SelectorDeMiembrosDeCombo } from './SelectorDeMiembrosDeCombo'

function articulo(idArticulo: number, nombre: string, unidadVenta: UnidadVenta = 'Unidad'): ArticuloEscaneado {
  return { idArticulo, codigoInterno: `A${idArticulo}`, nombre, codigoBarra: null, cantidad: 1, acumulaEnVenta: true, unidadVenta }
}

const COCA = articulo(2, 'Coca')
const SPRITE = articulo(3, 'Sprite')
const PANCHO = articulo(1, 'Pancho')

function combo(extra: Partial<ComboParaCargar> = {}): ComboParaCargar {
  return {
    idCombo: 7,
    codigo: 12,
    nombre: 'Pancho + gaseosas',
    precio: 5000,
    componentes: [
      { idArticulo: 1, idFamilia: null, etiqueta: 'Pancho', cantidad: 1, candidatos: [PANCHO] },
      { idArticulo: null, idFamilia: 9, etiqueta: 'Gaseosas', cantidad: 2, candidatos: [COCA, SPRITE] },
    ],
    ...extra,
  }
}

function montar(c: ComboParaCargar = combo()) {
  const onConfirmar = vi.fn()
  const onCancelar = vi.fn()
  render(<SelectorDeMiembrosDeCombo combo={c} onConfirmar={onConfirmar} onCancelar={onCancelar} />)
  return { onConfirmar, onCancelar }
}

describe('SelectorDeMiembrosDeCombo', () => {
  it('pregunta solo por los componentes con varios miembros y lista los fijos aparte', () => {
    montar()

    expect(screen.getByText('Gaseosas: elegí 2')).toBeInTheDocument()
    expect(screen.getByLabelText('Coca')).toBeInTheDocument()
    expect(screen.getByLabelText('Sprite')).toBeInTheDocument()
    expect(screen.queryByLabelText('Pancho')).not.toBeInTheDocument()
    expect(screen.getByText(/También se agrega: Pancho × 1/)).toBeInTheDocument()
    expect(screen.getByRole('status')).toHaveTextContent('Elegidas 0 de 2')
  })

  it('el foco arranca en el primer miembro', () => {
    montar()

    expect(screen.getByLabelText('Coca')).toHaveFocus()
  })

  it('confirmar queda deshabilitado hasta que el total elegido es exactamente la cantidad', async () => {
    const usuario = userEvent.setup()
    montar()
    const confirmar = screen.getByRole('button', { name: 'Agregar combo' })
    const coca = screen.getByLabelText('Coca')
    const sprite = screen.getByLabelText('Sprite')

    expect(confirmar).toBeDisabled()
    await usuario.clear(coca)
    await usuario.type(coca, '1')
    expect(confirmar).toBeDisabled()
    expect(screen.getByRole('status')).toHaveTextContent('Elegidas 1 de 2')

    await usuario.clear(sprite)
    await usuario.type(sprite, '1')
    expect(confirmar).toBeEnabled()

    await usuario.clear(sprite)
    await usuario.type(sprite, '2')
    expect(confirmar).toBeDisabled()
    expect(screen.getByRole('status')).toHaveTextContent('Elegidas 3 de 2')
  })

  it('confirmar entrega las líneas de todo el combo: 1 Coca + 1 Sprite y el pancho fijo', async () => {
    const usuario = userEvent.setup()
    const { onConfirmar } = montar()

    await usuario.clear(screen.getByLabelText('Coca'))
    await usuario.type(screen.getByLabelText('Coca'), '1')
    await usuario.clear(screen.getByLabelText('Sprite'))
    await usuario.type(screen.getByLabelText('Sprite'), '1')
    await usuario.click(screen.getByRole('button', { name: 'Agregar combo' }))

    expect(onConfirmar).toHaveBeenCalledTimes(1)
    expect(onConfirmar).toHaveBeenCalledWith([
      { ...PANCHO, cantidad: 1 },
      { ...COCA, cantidad: 1 },
      { ...SPRITE, cantidad: 1 },
    ])
  })

  it('toda la cantidad en un miembro también vale, y Enter en un campo confirma', async () => {
    const usuario = userEvent.setup()
    const { onConfirmar } = montar()

    await usuario.clear(screen.getByLabelText('Coca'))
    await usuario.type(screen.getByLabelText('Coca'), '2{Enter}')

    expect(onConfirmar).toHaveBeenCalledWith([
      { ...PANCHO, cantidad: 1 },
      { ...COCA, cantidad: 2 },
    ])
  })

  it('Enter con el total incompleto no confirma', async () => {
    const usuario = userEvent.setup()
    const { onConfirmar } = montar()

    await usuario.clear(screen.getByLabelText('Coca'))
    await usuario.type(screen.getByLabelText('Coca'), '1{Enter}')

    expect(onConfirmar).not.toHaveBeenCalled()
  })

  it('un doble clic en confirmar entrega una sola vez', async () => {
    const usuario = userEvent.setup()
    const { onConfirmar } = montar()
    await usuario.clear(screen.getByLabelText('Coca'))
    await usuario.type(screen.getByLabelText('Coca'), '2')

    await usuario.dblClick(screen.getByRole('button', { name: 'Agregar combo' }))

    expect(onConfirmar).toHaveBeenCalledTimes(1)
  })

  it('cancelar, Escape y la × descartan el combo sin entregar nada', async () => {
    const usuario = userEvent.setup()
    const { onConfirmar, onCancelar } = montar()
    await usuario.clear(screen.getByLabelText('Coca'))
    await usuario.type(screen.getByLabelText('Coca'), '2')

    await usuario.click(screen.getByRole('button', { name: 'Cancelar' }))
    await usuario.keyboard('{Escape}')

    expect(onCancelar).toHaveBeenCalledTimes(2)
    expect(onConfirmar).not.toHaveBeenCalled()
  })

  describe('una familia de artículos pesables', () => {
    const JAMON = articulo(7, 'Jamón', 'Peso')
    const QUESO = articulo(8, 'Queso', 'Peso')
    const picada = () =>
      combo({
        componentes: [
          { idArticulo: null, idFamilia: 30, etiqueta: 'Fiambres', cantidad: 0.3, candidatos: [JAMON, QUESO] },
          { idArticulo: 1, idFamilia: null, etiqueta: 'Pancho', cantidad: 1, candidatos: [PANCHO] },
        ],
      })

    it('el campo de cada miembro acepta la granularidad de su unidad: milésimas en el pesable, enteros en el de unidad', () => {
      montar(
        combo({
          componentes: [{ idArticulo: null, idFamilia: 30, etiqueta: 'Picada', cantidad: 2, candidatos: [JAMON, COCA] }],
        }),
      )

      expect(screen.getByLabelText('Jamón')).toHaveAttribute('inputmode', 'decimal')
      expect(screen.getByLabelText('Coca')).toHaveAttribute('inputmode', 'numeric')
    })

    it.each(['0,25', '0.25'])('"%s" cierra un componente de 0,25 kg y habilita "Agregar combo"', async (texto) => {
      const usuario = userEvent.setup()
      const { onConfirmar } = montar(
        combo({
          componentes: [{ idArticulo: null, idFamilia: 30, etiqueta: 'Fiambres', cantidad: 0.25, candidatos: [JAMON, QUESO] }],
        }),
      )

      await usuario.clear(screen.getByLabelText('Jamón'))
      await usuario.type(screen.getByLabelText('Jamón'), texto)

      expect(screen.getByLabelText('Jamón')).toHaveValue(texto)
      expect(screen.getByRole('status')).toHaveTextContent('Elegidas 0,25 de 0,25')
      await usuario.click(screen.getByRole('button', { name: 'Agregar combo' }))
      expect(onConfirmar).toHaveBeenCalledWith([{ ...JAMON, cantidad: 0.25 }])
    })

    it('conserva la coma a mitad de tipeo ("0," no se pierde)', async () => {
      const usuario = userEvent.setup()
      montar(picada())

      await usuario.clear(screen.getByLabelText('Jamón'))
      await usuario.type(screen.getByLabelText('Jamón'), '0,')

      expect(screen.getByLabelText('Jamón')).toHaveValue('0,')
    })

    it('reparte kilos entre los miembros, compara el total exacto (0,1 + 0,2 = 0,3) y entrega las líneas en kilos', async () => {
      const usuario = userEvent.setup()
      const { onConfirmar } = montar(picada())
      const confirmar = screen.getByRole('button', { name: 'Agregar combo' })

      expect(screen.getByText('Fiambres: elegí 0,3')).toBeInTheDocument()
      await usuario.clear(screen.getByLabelText('Jamón'))
      await usuario.type(screen.getByLabelText('Jamón'), '0.1')
      expect(confirmar).toBeDisabled()
      expect(screen.getByRole('status')).toHaveTextContent('Elegidas 0,1 de 0,3')

      await usuario.clear(screen.getByLabelText('Queso'))
      await usuario.type(screen.getByLabelText('Queso'), '0.2')
      expect(screen.getByRole('status')).toHaveTextContent('Elegidas 0,3 de 0,3')
      expect(confirmar).toBeEnabled()

      await usuario.click(confirmar)
      expect(onConfirmar).toHaveBeenCalledWith([
        { ...JAMON, cantidad: 0.1 },
        { ...QUESO, cantidad: 0.2 },
        { ...PANCHO, cantidad: 1 },
      ])
      expect(screen.getByText(/También se agrega: Pancho × 1/)).toBeInTheDocument()
    })
  })

  it('varias familias se resuelven en un solo diálogo y se confirman juntas', async () => {
    const usuario = userEvent.setup()
    const FANTA = articulo(4, 'Fanta')
    const MOSTAZA = articulo(5, 'Mostaza')
    const KETCHUP = articulo(6, 'Ketchup')
    const { onConfirmar } = montar(
      combo({
        componentes: [
          { idArticulo: null, idFamilia: 9, etiqueta: 'Gaseosas', cantidad: 1, candidatos: [COCA, FANTA] },
          { idArticulo: null, idFamilia: 10, etiqueta: 'Salsas', cantidad: 1, candidatos: [MOSTAZA, KETCHUP] },
        ],
      }),
    )
    const confirmar = screen.getByRole('button', { name: 'Agregar combo' })

    await usuario.clear(screen.getByLabelText('Fanta'))
    await usuario.type(screen.getByLabelText('Fanta'), '1')
    expect(confirmar).toBeDisabled()
    await usuario.clear(screen.getByLabelText('Ketchup'))
    await usuario.type(screen.getByLabelText('Ketchup'), '1')

    expect(confirmar).toBeEnabled()
    await usuario.click(confirmar)
    expect(onConfirmar).toHaveBeenCalledWith([
      { ...FANTA, cantidad: 1 },
      { ...KETCHUP, cantidad: 1 },
    ])
  })
})

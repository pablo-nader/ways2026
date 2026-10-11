import {
  camposInvalidosDeLinea,
  cambiosAlEscribirCodigo,
  cambiosAlEscribirDetalle,
  cantidadDeLinea,
  diferenciaDePrecioPorBulto,
  lineaTieneDatos,
  mensajeDeCantidadDeBultos,
  tipoDeLinea,
  type LineaDeCompraFormulario,
  type UnidadDeMedidaDeLinea,
} from '../../api/lineaDeCompra'
import { esFraccionDeArticuloPorUnidad, MENSAJE_DE_CANTIDAD_ENTERA, restriccionDeCantidad } from '../../api/cantidadPorUnidad'
import type { AlicuotaIvaListado, ArticuloListado } from '../../api/tipos'
import { CampoFecha } from '../../componentes/CampoFecha'
import { CampoImporte } from '../../componentes/CampoImporte'
import { formatearImporte } from '../../formato/importes'
import { ComboboxDeArticulos } from './ComboboxDeArticulos'

const LARGO_MAXIMO_CODIGO_PROVEEDOR = 50
const PASO_DE_BULTOS = 0.01

type Props = {
  /** Posición de la fila en la grilla (desde 1): da nombre accesible propio a cada celda. */
  numero: number
  linea: LineaDeCompraFormulario
  alicuotas: AlicuotaIvaListado[]
  disabled: boolean
  idProveedor: number | null
  /** Pinta como inválidas las celdas que le faltan a una fila con datos (tras un intento de guardar). */
  marcarFaltantes: boolean
  /** Total de la línea ya calculado por la pantalla (misma cuenta que los totales del pie). */
  total: number
  consulta?: 'buscando' | 'error'
  onCambio: (clave: number, cambios: Partial<LineaDeCompraFormulario>) => void
  onCodigoSalida: (clave: number) => void
  onElegir: (clave: number, articulo: ArticuloListado) => void
  onQuitar: (clave: number) => void
  onCrear: (clave: number) => void
}

function pistaDelCodigo(linea: LineaDeCompraFormulario, idProveedor: number | null, consulta?: 'buscando' | 'error'): string {
  if (consulta === 'buscando') return 'Buscando…'
  if (consulta === 'error') return 'No se pudo buscar el código.'
  const codigo = linea.codigoProveedor.trim()
  if (codigo === '' || linea.resueltaPorCodigo) return ''
  if (idProveedor === null) return 'Elegí un proveedor para buscar el código.'
  if (linea.idArticulo !== '') return codigo === linea.codigoConsultado ? '' : 'Se asociará al artículo al confirmar.'
  return codigo === linea.codigoConsultado ? 'Pendiente de asociar' : ''
}

export function FilaDeGrilla({
  numero,
  linea,
  alicuotas,
  disabled,
  idProveedor,
  marcarFaltantes,
  total,
  consulta,
  onCambio,
  onCodigoSalida,
  onElegir,
  onQuitar,
  onCrear,
}: Props) {
  const tieneDatos = lineaTieneDatos(linea)
  const invalidos = camposInvalidosDeLinea(linea)
  const esConcepto = tipoDeLinea(linea) === 'concepto'
  const puedeBulto = !esConcepto && linea.unidadesPorBulto !== null
  const esBulto = !esConcepto && linea.unidadDeMedida === 'Bulto'
  const restriccion = restriccionDeCantidad(esConcepto || esBulto ? undefined : linea.unidadVenta)
  const fraccionEnUnidad = !esConcepto && !esBulto && esFraccionDeArticuloPorUnidad(linea.unidadVenta, linea.cantidad)
  const problemaDeBultos = mensajeDeCantidadDeBultos(linea)
  const diferenciaPorBulto = diferenciaDePrecioPorBulto(linea)
  const nombre = (celda: string) => `${celda}, línea ${numero}`
  const marca = (campo: (typeof invalidos)[number]) => marcarFaltantes && invalidos.includes(campo)
  const clase = (campo: (typeof invalidos)[number], extra = '') =>
    `form-control form-control-sm${extra ? ` ${extra}` : ''}${marca(campo) ? ' is-invalid' : ''}`
  const pista = pistaDelCodigo(linea, idProveedor, consulta)
  const mostrarLote = !esConcepto && linea.controlaLote
  const clave = linea.clave

  return (
    <>
      <tr data-fila={clave} className={tieneDatos && invalidos.length > 0 ? 'table-warning' : undefined}>
        <td style={{ minWidth: 130, width: 140 }}>
          <input
            type="text"
            className="form-control form-control-sm"
            aria-label={nombre('Código')}
            data-celda="codigo"
            maxLength={LARGO_MAXIMO_CODIGO_PROVEEDOR}
            autoComplete="off"
            value={linea.codigoProveedor}
            disabled={disabled}
            onChange={(e) => onCambio(clave, cambiosAlEscribirCodigo(linea, e.target.value))}
            onBlur={() => onCodigoSalida(clave)}
          />
          {pista && <div className="small text-muted">{pista}</div>}
        </td>
        <td style={{ minWidth: 260 }}>
          <ComboboxDeArticulos
            etiqueta={nombre('Detalle')}
            etiquetaDeCrear={nombre('Crear artículo')}
            texto={linea.descripcion}
            conArticulo={linea.idArticulo !== ''}
            soloLectura={linea.resueltaPorCodigo}
            disabled={disabled}
            idProveedor={idProveedor}
            invalido={marca('detalle')}
            onTexto={(texto) => onCambio(clave, cambiosAlEscribirDetalle(linea, texto))}
            onElegir={(articulo) => onElegir(clave, articulo)}
            onCrear={() => onCrear(clave)}
          />
        </td>
        <td style={{ width: 100 }}>
          <select
            className="form-select form-select-sm"
            aria-label={nombre('UM')}
            data-celda="um"
            value={linea.unidadDeMedida}
            disabled={disabled || !puedeBulto}
            onChange={(e) => onCambio(clave, { unidadDeMedida: e.target.value as UnidadDeMedidaDeLinea })}
          >
            <option value="Unidad">Unidad</option>
            <option value="Bulto" disabled={!puedeBulto}>
              Bulto
            </option>
          </select>
        </td>
        <td style={{ width: 110 }}>
          <input
            type="number"
            step={esBulto ? PASO_DE_BULTOS : restriccion.step}
            min={esBulto ? PASO_DE_BULTOS : restriccion.min}
            className={clase('cantidad', fraccionEnUnidad || problemaDeBultos !== null ? 'is-invalid' : '')}
            aria-label={nombre('Cantidad')}
            aria-invalid={marca('cantidad') || fraccionEnUnidad || problemaDeBultos !== null || undefined}
            data-celda="cantidad"
            value={linea.cantidad}
            disabled={disabled}
            onChange={(e) => onCambio(clave, { cantidad: e.target.value })}
          />
          {fraccionEnUnidad && <div className="invalid-feedback d-block">{MENSAJE_DE_CANTIDAD_ENTERA}</div>}
          {problemaDeBultos !== null && <div className="invalid-feedback d-block">{problemaDeBultos}</div>}
          {esBulto && linea.unidadesPorBulto !== null && !invalidos.includes('cantidad') && (
            <div className="small text-muted">
              = {cantidadDeLinea(0, Number(linea.cantidad), linea.unidadesPorBulto)} un. ({linea.unidadesPorBulto} por bulto)
            </div>
          )}
        </td>
        <td style={{ width: 130 }}>
          <CampoImporte
            className={clase('importe')}
            aria-label={nombre('Importe')}
            aria-invalid={marca('importe') || undefined}
            data-celda="importe"
            placeholder={esBulto ? 'por bulto' : undefined}
            decimales={4}
            valor={linea.importe}
            disabled={disabled}
            onChange={(v) => onCambio(clave, { importe: v })}
          />
          {diferenciaPorBulto && (
            <div className="small text-warning-emphasis" data-aviso="precio-por-bulto">
              El precio por bulto no se divide exacto entre {diferenciaPorBulto.unidadesPorBulto} unidades: la línea se registra por{' '}
              {formatearImporte(diferenciaPorBulto.registrado, { simbolo: true })} (diferencia de{' '}
              {formatearImporte(Math.abs(diferenciaPorBulto.diferencia), { simbolo: true })}).
            </div>
          )}
          {!esConcepto && !linea.actualizaCosto && <div className="small text-muted">No actualiza el costo del artículo</div>}
        </td>
        <td style={{ width: 100 }}>
          <CampoImporte
            className={clase('descuento')}
            aria-label={nombre('Descuento (%)')}
            aria-invalid={marca('descuento') || undefined}
            data-celda="descuento"
            placeholder="%"
            valor={linea.descuentoPorcentaje}
            disabled={disabled}
            onChange={(v) => onCambio(clave, { descuentoPorcentaje: v })}
          />
          {linea.descuentoPorcentaje !== null && linea.descuentoPorcentaje > 100 && (
            <div className="invalid-feedback d-block">No puede superar el 100 %.</div>
          )}
        </td>
        <td style={{ width: 110 }}>
          <select
            className={`form-select form-select-sm${marca('iva') ? ' is-invalid' : ''}`}
            aria-label={nombre('IVA')}
            aria-invalid={marca('iva') || undefined}
            data-celda="iva"
            value={linea.idAlicuotaIva}
            disabled={disabled}
            onChange={(e) => onCambio(clave, { idAlicuotaIva: e.target.value === '' ? '' : Number(e.target.value) })}
          >
            <option value="">Elegir…</option>
            {alicuotas.map((al) => (
              <option key={al.id} value={al.id}>
                {al.nombre}
              </option>
            ))}
          </select>
        </td>
        <td className="text-end" style={{ width: 120 }} data-celda="total">
          {tieneDatos ? formatearImporte(total, { simbolo: true }) : ''}
        </td>
        <td className="text-center" style={{ width: 44 }}>
          {tieneDatos && (
            <button
              type="button"
              className="btn btn-outline-danger btn-sm py-0 px-1"
              tabIndex={-1}
              aria-label={`Quitar línea ${numero}`}
              title="Quitar línea (Alt+Supr)"
              aria-keyshortcuts="Alt+Delete"
              disabled={disabled}
              onClick={() => onQuitar(clave)}
            >
              ×
            </button>
          )}
        </td>
      </tr>
      {mostrarLote && (
        <tr data-lote-de={clave} className={tieneDatos && invalidos.length > 0 ? 'table-warning' : undefined}>
          <td colSpan={9}>
            <div className="d-flex flex-wrap align-items-start gap-3 ps-3">
              <span className="small text-muted pt-1">Lote</span>
              <input
                type="text"
                className="form-control form-control-sm w-auto"
                aria-label={nombre('Código de lote')}
                data-celda="codigo-lote"
                placeholder="Código de lote (opcional)"
                value={linea.codigoLote}
                disabled={disabled}
                onChange={(e) => onCambio(clave, { codigoLote: e.target.value })}
              />
              <div>
                <CampoFecha
                  className={`form-control form-control-sm${linea.fechaVencimiento.trim() === '' ? ' is-invalid' : ''}`}
                  aria-label={nombre('Fecha de vencimiento')}
                  data-celda="vencimiento"
                  value={linea.fechaVencimiento}
                  disabled={disabled}
                  onChange={(valor) => onCambio(clave, { fechaVencimiento: valor })}
                />
                {linea.fechaVencimiento.trim() === '' && (
                  <div className="invalid-feedback d-block">
                    Este artículo controla lote — la fecha de vencimiento es obligatoria.
                  </div>
                )}
              </div>
            </div>
          </td>
        </tr>
      )}
    </>
  )
}

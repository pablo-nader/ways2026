import { useEffect, useRef, useState } from 'react'
import { clienteDeArticulos } from '../../api/articulos'
import { clienteDeCatalogo } from '../../api/catalogos'
import { ErrorApi } from '../../api/cliente'
import type { AlicuotaIvaListado, AreaAlta, AreaListado, ArticuloListado } from '../../api/tipos'
import { CampoImporte } from '../../componentes/CampoImporte'
import { Cargando } from '../../componentes/Cargando'
import { Modal } from '../../componentes/Modal'
import { elegirAlicuotaPorDefecto } from '../articulos/helpers'
import { costoDeListaDeAltaRapida } from './prefillAltaRapidaArticulo'

const clienteAreas = clienteDeCatalogo<AreaListado, AreaAlta>('areas')

const LARGO_MAXIMO_CODIGO_PROVEEDOR = 50

type Props = {
  /** Lo que ya se tipeó en la fila: el Detalle precarga el nombre y el Código, el código del proveedor. */
  nombreInicial: string
  codigoInicial: string
  idProveedor: number | null
  nombreProveedor: string
  alicuotas: AlicuotaIvaListado[]
  idAlicuotaIvaDeLaLinea: number | ''
  costoDeLaLinea: number | null
  /** El Importe de la línea es un precio neto de IVA (comprobante que discrimina IVA con precios netos):
   * el costo se guarda declarado sin IVA para que el servidor lo lleve a su costo nominal con IVA. */
  costoSinIva: boolean
  /** `codigoProveedor` es el que viajó al crear el artículo (el operador puede haberlo corregido acá). */
  onCreado: (articulo: ArticuloListado, codigoProveedor: string | null) => void
  onCancelar: () => void
}

/**
 * Alta rápida de un artículo desde una línea de compra. Solo pide lo que `AltaArticulo` exige
 * (nombre, área, alícuota) más el código del proveedor y el costo; el resto viaja con los defaults
 * de un alta en blanco del ABM. El costo se guarda como `costoLista`, el campo de costo que el ABM
 * de artículos edita de forma directa (el servidor calcula el `costoNominal` a partir de él); se declara
 * por unidad y con o sin IVA según `costoSinIva`.
 */
export function AltaRapidaArticuloDeCompra({
  nombreInicial,
  codigoInicial,
  idProveedor,
  nombreProveedor,
  alicuotas,
  idAlicuotaIvaDeLaLinea,
  costoDeLaLinea,
  costoSinIva,
  onCreado,
  onCancelar,
}: Props) {
  const alicuotasActivas = alicuotas.filter((a) => a.activo)

  const [areas, setAreas] = useState<AreaListado[] | null>(null)
  const [cargandoAreas, setCargandoAreas] = useState(true)
  const [errorAreas, setErrorAreas] = useState('')
  const [nombre, setNombre] = useState(nombreInicial)
  const [codigoProveedor, setCodigoProveedor] = useState(codigoInicial)
  const [idArea, setIdArea] = useState<number | ''>('')
  const [idAlicuotaIva, setIdAlicuotaIva] = useState<number | ''>(() =>
    idAlicuotaIvaDeLaLinea !== '' && alicuotasActivas.some((a) => a.id === idAlicuotaIvaDeLaLinea)
      ? idAlicuotaIvaDeLaLinea
      : elegirAlicuotaPorDefecto(alicuotasActivas),
  )
  const [costo, setCosto] = useState<number | null>(() => costoDeListaDeAltaRapida(costoDeLaLinea))
  const [guardando, setGuardando] = useState(false)
  const [error, setError] = useState('')
  const bloqueadoRef = useRef(false)
  // Token de la carga de áreas: "Reintentar" invalida la carga anterior en vuelo y el desmontaje
  // invalida la última (react-async-state regla 2/3).
  const tokenAreasRef = useRef(0)

  function cargarAreas() {
    const token = (tokenAreasRef.current += 1)
    setCargandoAreas(true)
    setErrorAreas('')
    clienteAreas
      .listar(false)
      .then((lista) => {
        if (tokenAreasRef.current !== token) return
        setAreas(lista)
      })
      .catch(() => {
        if (tokenAreasRef.current !== token) return
        setErrorAreas('No se pudieron cargar las áreas.')
      })
      .finally(() => {
        if (tokenAreasRef.current === token) setCargandoAreas(false)
      })
  }

  useEffect(() => {
    cargarAreas()
    return () => {
      tokenAreasRef.current += 1
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [])

  const sinProveedor = idProveedor === null
  const codigoLimpio = codigoProveedor.trim()
  const listoParaGuardar = nombre.trim() !== '' && idArea !== '' && idAlicuotaIva !== ''

  async function guardar(evento: React.FormEvent<HTMLFormElement>) {
    evento.preventDefault()
    evento.stopPropagation()
    if (bloqueadoRef.current || !listoParaGuardar) return
    bloqueadoRef.current = true
    setGuardando(true)
    setError('')
    try {
      const creado = await clienteDeArticulos.crear({
        codigoInterno: null,
        nombre: nombre.trim(),
        descripcion: null,
        idArea,
        idCategoria: null,
        idMarca: null,
        idGrupo: null,
        idProveedorHabitual: idProveedor,
        idAlicuotaIva,
        unidadVenta: 'Unidad',
        unidadesPorBulto: null,
        esProducto: true,
        costoLista: costo,
        costoListaPor: 'Unidad',
        costoListaIncluyeIva: !costoSinIva,
        descuentoProveedor: null,
        costoNominal: null,
        disponibleParaTodas: true,
        idsEmpresas: null,
        activo: true,
        controlaLote: false,
        codigoProveedor: sinProveedor || codigoLimpio === '' ? null : codigoLimpio,
      })
      onCreado(creado, sinProveedor || codigoLimpio === '' ? null : codigoLimpio)
    } catch (e) {
      setError(e instanceof ErrorApi ? e.message : 'No se pudo crear el artículo.')
    } finally {
      bloqueadoRef.current = false
      setGuardando(false)
    }
  }

  return (
    <Modal titulo="Nuevo artículo" ocupado={guardando} onCerrar={onCancelar}>
      <form onSubmit={guardar}>
        {errorAreas && (
          <div className="alert alert-warning py-1 px-2 small d-flex justify-content-between align-items-center gap-2">
            <span>{errorAreas}</span>
            <button
              type="button"
              className="btn btn-sm btn-outline-secondary"
              onClick={cargarAreas}
              disabled={cargandoAreas}
            >
              Reintentar
            </button>
          </div>
        )}
        {alicuotasActivas.length === 0 && (
          <div className="alert alert-warning py-1 px-2 small">No hay alícuotas de IVA disponibles.</div>
        )}
        {error && <div className="alert alert-danger py-1 px-2 small">{error}</div>}

        <div className="mb-3">
          <label className="form-label" htmlFor="alta-articulo-compra-nombre">
            Nombre
          </label>
          <input
            id="alta-articulo-compra-nombre"
            className="form-control"
            maxLength={150}
            value={nombre}
            disabled={guardando}
            onChange={(e) => setNombre(e.target.value)}
            autoFocus
            required
          />
        </div>

        <div className="mb-3">
          <label className="form-label" htmlFor="alta-articulo-compra-codigo-proveedor">
            Código del proveedor
          </label>
          <input
            id="alta-articulo-compra-codigo-proveedor"
            className="form-control"
            maxLength={LARGO_MAXIMO_CODIGO_PROVEEDOR}
            value={sinProveedor ? '' : codigoProveedor}
            disabled={guardando || sinProveedor}
            onChange={(e) => setCodigoProveedor(e.target.value)}
          />
          <div className="form-text">
            {sinProveedor
              ? 'Elegí un proveedor en la compra para poder cargar su código.'
              : `El código con el que ${nombreProveedor} identifica este artículo en su factura (opcional).`}
          </div>
        </div>

        <div className="mb-3">
          <label className="form-label" htmlFor="alta-articulo-compra-area">
            Área
          </label>
          {areas === null ? (
            errorAreas ? null : <Cargando texto="Cargando áreas…" />
          ) : (
            <select
              id="alta-articulo-compra-area"
              className="form-select"
              value={idArea}
              disabled={guardando}
              onChange={(e) => setIdArea(e.target.value === '' ? '' : Number(e.target.value))}
              required
            >
              <option value="" disabled>
                Elegir…
              </option>
              {areas.map((a) => (
                <option key={a.id} value={a.id}>
                  {a.nombre}
                </option>
              ))}
            </select>
          )}
        </div>

        <div className="mb-3">
          <label className="form-label" htmlFor="alta-articulo-compra-alicuota">
            Alícuota de IVA
          </label>
          <select
            id="alta-articulo-compra-alicuota"
            className="form-select"
            value={idAlicuotaIva}
            disabled={guardando}
            onChange={(e) => setIdAlicuotaIva(e.target.value === '' ? '' : Number(e.target.value))}
            required
          >
            <option value="" disabled>
              Elegir…
            </option>
            {alicuotasActivas.map((a) => (
              <option key={a.id} value={a.id}>
                {a.nombre}
              </option>
            ))}
          </select>
        </div>

        <div className="mb-3">
          <label className="form-label" htmlFor="alta-articulo-compra-costo">
            Costo de lista
          </label>
          <CampoImporte
            id="alta-articulo-compra-costo"
            className="form-control"
            valor={costo}
            disabled={guardando}
            onChange={setCosto}
          />
          {costoSinIva && <div className="form-text">Se guarda sin IVA, tal como se cargó en la compra.</div>}
        </div>

        <div className="d-flex gap-2">
          <button type="submit" className="btn btn-success" disabled={guardando || areas === null || !listoParaGuardar}>
            {guardando ? 'Creando…' : 'Crear'}
          </button>
          <button type="button" className="btn btn-outline-secondary" onClick={onCancelar} disabled={guardando}>
            Cancelar
          </button>
        </div>
      </form>
    </Modal>
  )
}

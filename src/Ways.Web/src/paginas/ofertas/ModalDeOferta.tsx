import { useRef, useState } from 'react'
import { clienteDeArticulos } from '../../api/articulos'
import { ErrorApi } from '../../api/cliente'
import {
  opcionesDeFamiliaDeOferta,
  opcionesDeLista,
  type AlcanceOferta,
  type BeneficioOferta,
  type FormularioOferta,
} from '../../api/ofertas'
import { DIAS_SEMANA } from '../../api/tipos'
import type {
  ArticuloListado,
  CategoriaListado,
  EmpresaListado,
  FamiliaListado,
  GrupoListado,
  ListaPrecioListado,
} from '../../api/tipos'
import { CampoImporte } from '../../componentes/CampoImporte'
import { CampoFecha } from '../../componentes/CampoFecha'
import { ErrorDeFormulario } from '../../componentes/ErrorDeFormulario'
import { Modal } from '../../componentes/Modal'
import { TituloDeSeccion } from '../../componentes/TituloDeSeccion'
import { atributosDeCampoDecimal, atributosDeCampoEntero } from '../../formato/numeros'

const ID_FORMULARIO_OFERTA = 'formulario-oferta'

export function ModalDeOferta({
  valor,
  grupos,
  categorias,
  familias,
  empresas,
  listasPrecio,
  guardando,
  ocupado,
  error,
  avisos = [],
  disparoDelError,
  focoDeReserva,
  onCambio,
  onGuardar,
  onCancelar,
}: {
  valor: FormularioOferta
  grupos: GrupoListado[]
  categorias: CategoriaListado[]
  familias: FamiliaListado[]
  empresas: EmpresaListado[]
  listasPrecio: ListaPrecioListado[]
  guardando: boolean
  ocupado: boolean
  error: string
  /** Aclaraciones fijas (alcance de la oferta, catálogos que no cargaron), visibles mientras el modal está abierto. */
  avisos?: readonly string[]
  disparoDelError: number
  focoDeReserva: React.RefObject<HTMLElement | null>
  onCambio: (f: FormularioOferta) => void
  onGuardar: () => void
  onCancelar: () => void
}) {
  const esNuevo = valor.id === null
  const opcionesLista = opcionesDeLista(listasPrecio)
  const opcionesFamilia = opcionesDeFamiliaDeOferta(familias)
  // Una familia que no cargó (o no está en el listado) no puede cambiar el valor de la oferta en silencio: la
  // opción actual se conserva rotulada como no disponible.
  const familiaFaltante = valor.idFamilia !== '' && !familias.some((f) => f.id === valor.idFamilia)

  function alternarDia(dia: number) {
    const yaEsta = valor.diasSemana.includes(dia)
    onCambio({ ...valor, diasSemana: yaEsta ? valor.diasSemana.filter((d) => d !== dia) : [...valor.diasSemana, dia] })
  }

  function alternarLista(id: number) {
    const yaEsta = valor.idsListas.includes(id)
    onCambio({ ...valor, idsListas: yaEsta ? valor.idsListas.filter((x) => x !== id) : [...valor.idsListas, id] })
  }

  const pie = (
    <>
      <button type="button" className="btn btn-outline-secondary" onClick={onCancelar} disabled={ocupado}>
        Cancelar
      </button>
      <button type="submit" form={ID_FORMULARIO_OFERTA} className="btn btn-success" disabled={ocupado}>
        {guardando ? 'Guardando…' : 'Guardar'}
      </button>
    </>
  )

  return (
    <Modal
      titulo={esNuevo ? 'Nueva oferta' : 'Editar oferta'}
      tamano="xl"
      desplazable
      ocupado={ocupado}
      focoDeReserva={focoDeReserva}
      pie={pie}
      onCerrar={onCancelar}
    >
      {avisos.map((aviso) => (
        <div key={aviso} className="alert alert-warning py-1 px-2 small">
          {aviso}
        </div>
      ))}
      <ErrorDeFormulario mensaje={error} disparo={disparoDelError} />
      <form
        id={ID_FORMULARIO_OFERTA}
        autoComplete="off"
        onSubmit={(e) => {
          e.preventDefault()
          if (ocupado) return
          onGuardar()
        }}
      >
        {/* fieldset disabled: cascada nativa a todos los controles anidados mientras hay un
            guardado en vuelo (react-async-state rule 5/9), mismo criterio que Articulos.tsx. */}
        <fieldset disabled={ocupado} className="row g-3 border-0 p-0 m-0">
          <div className="col-12">
            <TituloDeSeccion>Identificación</TituloDeSeccion>
          </div>

          <div className="col-md-5">
            <label className="form-label" htmlFor="of-nombre">
              Nombre
            </label>
            <input
              id="of-nombre"
              autoFocus
              className="form-control"
              maxLength={150}
              value={valor.nombre}
              onChange={(e) => onCambio({ ...valor, nombre: e.target.value })}
              required
            />
          </div>

          <div className="col-md-2">
            <label className="form-label" htmlFor="of-prioridad">
              Prioridad
            </label>
            <input
              id="of-prioridad"
              {...atributosDeCampoEntero()}
              className="form-control"
              value={valor.prioridad}
              onChange={(e) => onCambio({ ...valor, prioridad: e.target.value })}
            />
          </div>

          <div className="col-md-2 d-flex align-items-end">
            <div className="form-check">
              <input
                id="of-acumulable"
                type="checkbox"
                className="form-check-input"
                checked={valor.acumulable}
                onChange={(e) => onCambio({ ...valor, acumulable: e.target.checked })}
              />
              <label className="form-check-label" htmlFor="of-acumulable">
                Acumulable
              </label>
            </div>
          </div>

          <div className="col-md-3">
            <label className="form-label" htmlFor="of-empresa">
              Empresa
            </label>
            <select
              id="of-empresa"
              className="form-select"
              value={valor.idEmpresa}
              onChange={(e) => onCambio({ ...valor, idEmpresa: e.target.value === '' ? '' : Number(e.target.value) })}
            >
              <option value="">Todas las empresas</option>
              {empresas.map((e) => (
                <option key={e.id} value={e.id}>
                  {e.razonSocial}
                  {e.nombreFantasia ? ` (${e.nombreFantasia})` : ''}
                </option>
              ))}
            </select>
          </div>

          <div className="col-12">
            <TituloDeSeccion>Alcance (exactamente uno)</TituloDeSeccion>
          </div>

          <div className="col-12 d-flex gap-3">
            {(['Articulo', 'Grupo', 'Categoria', 'Familia'] as AlcanceOferta[]).map((opcion) => (
              <div className="form-check" key={opcion}>
                <input
                  id={`of-alcance-${opcion}`}
                  type="radio"
                  name="of-alcance"
                  className="form-check-input"
                  checked={valor.alcance === opcion}
                  onChange={() => onCambio({ ...valor, alcance: opcion })}
                />
                <label className="form-check-label" htmlFor={`of-alcance-${opcion}`}>
                  {opcion === 'Articulo' ? 'Artículo' : opcion === 'Grupo' ? 'Grupo' : opcion === 'Familia' ? 'Familia' : 'Categoría'}
                </label>
              </div>
            ))}
          </div>

          {valor.alcance === 'Articulo' && (
            <div className="col-12">
              <SelectorDeArticulo
                idArticulo={valor.idArticulo}
                nombreArticulo={valor.nombreArticulo}
                disabled={ocupado}
                onSeleccionar={(id, nombre) => onCambio({ ...valor, idArticulo: id, nombreArticulo: nombre })}
              />
            </div>
          )}

          {valor.alcance === 'Grupo' && (
            <div className="col-md-4">
              <label className="form-label" htmlFor="of-grupo">
                Grupo objetivo
              </label>
              <select
                id="of-grupo"
                className="form-select"
                value={valor.idGrupo}
                onChange={(e) => onCambio({ ...valor, idGrupo: e.target.value === '' ? '' : Number(e.target.value) })}
              >
                <option value="" disabled>
                  Elegir…
                </option>
                {grupos.map((g) => (
                  <option key={g.id} value={g.id}>
                    {g.nombre}
                  </option>
                ))}
              </select>
            </div>
          )}

          {valor.alcance === 'Categoria' && (
            <div className="col-md-4">
              <label className="form-label" htmlFor="of-categoria">
                Categoría objetivo
              </label>
              <select
                id="of-categoria"
                className="form-select"
                value={valor.idCategoria}
                onChange={(e) => onCambio({ ...valor, idCategoria: e.target.value === '' ? '' : Number(e.target.value) })}
              >
                <option value="" disabled>
                  Elegir…
                </option>
                {categorias.map((c) => (
                  <option key={c.id} value={c.id}>
                    {c.nombre}
                  </option>
                ))}
              </select>
            </div>
          )}

          {valor.alcance === 'Familia' && (
            <div className="col-md-4">
              <label className="form-label" htmlFor="of-familia">
                Familia objetivo
              </label>
              <select
                id="of-familia"
                className="form-select"
                value={valor.idFamilia}
                onChange={(e) => onCambio({ ...valor, idFamilia: e.target.value === '' ? '' : Number(e.target.value) })}
              >
                <option value="" disabled>
                  Elegir…
                </option>
                {familiaFaltante && <option value={valor.idFamilia}>Familia #{valor.idFamilia} (no disponible)</option>}
                {opcionesFamilia.map((o) => (
                  <option key={o.valor} value={o.valor}>
                    {o.etiqueta}
                  </option>
                ))}
              </select>
            </div>
          )}

          <div className="col-12">
            <TituloDeSeccion>Vigencia</TituloDeSeccion>
          </div>

          <div className="col-md-3">
            <label className="form-label" htmlFor="of-fecha-desde">
              Fecha desde
            </label>
            <CampoFecha
              id="of-fecha-desde"
              className="form-control"
              value={valor.fechaDesde}
              onChange={(fecha) => onCambio({ ...valor, fechaDesde: fecha })}
            />
          </div>
          <div className="col-md-3">
            <label className="form-label" htmlFor="of-fecha-hasta">
              Fecha hasta
            </label>
            <CampoFecha
              id="of-fecha-hasta"
              className="form-control"
              value={valor.fechaHasta}
              onChange={(fecha) => onCambio({ ...valor, fechaHasta: fecha })}
            />
          </div>
          <div className="col-md-3">
            <label className="form-label" htmlFor="of-hora-desde">
              Hora desde
            </label>
            <input
              id="of-hora-desde"
              type="time"
              className="form-control"
              value={valor.horaDesde}
              onChange={(e) => onCambio({ ...valor, horaDesde: e.target.value })}
            />
          </div>
          <div className="col-md-3">
            <label className="form-label" htmlFor="of-hora-hasta">
              Hora hasta
            </label>
            <input
              id="of-hora-hasta"
              type="time"
              className="form-control"
              value={valor.horaHasta}
              onChange={(e) => onCambio({ ...valor, horaHasta: e.target.value })}
            />
          </div>

          <div className="col-12">
            <div className="form-text mb-1">Días de la semana (sin marcar ninguno = todos los días).</div>
            <div className="d-flex flex-wrap gap-3">
              {DIAS_SEMANA.map((d) => (
                <div className="form-check" key={d.valor}>
                  <input
                    id={`of-dia-${d.valor}`}
                    type="checkbox"
                    className="form-check-input"
                    checked={valor.diasSemana.includes(d.valor)}
                    onChange={() => alternarDia(d.valor)}
                  />
                  <label className="form-check-label" htmlFor={`of-dia-${d.valor}`}>
                    {d.etiqueta}
                  </label>
                </div>
              ))}
            </div>
          </div>

          <div className="col-md-3">
            <label className="form-label" htmlFor="of-cantidad-minima">
              Cantidad mínima
            </label>
            <input
              id="of-cantidad-minima"
              {...atributosDeCampoDecimal({ decimales: 3 })}
              className="form-control"
              placeholder="Sin mínimo (oferta directa)"
              value={valor.cantidadMinima}
              onChange={(e) => {
                const cantidadMinima = e.target.value
                onCambio({ ...valor, cantidadMinima, soloMultiplos: cantidadMinima.trim() === '' ? false : valor.soloMultiplos })
              }}
            />
          </div>

          <div className="col-md-9 d-flex flex-column justify-content-end">
            <div className="form-check">
              <input
                id="of-solo-multiplos"
                type="checkbox"
                className="form-check-input"
                checked={valor.soloMultiplos}
                disabled={valor.cantidadMinima.trim() === ''}
                aria-describedby="of-solo-multiplos-ayuda"
                onChange={(e) => onCambio({ ...valor, soloMultiplos: e.target.checked })}
              />
              <label className="form-check-label" htmlFor="of-solo-multiplos">
                Solo múltiplos de la cantidad
              </label>
            </div>
            <div className="form-text" id="of-solo-multiplos-ayuda">
              Solo se rebajan los grupos completos de la cantidad mínima; el resto paga el precio normal. Ej.: 2 x $7.500, con 3 unidades =
              $7.500 + 1 a precio normal.
              {valor.cantidadMinima.trim() === '' && ' Requiere cargar una cantidad mínima.'}
            </div>
          </div>

          <div className="col-12">
            <TituloDeSeccion>Beneficio (exactamente uno)</TituloDeSeccion>
          </div>

          <div className="col-12 d-flex gap-3">
            {(['Porcentaje', 'ImporteFijo', 'PrecioUnitario'] as BeneficioOferta[]).map((opcion) => (
              <div className="form-check" key={opcion}>
                <input
                  id={`of-beneficio-${opcion}`}
                  type="radio"
                  name="of-beneficio"
                  className="form-check-input"
                  checked={valor.beneficio === opcion}
                  onChange={() => onCambio({ ...valor, beneficio: opcion })}
                />
                <label className="form-check-label" htmlFor={`of-beneficio-${opcion}`}>
                  {opcion === 'Porcentaje' ? 'Porcentaje' : opcion === 'ImporteFijo' ? 'Importe fijo por unidad' : 'Precio unitario'}
                </label>
              </div>
            ))}
          </div>

          {valor.beneficio === 'Porcentaje' && (
            <div className="col-md-3">
              <label className="form-label" htmlFor="of-porcentaje">
                Porcentaje (%)
              </label>
              <input
                id="of-porcentaje"
                {...atributosDeCampoDecimal({ decimales: 2 })}
                className="form-control"
                value={valor.porcentaje}
                onChange={(e) => onCambio({ ...valor, porcentaje: e.target.value })}
              />
            </div>
          )}

          {valor.beneficio === 'ImporteFijo' && (
            <div className="col-md-3">
              <label className="form-label" htmlFor="of-importe-fijo">
                Importe fijo por unidad ($)
              </label>
              <CampoImporte
                id="of-importe-fijo"
                className="form-control"
                valor={valor.importeFijo === '' ? null : Number(valor.importeFijo)}
                onChange={(n) => onCambio({ ...valor, importeFijo: n === null ? '' : String(n) })}
              />
            </div>
          )}

          {valor.beneficio === 'PrecioUnitario' && (
            <div className="col-md-3">
              <label className="form-label" htmlFor="of-precio-unitario">
                Precio unitario ($)
              </label>
              <CampoImporte
                id="of-precio-unitario"
                className="form-control"
                valor={valor.precioUnitario === '' ? null : Number(valor.precioUnitario)}
                onChange={(n) => onCambio({ ...valor, precioUnitario: n === null ? '' : String(n) })}
              />
            </div>
          )}

          <div className="col-12">
            <TituloDeSeccion>Listas de precio</TituloDeSeccion>
          </div>

          <div className="col-12">
            <div className="form-text mb-1">
              Sin marcar ninguna lista, la oferta aplica a <strong>todas</strong> las listas de precio de la cuenta.
            </div>
            <div className="d-flex flex-wrap gap-3">
              {opcionesLista.map((o) => (
                <div className="form-check" key={o.valor}>
                  <input
                    id={`of-lista-${o.valor}`}
                    type="checkbox"
                    className="form-check-input"
                    checked={valor.idsListas.includes(Number(o.valor))}
                    onChange={() => alternarLista(Number(o.valor))}
                  />
                  <label className="form-check-label" htmlFor={`of-lista-${o.valor}`}>
                    {o.etiqueta}
                  </label>
                </div>
              ))}
              {opcionesLista.length === 0 && <span className="text-muted small">No hay listas de precio activas.</span>}
            </div>
          </div>

          <div className="col-md-3 d-flex align-items-end">
            <div className="form-check">
              <input
                id="of-activo"
                type="checkbox"
                className="form-check-input"
                checked={valor.activo}
                onChange={(e) => onCambio({ ...valor, activo: e.target.checked })}
              />
              <label className="form-check-label" htmlFor="of-activo">
                Activa
              </label>
            </div>
          </div>

          <div className="col-md-3 d-flex align-items-end">
            <div className="form-check">
              <input
                id="of-disponible-online"
                type="checkbox"
                className="form-check-input"
                checked={valor.disponibleOnline}
                onChange={(e) => onCambio({ ...valor, disponibleOnline: e.target.checked })}
                aria-describedby="of-disponible-online-ayuda"
              />
              <label className="form-check-label" htmlFor="of-disponible-online">
                Disponible online
              </label>
              <div id="of-disponible-online-ayuda" className="form-text">
                Desmarcalo para que la tienda online no la aplique a los precios.
              </div>
            </div>
          </div>
        </fieldset>
      </form>
    </Modal>
  )
}

/**
 * Picker de artículo (design: "articulo search/select"): sin catálogo completo cargado en
 * memoria (podrían ser miles), busca contra `/api/articulos` con el mismo cliente que la
 * pantalla de artículos. Búsqueda generacional (react-async-state rule 2): una respuesta tardía
 * de una búsqueda anterior no puede pisar los resultados de la más reciente.
 */
function SelectorDeArticulo({
  idArticulo,
  nombreArticulo,
  disabled,
  onSeleccionar,
}: {
  idArticulo: number | ''
  nombreArticulo: string
  disabled: boolean
  onSeleccionar: (id: number, nombre: string) => void
}) {
  const [termino, setTermino] = useState('')
  const [resultados, setResultados] = useState<ArticuloListado[]>([])
  const [buscando, setBuscando] = useState(false)
  const [error, setError] = useState('')
  const generacionRef = useRef(0)

  async function buscar() {
    if (buscando || disabled) return
    const generacion = (generacionRef.current += 1)
    setBuscando(true)
    setError('')
    try {
      const pagina = await clienteDeArticulos.listar(termino, false)
      if (generacionRef.current !== generacion) return
      setResultados(pagina.items)
    } catch (e) {
      if (generacionRef.current === generacion) {
        setError(e instanceof ErrorApi ? e.message : 'No se pudieron buscar artículos.')
      }
    } finally {
      if (generacionRef.current === generacion) setBuscando(false)
    }
  }

  return (
    <div>
      <label className="form-label" htmlFor="of-buscar-articulo">
        Buscar artículo
      </label>
      {idArticulo !== '' && (
        <div className="mb-2">
          Seleccionado: <strong>{nombreArticulo || `Artículo #${idArticulo}`}</strong>
        </div>
      )}
      <div className="input-group mb-2" style={{ maxWidth: 420 }}>
        <input
          id="of-buscar-articulo"
          type="search"
          className="form-control"
          placeholder="Buscar por nombre o código interno…"
          value={termino}
          disabled={disabled}
          onChange={(e) => setTermino(e.target.value)}
          onKeyDown={(e) => e.key === 'Enter' && (e.preventDefault(), buscar())}
        />
        <button type="button" className="btn btn-outline-primary" disabled={disabled || buscando} onClick={buscar}>
          {buscando ? 'Buscando…' : 'Buscar'}
        </button>
      </div>
      {error && <div className="alert alert-danger py-1 px-2 small">{error}</div>}
      {resultados.length > 0 && (
        <select
          className="form-select"
          size={Math.min(resultados.length, 6)}
          disabled={disabled}
          value=""
          onChange={(e) => {
            const encontrado = resultados.find((a) => a.id === Number(e.target.value))
            if (encontrado) onSeleccionar(encontrado.id, encontrado.nombre)
          }}
        >
          <option value="" disabled>
            Elegí un resultado…
          </option>
          {resultados.map((a) => (
            <option key={a.id} value={a.id}>
              {a.codigoInterno} — {a.nombre}
            </option>
          ))}
        </select>
      )}
    </div>
  )
}

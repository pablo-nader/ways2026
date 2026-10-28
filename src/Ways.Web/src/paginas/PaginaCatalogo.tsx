import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { api, ErrorApi } from '../api/cliente'
import { copiaDeFalloDeBaja } from '../api/bajas'
import { columnasDeCatalogo, textoDeColumna } from '../api/catalogos'
import type { CampoDescriptor, DescriptorDeCatalogo, ValorDeCampo } from '../api/catalogos'
import type { CatalogoListado } from '../api/tipos'
import { Box } from '../componentes/Box'
import { Cargando } from '../componentes/Cargando'
import { ConfirmacionDeBaja } from '../componentes/ConfirmacionDeBaja'
import { etiquetaParaValorFaltante } from './etiquetaParaValorFaltante'
import { BotonIcono } from '../componentes/BotonIcono'
import { ErrorDeFormulario } from '../componentes/ErrorDeFormulario'
import { EncabezadoOrdenable, FilaDeFiltros, useFiltrosDeColumna, useOrdenamiento } from '../componentes/grilla'
import { Modal } from '../componentes/Modal'
import { atributosDeCampoDecimal, atributosDeCampoEntero } from '../formato/numeros'

type Formulario = {
  id: number | null
  nombre: string
  activo: boolean
  /** Empresa de la fila que se edita; null en un alta y en una fila de todo el tenant. */
  idEmpresa: number | null
  valores: Record<string, ValorDeCampo>
}

const ID_FORMULARIO_CATALOGO = 'formulario-catalogo'

/** Lectura aparte de las opciones de un select (`opcionesRemotas`), atada a la apertura del formulario que la pidió:
 * lo que llegó para una apertura anterior no se usa en la siguiente. */
type LecturaRemota = { apertura: number; porClave: Record<string, { datos: unknown; fallo: boolean }> }

const AVISO_REFRESCO_FALLIDO = 'Se guardó, pero no se pudo actualizar la vista. Recargá la pantalla.'
const AVISO_REFRESCO_FALLIDO_BAJA = 'Se eliminó, pero no se pudo actualizar la vista. Recargá la pantalla.'

/**
 * ABM genérico de un catálogo de tenant (ADR-11): el descriptor define qué campos propios
 * tiene además de `nombre`/`activo` (comunes a los 5) — esta pantalla no sabe nada de un
 * catálogo en particular. `categorias` no pasa por acá: es el escape hatch (árbol + regla de
 * profundidad, `Categorias.tsx`).
 *
 * La baja (fix/web-bajas-catalogos) sigue el mismo patrón que `Empresas.tsx`/`Tenants.tsx`
 * (`react-async-state` regla 10): puerta modal (`ConfirmacionDeBaja`), token de generación +
 * `ocupadoRef` de re-entrancia COMPARTIDOS entre el guardado y la baja —solo una escritura puede
 * estar en vuelo a la vez, sea alta/edición o baja— y `bloqueado` deja inerte toda la pantalla
 * mientras cualquiera de las dos está en vuelo.
 *
 * El alta y la edición viven en un `Modal` (mismo criterio que `Ofertas.tsx` y `Gastos.tsx`): un
 * guardado exitoso lo cierra y el aviso queda en la pantalla; el rechazo de un guardado se muestra
 * adentro (`errorFormulario`), porque un alert de la pantalla quedaría detrás del fondo. Los errores
 * de la carga del listado y de la baja siguen en la pantalla.
 */
function atributosDeCampoNumerico(campo: CampoDescriptor) {
  const decimales = (campo.paso ?? (campo.tipo === 'numeroDecimal' ? '0.01' : '1')).split('.')[1]?.length ?? 0
  return decimales === 0
    ? atributosDeCampoEntero({ permitirNegativo: true })
    : atributosDeCampoDecimal({ decimales, permitirNegativo: true })
}

export function PaginaCatalogo<TListado extends CatalogoListado, TAlta>({
  definicion,
}: {
  definicion: DescriptorDeCatalogo<TListado, TAlta>
}) {
  const [items, setItems] = useState<TListado[]>([])
  const [incluirInactivos, setIncluirInactivos] = useState(false)
  const [cargando, setCargando] = useState(true)
  const [error, setError] = useState('')
  const [aviso, setAviso] = useState('')
  const [errorFormulario, setErrorFormulario] = useState('')
  const [formulario, setFormulario] = useState<Formulario | null>(null)
  const [guardando, setGuardando] = useState(false)
  /** Baja pendiente de confirmación: ver `Empresas.tsx`. La puerta es MODAL —`bloqueado` deja
   * inerte el resto de la pantalla mientras está abierta— y NO acuña token: eso lo hace la
   * escritura, al confirmar. */
  const [baja, setBaja] = useState<TListado | null>(null)
  /** Id del item cuya baja está en vuelo (distinto de `guardando`, que cubre alta/edición). */
  const [ocupadoBaja, setOcupadoBaja] = useState<number | null>(null)
  /** Control que abrió la puerta, capturado en el `onClick` y no dentro de la puerta: ver
   * `ConfirmacionDeBaja.tsx`. */
  const [disparadorDeLaPuerta, setDisparadorDeLaPuerta] = useState<HTMLElement | null>(null)
  /** Destino de foco cuando la modal se cierra con la pantalla inerte (guardado en vuelo): ningún
   * botón es alcanzable en ese momento. */
  const contenedorRef = useRef<HTMLDivElement>(null)

  /** Contrato de invalidación compartido por guardar y por la baja: ver `Empresas.tsx`. */
  const generacion = useRef(0)
  /** Espejo síncrono de "hay una escritura en vuelo" (alta/edición O baja) — la ÚNICA guarda de
   * re-entrancia válida (`react-async-state` regla 11): dos clicks del mismo tick leen el mismo
   * render, así que el estado los deja pasar a los dos. */
  const ocupadoRef = useRef(false)
  /** Cuenta las aperturas del formulario (alta o edición). Cada apertura vuelve a leer las opciones remotas, y una
   * lectura que llega después de que se abrió otro formulario se descarta. */
  const aperturaRef = useRef(0)
  const [apertura, setApertura] = useState(0)
  const [lecturaRemota, setLecturaRemota] = useState<LecturaRemota>({ apertura: 0, porClave: {} })

  const { recurso, titulo, tituloSingular, campos, valoresPorDefecto, aValores, aAlta, sujetoDeBaja } = definicion

  const columnas = useMemo(() => columnasDeCatalogo(definicion), [definicion])
  const {
    filas: filasFiltradas,
    filtros,
    cambiar: cambiarFiltro,
    limpiar: limpiarFiltros,
    hayFiltros,
  } = useFiltrosDeColumna(items, columnas.filtrables)
  const { filas: filasVisibles, orden, alternar } = useOrdenamiento(filasFiltradas, columnas.ordenables)

  const cargar = useCallback(
    async (token: number, conInactivos: boolean, propagar = false) => {
      setCargando(true)
      try {
        const parametros = conInactivos ? '?incluirInactivos=true' : ''
        const filas = await api.get<TListado[]>(`/catalogos/${recurso}${parametros}`)
        if (generacion.current !== token) return
        setItems(filas)
        setError('')
      } catch (e) {
        if (generacion.current !== token) return
        if (propagar) throw e
        setError(e instanceof ErrorApi ? e.message : `No se pudo cargar ${titulo.toLowerCase()}.`)
      } finally {
        if (generacion.current === token) setCargando(false)
      }
    },
    [recurso, titulo],
  )

  useEffect(() => {
    void cargar(++generacion.current, incluirInactivos)
  }, [cargar, incluirInactivos])

  useEffect(() => {
    if (apertura === 0) return

    for (const campo of campos) {
      const remotas = campo.opcionesRemotas
      if (!remotas) continue

      remotas
        .cargar()
        .then(
          (datos) => ({ datos, fallo: false }),
          () => ({ datos: null, fallo: true }),
        )
        .then((resultado) => {
          if (aperturaRef.current !== apertura) return

          setLecturaRemota((prev) => ({
            apertura,
            porClave: { ...(prev.apertura === apertura ? prev.porClave : {}), [campo.clave]: resultado },
          }))
        })
    }
  }, [apertura, campos])

  function marcarApertura() {
    aperturaRef.current += 1
    setApertura(aperturaRef.current)
  }

  /** El refresco post-escritura va fuera del try/catch de la escritura: una escritura que ya
   * commiteó nunca se reporta como fallida (`react-async-state` regla 6). NO apaga la escritura:
   * de eso se ocupa el `finally` ungated de cada escritura. */
  async function refrescarTrasEscribir(token: number, mensajeOk: string, avisoDeFallo: string) {
    if (generacion.current !== token) return

    setAviso(mensajeOk)
    try {
      await cargar(token, incluirInactivos, true)
    } catch {
      if (generacion.current === token) setAviso(`${mensajeOk} ${avisoDeFallo}`)
    }
  }

  function abrirNuevo() {
    if (ocupadoRef.current) return

    marcarApertura()
    setFormulario({ id: null, nombre: '', activo: true, idEmpresa: null, valores: { ...valoresPorDefecto } })
    setAviso('')
    setError('')
    setErrorFormulario('')
  }

  function abrirEdicion(item: TListado) {
    if (ocupadoRef.current) return

    marcarApertura()
    setFormulario({ id: item.id, nombre: item.nombre, activo: item.activo, idEmpresa: item.idEmpresa, valores: aValores(item) })
    setAviso('')
    setError('')
    setErrorFormulario('')
  }

  /** El modal no ofrece cancelar con un guardado en vuelo (`ocupado`), así que cerrar no supersede
   * ninguna escritura y no acuña generación. */
  function cancelarFormulario() {
    setFormulario(null)
  }

  async function guardar() {
    if (!formulario || ocupadoRef.current) return

    const datos = formulario
    // Una lectura en vuelo queda invalidada por el token de esta escritura: no va a dejar datos ni a
    // bajar `cargando`. Si la escritura falla no hay refresco que lo repare, así que se vuelve a pedir.
    const habiaLecturaEnVuelo = cargando
    const token = ++generacion.current
    ocupadoRef.current = true
    setGuardando(true)
    setError('')
    setErrorFormulario('')
    setAviso('')
    try {
      try {
        const alta = aAlta(datos.nombre, datos.activo, datos.valores, datos.idEmpresa)
        if (datos.id === null) {
          await api.post(`/catalogos/${recurso}`, alta)
        } else {
          await api.put(`/catalogos/${recurso}/${datos.id}`, alta)
        }
      } catch (e) {
        if (generacion.current === token) {
          setErrorFormulario(e instanceof ErrorApi ? e.message : 'No se pudo guardar.')
          if (habiaLecturaEnVuelo) void cargar(++generacion.current, incluirInactivos)
        }

        return
      }

      if (generacion.current !== token) return

      setFormulario(null)
      await refrescarTrasEscribir(
        token,
        datos.id === null ? `Se creó "${datos.nombre}".` : `Se actualizó "${datos.nombre}".`,
        AVISO_REFRESCO_FALLIDO,
      )
    } finally {
      ocupadoRef.current = false
      setGuardando(false)
    }
  }

  /** Ver `Empresas.tsx`: mismo patrón de puerta, mismo contrato de invalidación, misma
   * re-entrancia. Abrir NO acuña generación: no hay escritura todavía. */
  function pedirBaja(item: TListado, disparador: HTMLElement | null) {
    if (ocupadoRef.current) return

    setDisparadorDeLaPuerta(disparador)
    setBaja(item)
    setError('')
    setAviso('')
  }

  /** Cancelar no supersede nada: solo cierra la puerta. Por eso no acuña generación y limpia los
   * dos avisos, en simetría con la apertura. */
  function cancelarBaja() {
    if (ocupadoRef.current) return

    setDisparadorDeLaPuerta(null)
    setBaja(null)
    setError('')
    setAviso('')
  }

  async function confirmarBaja() {
    if (!baja || ocupadoRef.current) return

    // El token se acuña ACÁ, primera sentencia síncrona de la escritura (`react-async-state`
    // regla 2).
    const item = baja
    const token = ++generacion.current
    ocupadoRef.current = true
    setOcupadoBaja(item.id)
    setError('')
    setAviso('')
    try {
      try {
        await api.delete(`/catalogos/${recurso}/${item.id}`)
      } catch (e) {
        // Un rechazo SIEMPRE se rinde, con la puerta abierta al lado del motivo.
        setError(copiaDeFalloDeBaja(e, sujetoDeBaja))

        return
      }

      // Un 204 SIEMPRE cierra la puerta y refresca; la generación solo gobierna el REFRESCO.
      setBaja(null)
      // La baja del item que se está editando se lleva también su formulario: dejarlo abierto
      // ofrecía guardar sobre una entidad que ya no existe, y el PUT moría en 404.
      setFormulario((prev) => (prev?.id === item.id ? null : prev))
      await refrescarTrasEscribir(token, `Se dio de baja "${item.nombre}".`, AVISO_REFRESCO_FALLIDO_BAJA)
    } finally {
      ocupadoRef.current = false
      setOcupadoBaja(null)
    }
  }

  /** La puerta abierta o cualquier escritura en vuelo bloquean la pantalla entera: ver
   * `Empresas.tsx`. */
  const bloqueado = guardando || ocupadoBaja !== null || baja !== null

  const columnasExtra = campos.filter((c) => c.columnaEnListado)

  const herramientas = (
    <nav className="p-2 d-flex align-items-center gap-3">
      <div className="form-check form-switch mb-0">
        <input
          id="incluir-inactivos"
          className="form-check-input"
          type="checkbox"
          checked={incluirInactivos}
          onChange={(e) => setIncluirInactivos(e.target.checked)}
          disabled={bloqueado}
        />
        <label className="form-check-label text-body-secondary small" htmlFor="incluir-inactivos">
          Incluir inactivos
        </label>
      </div>
      <BotonIcono
        icono="agregar"
        className="text-nowrap"
        onClick={abrirNuevo}
        disabled={bloqueado}
      />
    </nav>
  )

  return (
    <div ref={contenedorRef} tabIndex={-1} style={{ outline: 'none' }} className="container-fluid py-4">
      <Box titulo={titulo} variante="inverse" herramientas={herramientas}>
        {error && <div className="alert alert-danger">{error}</div>}
        {aviso && <div className="alert alert-success">{aviso}</div>}

        {baja && (
          <ConfirmacionDeBaja
            titulo={`${sujetoDeBaja} "${baja.nombre}"`}
            ocupado={ocupadoBaja !== null}
            disparador={disparadorDeLaPuerta}
            onConfirmar={confirmarBaja}
            onCancelar={cancelarBaja}
          />
        )}

        {cargando ? (
          <Cargando />
        ) : (
          <div className="table-responsive">
            <table className="table table-striped table-hover table-bordered align-middle">
              <thead>
                <tr>
                  <EncabezadoOrdenable columna="id" orden={orden} onAlternar={alternar}>
                    ID
                  </EncabezadoOrdenable>
                  <EncabezadoOrdenable columna="nombre" orden={orden} onAlternar={alternar}>
                    Nombre
                  </EncabezadoOrdenable>
                  {columnasExtra.map((c) => (
                    <EncabezadoOrdenable key={c.clave} columna={c.clave} orden={orden} onAlternar={alternar}>
                      {c.etiqueta}
                    </EncabezadoOrdenable>
                  ))}
                  <EncabezadoOrdenable columna="estado" orden={orden} onAlternar={alternar}>
                    Estado
                  </EncabezadoOrdenable>
                  <th scope="col" className="text-end">
                    Acciones
                  </th>
                </tr>
                <FilaDeFiltros
                  celdas={columnas.celdas}
                  columnas={columnas.filtrables}
                  filtros={filtros}
                  onCambio={cambiarFiltro}
                  onLimpiar={limpiarFiltros}
                  hayFiltros={hayFiltros}
                  deshabilitado={bloqueado}
                />
              </thead>
              <tbody>
                {filasVisibles.map((item) => {
                  const valores = aValores(item)
                  return (
                    <tr key={item.id}>
                      <td>{String(item.id).padStart(4, '0')}</td>
                      <td>{item.nombre}</td>
                      {columnasExtra.map((c) => (
                        <td key={c.clave}>{textoDeColumna(c, valores[c.clave], item) || '—'}</td>
                      ))}
                      <td>
                        <span className={`badge ${item.activo ? 'text-bg-success' : 'text-bg-secondary'}`}>
                          {item.activo ? 'Activo' : 'Inactivo'}
                        </span>
                      </td>
                      <td className="text-end text-nowrap">
                        <BotonIcono
                          icono="editar"
                          className="me-1"
                          onClick={() => abrirEdicion(item)}
                          disabled={bloqueado}
                        />
                        {item.activo && (
                          <BotonIcono
                            icono="eliminar"
                            onClick={(evento) => pedirBaja(item, evento.currentTarget)}
                            disabled={bloqueado}
                          />
                        )}
                      </td>
                    </tr>
                  )
                })}
                {filasVisibles.length === 0 && (
                  <tr>
                    <td colSpan={4 + columnasExtra.length} className="text-center text-muted py-4">
                      {items.length === 0
                        ? `No hay ${titulo.toLowerCase()} cargados.`
                        : 'Ningún registro coincide con los filtros.'}
                    </td>
                  </tr>
                )}
              </tbody>
            </table>
          </div>
        )}
      </Box>

      {formulario && (
        <ModalDeCatalogo
          valor={formulario}
          campos={campos}
          tituloSingular={tituloSingular}
          guardando={guardando}
          bloqueado={bloqueado}
          error={errorFormulario}
          focoDeReserva={contenedorRef}
          items={items}
          lecturaRemota={lecturaRemota.apertura === apertura ? lecturaRemota.porClave : {}}
          onCambio={setFormulario}
          onGuardar={guardar}
          onCancelar={cancelarFormulario}
        />
      )}
    </div>
  )
}

function ModalDeCatalogo({
  valor,
  campos,
  tituloSingular,
  guardando,
  bloqueado,
  error,
  focoDeReserva,
  items,
  lecturaRemota,
  onCambio,
  onGuardar,
  onCancelar,
}: {
  valor: Formulario
  campos: CampoDescriptor[]
  tituloSingular: string
  guardando: boolean
  bloqueado: boolean
  error: string
  focoDeReserva: React.RefObject<HTMLElement | null>
  items: unknown[]
  lecturaRemota: LecturaRemota['porClave']
  onCambio: (f: Formulario) => void
  onGuardar: () => void
  onCancelar: () => void
}) {
  const esNuevo = valor.id === null
  const camposVisibles = campos.filter((campo) => !campo.visibleSi || campo.visibleSi(valor.valores))

  function cambiarValorPropio(clave: string, nuevo: ValorDeCampo) {
    onCambio({ ...valor, valores: { ...valor.valores, [clave]: nuevo } })
  }

  const pie = (
    <>
      <button type="button" className="btn btn-outline-secondary" onClick={onCancelar} disabled={bloqueado}>
        Cancelar
      </button>
      <button type="submit" form={ID_FORMULARIO_CATALOGO} className="btn btn-success" disabled={bloqueado}>
        {guardando ? 'Guardando…' : 'Guardar'}
      </button>
    </>
  )

  return (
    <Modal
      titulo={esNuevo ? `Nueva ${tituloSingular}` : `Editar ${tituloSingular}`}
      tamano="lg"
      ocupado={bloqueado}
      focoDeReserva={focoDeReserva}
      pie={pie}
      onCerrar={onCancelar}
    >
      <ErrorDeFormulario mensaje={error} />
      <form
        id={ID_FORMULARIO_CATALOGO}
        className="row g-3"
        autoComplete="off"
        onSubmit={(e) => {
          e.preventDefault()
          onGuardar()
        }}
      >
        <div className="col-md-4">
          <label className="form-label" htmlFor="f-nombre">
            Nombre
          </label>
          <input
            id="f-nombre"
            autoFocus
            className="form-control"
            maxLength={150}
            value={valor.nombre}
            onChange={(e) => onCambio({ ...valor, nombre: e.target.value })}
            disabled={bloqueado}
            required
          />
        </div>

        {camposVisibles.map((campo) => {
          const remota = campo.opcionesRemotas
          const lectura = lecturaRemota[campo.clave]
          const opciones = remota
            ? remota.armar(lectura?.datos ?? null, valor.valores)
            : campo.opcionesDesdeListado
              ? campo.opcionesDesdeListado(items, valor.id)
              : (campo.opciones ?? [])
          const valorActual = String(valor.valores[campo.clave] ?? '')
          const avisoRemoto = remota && lectura ? remota.aviso(lectura.fallo ? null : lectura.datos, opciones) : null
          // Solo tiene sentido buscar la opción faltante en `items` cuando las opciones vienen del
          // listado: para selects de opciones estáticas (p. ej. comportamiento de medio de pago),
          // buscar el valor por id en `items` podría matchear con un item no relacionado.
          const opcionFaltante =
            campo.opcionesDesdeListado && valorActual !== '' && !opciones.some((o) => o.valor === valorActual)
              ? { valor: valorActual, etiqueta: etiquetaParaValorFaltante(valorActual, items) }
              : null

          return (
            <div className="col-md-3" key={campo.clave}>
              <label className="form-label" htmlFor={`f-${campo.clave}`}>
                {campo.etiqueta}
              </label>
              {campo.tipo === 'booleano' ? (
                <div className="form-check pt-2">
                  <input
                    id={`f-${campo.clave}`}
                    type="checkbox"
                    className="form-check-input"
                    checked={Boolean(valor.valores[campo.clave])}
                    onChange={(e) => cambiarValorPropio(campo.clave, e.target.checked)}
                    disabled={bloqueado}
                  />
                </div>
              ) : campo.tipo === 'select' ? (
                <select
                  id={`f-${campo.clave}`}
                  className="form-select"
                  value={valorActual}
                  onChange={(e) => cambiarValorPropio(campo.clave, e.target.value)}
                  disabled={bloqueado}
                  required={campo.requerido}
                >
                  {(valorActual === '' || campo.opcionVacia) && (
                    <option value="">{campo.opcionVacia ?? '— Elegí una opción —'}</option>
                  )}
                  {opcionFaltante && (
                    <option value={opcionFaltante.valor}>{opcionFaltante.etiqueta}</option>
                  )}
                  {opciones.map((o) => (
                    <option key={o.valor} value={o.valor}>
                      {o.etiqueta}
                    </option>
                  ))}
                </select>
              ) : (
                <input
                  id={`f-${campo.clave}`}
                  {...atributosDeCampoNumerico(campo)}
                  className="form-control"
                  value={valorActual}
                  onChange={(e) => cambiarValorPropio(campo.clave, e.target.value)}
                  disabled={bloqueado}
                  required={campo.requerido}
                />
              )}
              {campo.ayuda && <div className="form-text">{campo.ayuda}</div>}
              {avisoRemoto && (
                <div className={`form-text${avisoRemoto.error ? ' text-danger' : ''}`}>{avisoRemoto.texto}</div>
              )}
            </div>
          )
        })}

        <div className="col-md-2">
          <label className="form-label" htmlFor="f-activo">
            Estado
          </label>
          <select
            id="f-activo"
            className="form-select"
            value={valor.activo ? 'activo' : 'inactivo'}
            onChange={(e) => onCambio({ ...valor, activo: e.target.value === 'activo' })}
            disabled={bloqueado}
          >
            <option value="activo">Activo</option>
            <option value="inactivo">Inactivo</option>
          </select>
        </div>
      </form>
    </Modal>
  )
}

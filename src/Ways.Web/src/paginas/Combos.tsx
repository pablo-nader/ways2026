import { useCallback, useEffect, useRef, useState } from 'react'
import { ErrorApi } from '../api/cliente'
import { clienteDeCombos } from '../api/combos'
import {
  aSolicitudDeCombo,
  aValoresCombo,
  clienteDeCombosAbm,
  comboCoincideConAlta,
  errorDeServidorDeCombo,
  esComboInexistente,
  esResultadoIncierto,
  etiquetaDeCodigoDeCombo,
  formularioComboVacio,
  hayErrores,
  mensajeDeFalloDeBajaDeCombo,
  MENSAJE_COMBO_INEXISTENTE,
  resumenDeComponentes,
  tieneComponentesNoDisponibles,
  validarCombo,
  type ErrorDeServidorDeCombo,
  type FormularioCombo,
} from '../api/combosAbm'
import { clienteDeFamilias } from '../api/familias'
import { clienteDeOrganizacion } from '../api/organizacion'
import type { AltaCombo, ComboListado, EmpresaListado, FamiliaListado } from '../api/tipos'
import { formatearImporte } from '../formato/importes'
import { Box } from '../componentes/Box'
import { BotonIcono } from '../componentes/BotonIcono'
import { Cargando } from '../componentes/Cargando'
import { ConfirmacionDeBaja } from '../componentes/ConfirmacionDeBaja'
import { ModalDeCombo } from './combos/ModalDeCombo'

const AVISO_REFRESCO_FALLIDO = 'No se pudo actualizar el listado. Volvé a entrar para ver el cambio.'
const AVISO_ALTA_CONFIRMADA_POR_LECTURA = 'El combo se creó. No se pudo confirmar la respuesta del servidor.'

/**
 * ABM de combos (solo Admin): un combo son dos o más unidades, cada componente un artículo o una familia con su
 * cantidad, a un precio fijo. El alta y la edición viven en un `Modal`; la baja pasa por `ConfirmacionDeBaja`.
 *
 * Lecturas (listado) con generación: la más nueva gana. Las escrituras no la acuñan: mientras una está en vuelo,
 * o la puerta de la baja está abierta, la pantalla entera queda inerte (`bloqueado`), el modal no se puede cerrar
 * y nada puede reemplazar ni reabrir lo que la respuesta escribe; tampoco navega. Un espejo sincrónico
 * (`ocupadoRef`) frena el doble clic dentro del mismo tick (`react-async-state` reglas 2, 9 y 11).
 */
export function Combos() {
  const [combos, setCombos] = useState<ComboListado[] | null>(null)
  const [familias, setFamilias] = useState<FamiliaListado[]>([])
  const [empresas, setEmpresas] = useState<EmpresaListado[]>([])
  /** `true` solo cuando el listado respectivo cargó: sin él, una empresa o familia ausente no se rotula "no disponible". */
  const [familiasListas, setFamiliasListas] = useState(false)
  const [empresasListas, setEmpresasListas] = useState(false)
  const [avisosCatalogos, setAvisosCatalogos] = useState<string[]>([])
  const [cargando, setCargando] = useState(true)
  const [errorDeCarga, setErrorDeCarga] = useState('')
  const [error, setError] = useState('')
  const [aviso, setAviso] = useState('')
  const [formulario, setFormulario] = useState<FormularioCombo | null>(null)
  const [mostrarErrores, setMostrarErrores] = useState(false)
  const [errorGeneral, setErrorGeneral] = useState('')
  const [errorDeServidor, setErrorDeServidor] = useState<ErrorDeServidorDeCombo | null>(null)
  /** Cuenta los rechazos: el mismo mensaje puede repetirse sin cambiar el estado. */
  const [rechazos, setRechazos] = useState(0)
  const [guardando, setGuardando] = useState(false)
  const [eliminando, setEliminando] = useState(false)
  const [bajaPendiente, setBajaPendiente] = useState<ComboListado | null>(null)
  const [disparadorDeLaPuerta, setDisparadorDeLaPuerta] = useState<HTMLElement | null>(null)
  const generacionRef = useRef(0)
  const ocupadoRef = useRef(false)
  /** Destino de foco cuando el modal se cierra con la pantalla inerte (refresco en vuelo): ningún botón es alcanzable. */
  const contenedorRef = useRef<HTMLDivElement>(null)
  const bloqueado = guardando || eliminando || bajaPendiente !== null

  const cargar = useCallback(async (token: number, propagar = false) => {
    setCargando(true)
    try {
      const filas = await clienteDeCombos.listar()
      if (generacionRef.current !== token) return
      setCombos(filas)
      setErrorDeCarga('')
    } catch (e) {
      if (generacionRef.current !== token) return
      if (propagar) throw e
      setErrorDeCarga(e instanceof ErrorApi ? e.message : 'No se pudieron cargar los combos.')
    } finally {
      if (generacionRef.current === token) setCargando(false)
    }
  }, [])

  useEffect(() => {
    void cargar(++generacionRef.current)
    clienteDeFamilias
      .listar()
      .then((lista) => {
        setFamilias(lista)
        setFamiliasListas(true)
      })
      .catch(() => {
        setFamilias([])
        agregarAvisoCatalogo(
          'No se pudieron cargar las familias: no hay familias para elegir hasta recargar la página. Un componente que ya apunta a una familia se muestra por su número y se puede guardar sin cambiarlo.',
        )
      })
    clienteDeOrganizacion
      .listarEmpresas()
      .then((lista) => {
        setEmpresas(lista)
        setEmpresasListas(true)
      })
      .catch(() => {
        setEmpresas([])
        agregarAvisoCatalogo(
          'No se pudieron cargar las empresas: el selector de empresa no va a estar disponible (el combo se crea para todo el tenant).',
        )
      })
  }, [cargar])

  function agregarAvisoCatalogo(mensaje: string) {
    setAvisosCatalogos((prev) => (prev.includes(mensaje) ? prev : [...prev, mensaje]))
  }

  function cambiarFormulario(valor: FormularioCombo) {
    setFormulario(valor)
    setErrorDeServidor(null)
    setErrorGeneral('')
  }

  function abrirFormulario(valor: FormularioCombo) {
    setFormulario(valor)
    setMostrarErrores(false)
    setErrorDeServidor(null)
    setErrorGeneral('')
    setAviso('')
    setError('')
  }

  function abrirNuevo() {
    if (bloqueado) return
    abrirFormulario(formularioComboVacio())
  }

  function abrirEdicion(combo: ComboListado) {
    if (bloqueado) return
    abrirFormulario(aValoresCombo(combo))
  }

  function cancelarEdicion() {
    if (ocupadoRef.current) return
    setFormulario(null)
  }

  /** El refresco post-escritura va fuera del try/catch de la escritura: una escritura que ya commiteó nunca se
   * reporta como fallida (`react-async-state` regla 6). */
  async function refrescarTrasEscribir(mensajeOk: string) {
    setAviso(mensajeOk)
    try {
      await cargar(++generacionRef.current, true)
    } catch {
      setError(AVISO_REFRESCO_FALLIDO)
    }
  }

  /** Tras un alta de resultado incierto, relee el listado y dice si ya figura un combo que antes no estaba y coincide en
   * todo con lo enviado (el nombre solo no alcanza: no es único). Un fallo de la lectura no concluye nada. Avanza la generación para descartar una lectura anterior todavía en
   * vuelo; su propia respuesta no se compara contra la generación porque la pantalla está inerte mientras escribe y
   * ninguna lectura más nueva puede empezar. */
  async function altaConfirmadaPorLectura(cuerpo: AltaCombo, idsPrevios: ReadonlySet<number>): Promise<boolean> {
    generacionRef.current += 1
    try {
      const filas = await clienteDeCombos.listar()
      setCombos(filas)
      setErrorDeCarga('')
      return filas.some((c) => !idsPrevios.has(c.id) && comboCoincideConAlta(cuerpo, c))
    } catch {
      return false
    }
  }

  async function guardar() {
    if (!formulario || ocupadoRef.current) return

    if (hayErrores(validarCombo(formulario))) {
      setMostrarErrores(true)
      setErrorGeneral('Revisá los campos marcados.')
      setRechazos((n) => n + 1)
      return
    }

    const datos = formulario
    const idsPrevios = combos === null ? null : new Set(combos.map((c) => c.id))
    ocupadoRef.current = true
    setGuardando(true)
    setError('')
    setAviso('')
    setErrorGeneral('')
    setErrorDeServidor(null)
    try {
      try {
        const cuerpo = aSolicitudDeCombo(datos)
        if (datos.id === null) await clienteDeCombosAbm.crear(cuerpo)
        else await clienteDeCombosAbm.actualizar(datos.id, cuerpo)
      } catch (e) {
        if (esComboInexistente(e)) {
          // Guardar sobre algo que dejó de existir no tiene salida: se cierra el formulario y se vuelve a leer el listado.
          setFormulario(null)
          setError(MENSAJE_COMBO_INEXISTENTE)
          await cargar(++generacionRef.current)
          return
        }
        if (datos.id === null && idsPrevios !== null && esResultadoIncierto(e) && (await altaConfirmadaPorLectura(aSolicitudDeCombo(datos), idsPrevios))) {
          // El POST no tiene idempotencia: si el combo ya figura, reintentar crearía un segundo.
          setFormulario(null)
          setAviso(AVISO_ALTA_CONFIRMADA_POR_LECTURA)
          return
        }
        const rechazo = errorDeServidorDeCombo(e)
        setErrorDeServidor(rechazo)
        setErrorGeneral(
          rechazo.campo === 'formulario' ? rechazo.mensaje : 'No se pudo guardar el combo. Revisá el mensaje junto al campo marcado.',
        )
        setRechazos((n) => n + 1)
        return
      }

      setFormulario(null)
      await refrescarTrasEscribir(`Combo "${datos.nombre.trim()}" ${datos.id === null ? 'creado' : 'actualizado'}.`)
    } finally {
      ocupadoRef.current = false
      setGuardando(false)
    }
  }

  function pedirBaja(combo: ComboListado, disparador: HTMLElement | null) {
    if (bloqueado) return
    setDisparadorDeLaPuerta(disparador)
    setBajaPendiente(combo)
    setError('')
    setAviso('')
  }

  function cancelarBaja() {
    if (ocupadoRef.current) return
    setDisparadorDeLaPuerta(null)
    setBajaPendiente(null)
  }

  async function confirmarBaja() {
    if (!bajaPendiente || ocupadoRef.current) return

    const combo = bajaPendiente
    ocupadoRef.current = true
    setEliminando(true)
    setError('')
    try {
      try {
        await clienteDeCombosAbm.eliminar(combo.id)
      } catch (e) {
        setError(mensajeDeFalloDeBajaDeCombo(e))
        // El combo ya no existe: no hay nada que confirmar, así que la puerta se cierra y se vuelve a leer el listado.
        // Con cualquier otro fallo la puerta queda abierta para reintentar.
        if (esComboInexistente(e)) {
          setBajaPendiente(null)
          await cargar(++generacionRef.current)
        }
        return
      }

      setBajaPendiente(null)
      await refrescarTrasEscribir(`Combo "${combo.nombre}" dado de baja.`)
    } finally {
      ocupadoRef.current = false
      setEliminando(false)
    }
  }

  const herramientas = (
    <nav className="p-2 d-flex gap-2">
      <BotonIcono icono="agregar" className="text-nowrap" disabled={bloqueado} onClick={abrirNuevo} />
    </nav>
  )

  return (
    <div ref={contenedorRef} tabIndex={-1} style={{ outline: 'none' }} className="container-fluid py-4">
      <Box titulo="Combos" variante="inverse" herramientas={herramientas}>
        <p className="text-muted">
          Un combo junta dos o más unidades de artículos o familias a un precio fijo. En la caja se carga tipeando su código con
          la C adelante (por ejemplo, C12).
        </p>

        {errorDeCarga && <div className="alert alert-danger">{errorDeCarga}</div>}
        {error && <div className="alert alert-danger">{error}</div>}
        {aviso && <div className="alert alert-success">{aviso}</div>}
        {avisosCatalogos.length > 0 && <div className="alert alert-warning">{avisosCatalogos.join(' ')}</div>}

        {bajaPendiente && (
          <ConfirmacionDeBaja
            titulo={`el combo "${bajaPendiente.nombre}"`}
            ocupado={eliminando}
            disparador={disparadorDeLaPuerta}
            onConfirmar={confirmarBaja}
            onCancelar={cancelarBaja}
          />
        )}

        {cargando && combos === null ? (
          <Cargando />
        ) : (
          <div className="table-responsive">
            <table className="table table-striped table-hover table-bordered align-middle">
              <thead>
                <tr>
                  <th>Código</th>
                  <th>Nombre</th>
                  <th className="text-end">Precio</th>
                  <th>Componentes</th>
                  <th>Estado</th>
                  <th>Online</th>
                  <th className="text-end">Acciones</th>
                </tr>
              </thead>
              <tbody>
                {combos?.map((combo) => (
                  <tr key={combo.id}>
                    <td className="text-nowrap">{etiquetaDeCodigoDeCombo(combo.codigo)}</td>
                    <td>{combo.nombre}</td>
                    <td className="text-end text-nowrap">{formatearImporte(combo.precio, { simbolo: true })}</td>
                    <td>
                      {resumenDeComponentes(combo.componentes)}
                      {tieneComponentesNoDisponibles(combo) && (
                        <div className="text-danger small">
                          Tiene componentes no disponibles: el combo no se puede completar hasta corregirlo.
                        </div>
                      )}
                    </td>
                    <td>
                      <span className={`badge ${combo.activo ? 'text-bg-success' : 'text-bg-secondary'}`}>
                        {combo.activo ? 'Activo' : 'Inactivo'}
                      </span>
                    </td>
                    <td>{combo.disponibleOnline ? 'Sí' : 'No'}</td>
                    <td className="text-end text-nowrap">
                      <BotonIcono
                        icono="editar"
                        etiqueta={`Editar ${combo.nombre}`}
                        className="me-1"
                        disabled={bloqueado}
                        onClick={() => abrirEdicion(combo)}
                      />
                      <BotonIcono
                        icono="eliminar"
                        etiqueta={`Dar de baja ${combo.nombre}`}
                        disabled={bloqueado}
                        onClick={(evento) => pedirBaja(combo, evento.currentTarget)}
                      />
                    </td>
                  </tr>
                ))}
                {combos !== null && combos.length === 0 && errorDeCarga === '' && (
                  <tr>
                    <td colSpan={7} className="text-center text-muted py-4">
                      No hay combos cargados.
                    </td>
                  </tr>
                )}
              </tbody>
            </table>
          </div>
        )}
      </Box>

      {formulario && (
        <ModalDeCombo
          key={formulario.id ?? 'nuevo'}
          valor={formulario}
          familias={familias}
          empresas={empresas}
          familiasListas={familiasListas}
          empresasListas={empresasListas}
          guardando={guardando}
          ocupado={guardando}
          mostrarErrores={mostrarErrores}
          errorDeServidor={errorDeServidor}
          error={errorGeneral}
          disparoDelError={rechazos}
          focoDeReserva={contenedorRef}
          onCambio={cambiarFormulario}
          onGuardar={guardar}
          onCancelar={cancelarEdicion}
        />
      )}
    </div>
  )
}

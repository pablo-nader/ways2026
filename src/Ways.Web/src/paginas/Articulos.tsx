import { useEffect, useRef, useState } from 'react'
import { NavigationType, useLocation, useNavigate, useNavigationType } from 'react-router'
import { clienteDeArticulos } from '../api/articulos'
import { ErrorApi } from '../api/cliente'
import type { FilaDeGrillaDeArticulos } from '../api/tipos'
import { Box } from '../componentes/Box'
import { Modal } from '../componentes/Modal'
import { GrillaDeArticulos } from './articulos/GrillaDeArticulos'
import { desplazamientoHaciaLaAnterior, HISTORIAL_SIN_OBSERVAR, registrarEntrada } from './articulos/historialObservado'
import { ModalDeArticulo } from './articulos/ModalDeArticulo'
import { PreguntaDeAlcance } from './articulos/PreguntaDeAlcance'
import { analizarRutaModal, type ModoModalDeArticulo } from './articulos/rutaModal'
import { MENSAJE_CONFIRMAR_DESCARTE, useFormularioDeArticulo } from './articulos/useFormularioDeArticulo'
import { BotonIcono } from '../componentes/BotonIcono'

const MENSAJE_ID_INVALIDO = 'No se especificó un artículo válido.'

/** Identidad del modal actualmente comprometido en pantalla: `null` (cerrado), `'nuevo'` (alta) o
 * el id numérico de la edición en curso — nunca el `modo`/`idParam` crudos de la URL, que pueden
 * apuntar a un id inválido sin formulario cargado. Se usa para distinguir "la URL cambió pero
 * seguimos en el mismo modal" (p. ej. tras cancelar un intento de salida) de una salida real. */
type DestinoModal = 'nuevo' | number | 'invalido' | null

function destinoDeRuta(modo: ModoModalDeArticulo | null, idParam: string | null): DestinoModal {
  if (modo === 'crear') return 'nuevo'
  if (modo === 'editar') return idParam !== null && /^\d+$/.test(idParam) ? Number(idParam) : 'invalido'
  return null
}

function rutaDeDestino(destino: DestinoModal): string {
  if (destino === 'nuevo') return '/articulos/create'
  if (typeof destino === 'number') return `/articulos/edit/${destino}`
  return '/articulos'
}

/**
 * ABM dedicado de artículos (design decision 1: no la máquina genérica de catálogos) — la
 * pantalla más pesada a la fecha (identificación + códigos de barra + clasificación + costos +
 * disponibilidad por empresa + precios por lista). El código de barras y el editor de precios
 * solo se habilitan una vez que el artículo tiene `id` persistido: ambos endpoints cuelgan de
 * `/api/articulos/{id}/...`, no existen antes del alta.
 *
 * articulos-en-modal: la grilla es lo único que se ve en `/articulos`; el alta/edición vive en un
 * `Modal` gobernado por la URL (`/articulos/create`, `/articulos/edit/:id`). Montado en
 * `/articulos/*` (App.tsx) como una única entrada de ruta — el modo del modal se deriva de
 * `useLocation().pathname` con `analizarRutaModal` en vez de con `<Routes>` anidadas, para que
 * React Router nunca remonte esta página (y con ella, la grilla) al abrir/cerrar el modal: React
 * Router remonta al cambiar de ENTRADA de ruta, no al cambiar solo un param de la misma entrada.
 *
 * El estado, la validación y el guardado del formulario viven en `useFormularioDeArticulo`; esta página
 * le agrega lo que depende de la URL: cuándo se abre, cómo se cierra y a dónde va tras un alta.
 */
export function Articulos() {
  const location = useLocation()
  const navigate = useNavigate()
  const navigationType = useNavigationType()
  const { modo, idParam } = analizarRutaModal(location.pathname)

  // Banners de la Baja únicamente (articulos-grilla-web: el error de CARGA del listado ahora vive
  // dentro de `GrillaDeArticulos`, con su propio banner — regla 14 de react-async-state, un slot
  // de estado por fuente).
  const [error, setError] = useState('')
  const [aviso, setAviso] = useState('')
  const [eliminando, setEliminando] = useState(false)
  // Bump tras guardar/dar de baja: le pide a la grilla un refresco manteniendo sus propios
  // filtros/página — la página no espera ni conoce el resultado de ese refresco (ver el
  // doc-comment de `GrillaDeArticulos`).
  const [pedidoDeRefresco, setPedidoDeRefresco] = useState(0)

  const articulo = useFormularioDeArticulo({
    // History replace (no push): /articulos/create nunca queda alcanzable con "atrás" una
    // vez que el alta se concretó — el modal sigue montado (misma entrada de ruta,
    // `/articulos/*`), así que el aviso y el foco sobreviven al cambio de URL.
    alCrear: (creado) => navigate(`/articulos/edit/${creado.id}`, { replace: true }),
    alGuardar: () => setPedidoDeRefresco((n) => n + 1),
  })
  const { formulario, catalogos } = articulo

  // Identidad del modal ya comprometida (ver `DestinoModal`) — la compuerta de confirmación del
  // efecto de apertura la compara contra el destino que la URL pide ahora, para distinguir "salir
  // de verdad" de "la URL volvió sola al mismo modal" (p. ej. tras cancelar esa misma salida).
  // `destinoMostrado` es su espejo en estado y es lo ÚNICO que decide si el modal se renderiza: el
  // pathname en vivo puede adelantarse a la decisión (Atrás/Adelante commitean la URL antes de que
  // el efecto pregunte), y renderizar desde él desmontaría el modal — y con él el estado propio de
  // los hijos (código de barras tipeado, alta rápida abierta) — aunque después se cancele la salida.
  // Ref y estado se actualizan siempre juntos, solo cuando la salida o apertura ya quedó decidida.
  const destinoModalRef = useRef<DestinoModal>(null)
  const [destinoMostrado, setDestinoMostrado] = useState<DestinoModal>(null)
  // Posición relativa de las entradas del historial vistas, para deshacer un Atrás/Adelante
  // rechazado volviendo a la entrada del modal (ver `historialObservado`).
  const historialRef = useRef(HISTORIAL_SIN_OBSERVAR)
  const refBotonNuevo = useRef<HTMLButtonElement>(null)
  const ocupado = articulo.ocupado || eliminando

  // Vuelve al estado "sin modal": compromete el destino nulo (desmonta el modal) y limpia todo el
  // estado del formulario para no arrastrar restos a la próxima apertura. Lo usan el efecto de
  // apertura (salida aceptada por URL) y `cerrarModal` (salida aceptada por click) — en este último
  // caso, comprometerlo ANTES de navegar es lo que evita que el efecto vuelva a preguntar.
  function descartarModal() {
    destinoModalRef.current = null
    setDestinoMostrado(null)
    articulo.descartar()
  }

  // Declarado antes del efecto de apertura a propósito: React corre los efectos en orden de
  // declaración, así que cuando ese efecto decide una salida la entrada nueva ya quedó registrada.
  useEffect(() => {
    historialRef.current = registrarEntrada(historialRef.current, location.key, navigationType)
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [location.key])

  // Efecto de apertura: reacciona a la URL, no a clicks — así una edición abierta desde la grilla,
  // desde una URL tipeada a mano o desde "atrás/adelante" del navegador pasan siempre por el mismo
  // camino. Cuando `modo` pasa a null (URL vuelve a /articulos) se limpia todo el estado del modal
  // para no arrastrar restos a la próxima apertura.
  useEffect(() => {
    const destino = destinoDeRuta(modo, idParam)

    // Ya estamos en este destino: tras cancelar un intento de salir (unas líneas más abajo se navegó
    // de vuelta a esta misma URL), o tras un `cerrarModal` que ya comprometió el cierre antes de
    // navegar. No hay nada que resetear ni que preguntar — evita reaplicar el bloque de abajo (que
    // en 'crear' pisaría el borrador con un formulario en blanco nuevo).
    if (destino === destinoModalRef.current) return

    // La URL se fue de un modal con cambios sin guardar sin pasar por `cerrarModal` (Atrás/Adelante
    // del navegador, o un link a otra edición): la URL nueva ya está commiteada, pero el modal sigue
    // montado porque se renderiza desde `destinoMostrado`, que todavía no cambió. Si cancela, se
    // vuelve a la URL del modal actual y las mismas instancias siguen vivas (formulario e hijos
    // intactos); si acepta, se sigue de largo y el bloque de abajo compromete el destino nuevo.
    if (destinoModalRef.current !== null && articulo.haySinGuardar()) {
      if (!confirm(MENSAJE_CONFIRMAR_DESCARTE)) {
        // Un POP se deshace MOVIÉNDOSE a la entrada anterior, la del modal: un `replace` pisaría la
        // entrada a la que llegó el POP (p. ej. la de la grilla) y Atrás ya no la encontraría. Si
        // esa entrada es anterior al montaje de esta pantalla (p. ej. tras recargar), no tiene
        // posición conocida y se reemplaza igual. Un PUSH/REPLACE sí se deshace con `replace`:
        // reescribe solo la entrada que esa misma navegación acaba de crear o de pisar.
        const desplazamiento = navigationType === NavigationType.Pop ? desplazamientoHaciaLaAnterior(historialRef.current) : null
        if (desplazamiento === null) navigate(rutaDeDestino(destinoModalRef.current), { replace: true })
        else navigate(desplazamiento)
        return
      }
    }

    if (destino === null) {
      descartarModal()
      return
    }

    destinoModalRef.current = destino
    setDestinoMostrado(destino)

    if (modo === 'crear') {
      articulo.abrirCrear()
      return
    }

    if (modo === 'editar') {
      const idNumerico = idParam !== null && /^\d+$/.test(idParam) ? Number(idParam) : null
      if (idNumerico === null) {
        articulo.rechazarEdicion(MENSAJE_ID_INVALIDO)
        return
      }
      // Ya cargado: pasa exactamente cuando `guardar()` acaba de crear este mismo artículo y
      // reemplazó la URL a /articulos/edit/{id} — evita un refetch redundante que además pisaría
      // el aviso de éxito recién puesto.
      if (formulario?.id === idNumerico) return
      void articulo.abrirEdicion(idNumerico)
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [modo, idParam])

  // Único punto de cierre: el botón "Cancelar" del formulario, el × del header, Escape y el click
  // en el backdrop de `Modal` llegan todos acá (nunca `history.back()` — una pestaña nueva no tiene
  // historial previo). `Modal` ya bloquea estos tres últimos mientras `ocupado`; el guard de acá
  // cubre además el botón "Cancelar" del propio formulario. Con cambios sin guardar se pregunta
  // ACÁ, antes de navegar: si cancela no se toca ni la URL ni el modal (los hijos conservan su
  // estado); si acepta, `descartarModal()` compromete el cierre antes del `navigate`, así el efecto
  // de apertura encuentra el destino nulo ya aplicado y no vuelve a preguntar.
  function cerrarModal() {
    if (ocupado) return
    if (articulo.haySinGuardar() && !confirm(MENSAJE_CONFIRMAR_DESCARTE)) return
    descartarModal()
    navigate('/articulos', { replace: true })
  }

  function irACrear() {
    if (ocupado) return
    navigate('/articulos/create')
  }

  async function eliminar(a: FilaDeGrillaDeArticulos) {
    if (ocupado) return
    if (!confirm(`¿Dar de baja el artículo "${a.nombre}"?`)) return

    setError('')
    setAviso('')
    setEliminando(true)
    try {
      await clienteDeArticulos.eliminar(a.id)
      setAviso(`Artículo "${a.nombre}" dado de baja.`)
      // Igual criterio que `guardar()`: la baja ya se confirmó acá, el refresco de la grilla es
      // un pedido aparte que `GrillaDeArticulos` resuelve (y reporta) por su cuenta.
      setPedidoDeRefresco((n) => n + 1)
    } catch (e) {
      setError(e instanceof ErrorApi ? e.message : 'No se pudo dar de baja.')
    } finally {
      setEliminando(false)
    }
  }

  const herramientas = (
    <nav className="p-2 d-flex gap-2">
      <BotonIcono
        icono="agregar"
        ref={refBotonNuevo}
        className="text-nowrap"
        disabled={ocupado}
        onClick={irACrear}
      />
    </nav>
  )

  return (
    <div className="container-fluid py-4">
      <Box titulo="Artículos" variante="inverse" herramientas={herramientas}>
        {error && <div className="alert alert-danger">{error}</div>}
        {aviso && <div className="alert alert-success">{aviso}</div>}
        {catalogos.erroresCatalogosRequeridos.length > 0 && (
          <div className="alert alert-warning">
            {catalogos.erroresCatalogosRequeridos.join(' ')} El guardado (alta o edición) de artículos va a quedar bloqueado
            hasta que se puedan cargar — recargá la página para reintentar.
          </div>
        )}
        {catalogos.avisoListasPrecio && <div className="alert alert-warning">{catalogos.avisoListasPrecio}</div>}

        <GrillaDeArticulos
          proveedores={catalogos.proveedores}
          ocupado={ocupado}
          pedidoDeRefresco={pedidoDeRefresco}
          onEliminar={eliminar}
        />
      </Box>

      {destinoMostrado !== null && (
        <ModalDeArticulo {...articulo.propsDelModal({ ocupado, focoDeReserva: refBotonNuevo, onCerrar: cerrarModal })} />
      )}

      {/* La pregunta de alcance es hermana del modal de artículo y no hija: React propaga los eventos por el
          árbol de componentes, y el Tab de este diálogo no tiene que pasar por la trampa de foco del de abajo. */}
      {articulo.decisionDeAlcance !== null && (
        <Modal titulo="Cambio en una familia" ocupado={ocupado} onCerrar={articulo.cancelarAlcance}>
          <PreguntaDeAlcance
            contexto={articulo.decisionDeAlcance.contexto}
            cambios={articulo.decisionDeAlcance.campos}
            ocupado={ocupado}
            onElegir={articulo.elegirAlcance}
            onCancelar={articulo.cancelarAlcance}
          />
        </Modal>
      )}
    </div>
  )
}

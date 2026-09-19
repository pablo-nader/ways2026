/**
 * Deriva el modo del modal de alta/edición a partir del pathname — pura, sin `useParams`: se
 * calcula con `useLocation().pathname` directamente en `Articulos.tsx` (que se monta en
 * `/articulos/*` en `App.tsx`, no con `<Routes>` anidadas) para que la grilla y el estado del
 * formulario NUNCA se remonten al abrir/cerrar el modal — algo que sí pasaría si `create` y
 * `edit/:id` fueran dos `<Route>` distintas (React Router remonta al cambiar de entrada de ruta,
 * no al cambiar solo un param de la misma entrada).
 */
export type ModoModalDeArticulo = 'crear' | 'editar'

export type RutaModalDeArticulo = { modo: ModoModalDeArticulo; idParam: string | null } | { modo: null; idParam: null }

export function analizarRutaModal(pathname: string): RutaModalDeArticulo {
  const resto = pathname.replace(/^\/articulos\/?/, '')
  const segmentos = resto.split('/').filter(Boolean)

  if (segmentos[0] === 'create' && segmentos.length === 1) return { modo: 'crear', idParam: null }
  // Igual que 'create': como máximo dos segmentos ('edit' + el id) — un tercer segmento
  // (`/articulos/edit/5/extra`) no es una edición válida, no un id con sufijo ignorado.
  if (segmentos[0] === 'edit' && segmentos.length <= 2) return { modo: 'editar', idParam: segmentos[1] ?? null }
  return { modo: null, idParam: null }
}

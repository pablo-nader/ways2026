import type { FormularioOferta } from '../../api/ofertas'

export function validarFormulario(f: FormularioOferta): string | null {
  if (!f.nombre.trim()) return 'Ingresá un nombre para la oferta.'
  if (f.alcance === 'Articulo' && f.idArticulo === '') return 'Elegí un artículo para el alcance.'
  if (f.alcance === 'Grupo' && f.idGrupo === '') return 'Elegí un grupo para el alcance.'
  if (f.alcance === 'Categoria' && f.idCategoria === '') return 'Elegí una categoría para el alcance.'
  if (f.alcance === 'Familia' && f.idFamilia === '') return 'Elegí una familia para el alcance.'
  if (f.beneficio === 'PrecioUnitario' && f.precioUnitario.trim() === '') return 'Ingresá el precio unitario del beneficio.'
  if (f.beneficio === 'Porcentaje' && f.porcentaje.trim() === '') return 'Ingresá el porcentaje del beneficio.'
  if (f.beneficio === 'ImporteFijo' && f.importeFijo.trim() === '') return 'Ingresá el importe fijo del beneficio.'
  return null
}

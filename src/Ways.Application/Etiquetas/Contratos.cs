using Ways.Application.Ofertas;

namespace Ways.Application.Etiquetas;

/// <summary>stage-18-etiquetas-y-consulta, Slice 2 (task 2.12; design.md:182-183). Los cuatro
/// mismos ejes que <c>ServicioDeArticulos.ListarAsync</c> gana en esta slice, más
/// <see cref="SoloConOfertaVigente"/> — decidido ÍNTEGRAMENTE por
/// <see cref="ServicioDeOfertas.ResolverAsync"/> (<c>Aplicadas.Count &gt; 0</c>), nunca por un
/// segundo matching de ofertas acá (design decisión 6, proposal decisión 12).</summary>
public sealed record FiltroDeEtiquetas(
    string? Busqueda, int? IdArea, int? IdCategoria, int? IdMarca, bool SoloConOfertaVigente = false);

/// <summary>stage-18-etiquetas-y-consulta, Slice 2 (task 2.12; design.md:186-187, decisión 12).
/// Sin <c>Momento</c> (decisión 10 — el server resuelve UN instante para toda la hoja y lo
/// devuelve en <see cref="DatosDeEtiquetas.Momento"/>) y sin <c>copias</c> (Reconciliación T1 de
/// tasks.md — un multiplicador de impresión es un concern de pantalla, <c>dto-contract-honesty</c>
/// regla 1: un campo sin destino en el servidor no tiene lugar en el contrato).
/// <see cref="IdsArticulo"/> XOR <see cref="Filtro"/>: ambos ⇒ 400 <c>seleccion_ambigua</c>;
/// ninguno ⇒ 400 <c>seleccion_requerida</c> (design decisión 12, <see cref="ServicioDeEtiquetas"/>).</summary>
public sealed record SolicitudDeEtiquetas(
    int IdPuntoVenta, int IdListaPrecio, IReadOnlyList<int>? IdsArticulo, FiltroDeEtiquetas? Filtro);

/// <summary>stage-18-etiquetas-y-consulta, Slice 2 (task 2.12; design.md:189-195).
///
/// <para><b>CLÁUSULA DE EXPOSICIÓN</b> (decisión 10 del proposal, skill <c>dto-contract-honesty</c>
/// — EL invariante de esta etapa): este record NO declara —ni declarará jamás— <c>costo_lista</c>,
/// <c>costo_nominal</c>, <c>descuento_proveedor</c>, <c>id_proveedor_habitual</c>, <c>proveedor</c>
/// ni <c>margen</c>. No están ocultos en la UI: están AUSENTES del contrato. Una hoja impresa se
/// va del local, a cualquier persona que la levante del piso. El costo es admin-only por política
/// (<c>Politicas.LecturaDeRentabilidad</c>) — este DTO nunca lo transporta, sin importar qué rol
/// llame al endpoint. Mutation target 22: la prueba de exposición recorre el JSON serializado y
/// busca esos nombres de PROPIEDAD exactos, nunca un substring (<c>OfertaAplicadaDto.
/// DescuentoUnitario</c> contiene legítimamente la palabra "descuento").</para></summary>
public sealed record FilaDeEtiqueta(
    int IdArticulo, string CodigoInterno, string? CodigoBarra, string Nombre, string UnidadVenta,
    decimal PrecioOriginal, decimal PrecioFinal, IReadOnlyList<OfertaAplicadaDto> Ofertas);

/// <summary>stage-18-etiquetas-y-consulta, Slice 2 (task 2.12; design.md:197, Reconciliación T3 de
/// tasks.md). Un artículo sin precio vigente en la lista elegida NUNCA emite una
/// <see cref="FilaDeEtiqueta"/> (nunca <c>$0</c>) — se mueve acá, CON identidad, no solo un
/// contador: una cuenta no puede marcar una fila en la pantalla de selección
/// (spec: "the selection list to mark them").</summary>
public sealed record ArticuloExcluido(int IdArticulo, string CodigoInterno, string Nombre, string Motivo);

/// <summary>stage-18-etiquetas-y-consulta, Slice 2 (task 2.12; design.md:199-201).
/// <see cref="NombreDeLista"/> se lee de <c>listas_precio</c> por el SERVIDOR (decisión 11 —
/// nunca la etiqueta que el selector del cliente ya tenía, para que el nombre impreso y la lista
/// realmente usada para tasar sean SIEMPRE la misma lectura). <see cref="Momento"/> se resuelve
/// una sola vez para toda la hoja y se ECHA de vuelta (decisión 10).</summary>
public sealed record DatosDeEtiquetas(
    int IdListaPrecio, string NombreDeLista, DateTimeOffset Momento,
    IReadOnlyList<FilaDeEtiqueta> Filas, IReadOnlyList<ArticuloExcluido> Excluidos, bool Truncado);

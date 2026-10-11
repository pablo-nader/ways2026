namespace Ways.Domain.Ofertas;

/// <summary>
/// Proyección total del alcance de una <see cref="Oferta"/> (design decision 2): exactamente
/// uno de los cuatro factory methods construye la instancia, así que el resolver (Slice 3)
/// nunca ve las cuatro columnas nullable crudas — el invariante de exclusividad ya quedó
/// resuelto por <see cref="ReglaDeOfertas.LeerAlcance"/> antes de que exista un valor de este
/// tipo.
/// </summary>
public readonly record struct AlcanceDeOferta
{
    private AlcanceDeOferta(int? idArticulo, int? idGrupo, int? idCategoria, int? idFamilia)
    {
        IdArticulo = idArticulo;
        IdGrupo = idGrupo;
        IdCategoria = idCategoria;
        IdFamilia = idFamilia;
    }

    public int? IdArticulo { get; }
    public int? IdGrupo { get; }
    public int? IdCategoria { get; }
    public int? IdFamilia { get; }

    public static AlcanceDeOferta DeArticulo(int idArticulo) => new(idArticulo, null, null, null);
    public static AlcanceDeOferta DeGrupo(int idGrupo) => new(null, idGrupo, null, null);
    public static AlcanceDeOferta DeCategoria(int idCategoria) => new(null, null, idCategoria, null);
    public static AlcanceDeOferta DeFamilia(int idFamilia) => new(null, null, null, idFamilia);
}

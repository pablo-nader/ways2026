namespace Ways.Domain.Catalogos;

/// <summary>
/// Lista de precios del tenant (doc 10 §3). Reusa <see cref="CatalogoSimple"/> (design
/// decision 1): a diferencia de <c>Cliente</c>/<c>Proveedor</c>, su forma (nombre + flags,
/// dedupe estándar por nombre/alcance) calza genuinamente con la base genérica — solo capa
/// EF esta etapa, sin <c>Servicio*</c>/API (spec: listas_precio ABM Is Out of Scope This
/// Stage). <see cref="Modo"/>/<see cref="IdListaBase"/>/<see cref="Porcentaje"/> existen ya
/// (doc 10, principio de modelo completo por adelantado) pero solo <see cref="ModoLista.Fija"/>
/// se usa en esta etapa — <c>precios</c> y las listas derivadas llegan en la etapa 3.
/// </summary>
public class ListaPrecio : CatalogoSimple
{
    /// <summary>Exactamente una fila por alcance (tenant compartido o tenant+empresa) tiene
    /// que tener <c>true</c> — <c>ux_listas_precio_default_compartido/empresa</c> lo exige a
    /// nivel de esquema.</summary>
    public bool EsDefault { get; set; }

    public ModoLista Modo { get; set; } = ModoLista.Fija;

    /// <summary>Solo tiene sentido cuando <see cref="Modo"/> es <see cref="ModoLista.Derivada"/>
    /// — sin uso hasta la etapa 3.</summary>
    public int? IdListaBase { get; set; }

    /// <summary>Idem <see cref="IdListaBase"/>: sin uso hasta la etapa 3.</summary>
    public decimal? Porcentaje { get; set; }
}

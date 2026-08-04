using Ways.Domain.Common;

namespace Ways.Domain.Ofertas;

/// <summary>
/// Regla de descuento del tenant (doc 10 §Ofertas, deviado por stage-4 decisión 4: la
/// columna <c>id_lista_precio</c> del doc se reemplaza por la junction <see cref="OfertaLista"/>).
/// Catálogo-scoped (<c>id_tenant</c> NOT NULL, <see cref="IdEmpresa"/> NULL = todo el tenant,
/// doc 09 §Catálogo).
///
/// Design decision 1: mantiene las columnas nullable CRUDAS de doc 10 para los dos grupos
/// exclusivos (alcance: articulo/grupo/categoria; beneficio: precio_unitario/porcentaje/
/// importe_fijo) — el invariante vive en <see cref="ReglaDeOfertas"/> (pura) Y en las dos
/// CHECKs de esquema (<c>ck_ofertas_alcance_exclusivo</c>/<c>ck_ofertas_beneficio_exclusivo</c>).
/// Los tipos discriminados/owned types se descartaron: pelearían contra las CHECKs de
/// <c>num_nonnulls</c> y contra la query de candidatos de la etapa de resolución (Slice 3),
/// que necesita las columnas crudas como predicados <c>= ANY(...)</c> planos.
/// </summary>
public class Oferta : EntidadTenant
{
    public int Id { get; set; }

    /// <summary><c>NULL</c> ⇒ toda empresa del tenant (default). Alcance opcional a una
    /// empresa puntual, igual que el resto de los catálogos (doc 09 §Catálogo).</summary>
    public int? IdEmpresa { get; set; }

    /// <summary>Lo que imprime el ticket — a propósito NO es único (design decision 6): dos
    /// ofertas "2x1 Verano" con ventanas distintas son legítimas.</summary>
    public required string Nombre { get; set; }

    /// <summary>Alcance: exactamente uno de los tres tiene que estar seteado —
    /// <see cref="ReglaDeOfertas.LeerAlcance"/> valida el invariante antes de cualquier
    /// escritura; <c>ck_ofertas_alcance_exclusivo</c> es el backstop de esquema.</summary>
    public int? IdArticulo { get; set; }
    public int? IdGrupo { get; set; }
    public int? IdCategoria { get; set; }

    /// <summary>Vigencia por fecha — cada eje es independientemente opcional (NULL = sin
    /// restricción en ese eje), inclusivo en ambos extremos cuando está seteado.</summary>
    public DateOnly? FechaDesde { get; set; }
    public DateOnly? FechaHasta { get; set; }

    /// <summary>Vigencia por hora del día — mismo criterio que <see cref="FechaDesde"/>.</summary>
    public TimeOnly? HoraDesde { get; set; }
    public TimeOnly? HoraHasta { get; set; }

    /// <summary>Días de semana ISO-8601 (1 = lunes … 7 = domingo) en los que aplica —
    /// <c>NULL</c>/vacío = todos. Mapea a <c>smallint[]</c> nativo de Npgsql, sin converter.</summary>
    public short[]? DiasSemana { get; set; }

    /// <summary><c>NULL</c> ⇒ la oferta aplica sin importar la cantidad ("oferta directa").
    /// Seteada ⇒ aplica solo cuando la cantidad pedida es <c>&gt;=</c> este valor.</summary>
    public decimal? CantidadMinima { get; set; }

    /// <summary>Beneficio: exactamente uno de los tres tiene que estar seteado — mismo
    /// invariante que <see cref="IdArticulo"/>/<see cref="IdGrupo"/>/<see cref="IdCategoria"/>,
    /// validado por <see cref="ReglaDeOfertas.LeerBeneficio"/> y respaldado por
    /// <c>ck_ofertas_beneficio_exclusivo</c>.</summary>
    public decimal? PrecioUnitario { get; set; }
    public decimal? Porcentaje { get; set; }
    public decimal? ImporteFijo { get; set; }

    /// <summary>Ante solapamiento de varias ofertas <c>acumulable = false</c>, gana la de
    /// mayor prioridad (Slice 3: <c>ResolvedorDeOfertas</c>).</summary>
    public int Prioridad { get; set; }

    /// <summary><c>true</c> ⇒ se suma a otras ofertas aplicables; <c>false</c> ⇒ compite por
    /// ser la base (Slice 3).</summary>
    public bool Acumulable { get; set; }

    public bool Activo { get; set; } = true;
}

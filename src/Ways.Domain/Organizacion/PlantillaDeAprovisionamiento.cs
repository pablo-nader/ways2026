using Ways.Domain.Catalogos;

namespace Ways.Domain.Organizacion;

/// <summary>
/// Contenido con el que se provisiona un tenant nuevo (ADR-16). Versionada a propósito
/// (<see cref="V1"/>): una plantilla nueva para un vertical futuro se agrega como versión,
/// no se edita ésta.
/// </summary>
public static class PlantillaDeAprovisionamiento
{
    public static readonly PlantillaV1 V1 = new(
        Area: "General",
        MediosDePago:
        [
            new PlantillaMedioPago(
                "Efectivo", ComportamientoMedioPago.Efectivo, AdmiteVuelto: true, RequiereReferencia: false),
            new PlantillaMedioPago(
                "Transferencia", ComportamientoMedioPago.Electronico, AdmiteVuelto: false, RequiereReferencia: true)
        ]);
}

public sealed record PlantillaV1(string Area, IReadOnlyList<PlantillaMedioPago> MediosDePago)
{
    /// <summary>Extensiones declaradas para etapas 2/3, deliberadamente NO creadas en esta
    /// etapa (ADR-16): la lista de precios genérica y el cliente "Consumidor Final" necesitan
    /// tablas que todavía no existen (<c>listas_precio</c>, <c>clientes</c>). Se dejan
    /// nombradas acá para que un tenant provisionado hoy quede con un gap visible y
    /// documentado, no silenciosamente incompleto.</summary>
    public static readonly IReadOnlyList<string> ItemsDiferidos =
    [
        "lista_precio_general (etapa 3: listas_precio no existe todavía)",
        "cliente_consumidor_final (etapa 2: clientes no existe todavía)"
    ];
}

public sealed record PlantillaMedioPago(
    string Nombre, ComportamientoMedioPago Comportamiento, bool AdmiteVuelto, bool RequiereReferencia);

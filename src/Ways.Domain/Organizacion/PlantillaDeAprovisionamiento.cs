using Ways.Domain.Catalogos;

namespace Ways.Domain.Organizacion;

/// <summary>
/// Contenido con el que se provisiona un tenant nuevo (ADR-16). Versionada a propósito
/// (<see cref="V1"/>): una plantilla nueva para un vertical futuro se agrega como versión,
/// no se edita ésta.
///
/// <see cref="PlantillaV1.ClienteConsumidorFinal"/>/<see cref="PlantillaV1.ListaPrecioGeneral"/>
/// (stage-2-clientes-proveedores, design decision 5) cierran el gap que <c>ItemsDiferidos</c>
/// dejaba declarado desde la etapa 1: ahora que <c>clientes</c>/<c>listas_precio</c> existen,
/// completar V1 en el lugar es terminar su propio roadmap, no agregar un vertical de negocio
/// distinto — por eso no es un bump a V2 (ADR-16 es para eso otro).
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
        ],
        ListaPrecioGeneral: new PlantillaListaPrecio("General"),
        ClienteConsumidorFinal: new PlantillaClienteConsumidorFinal("Consumidor Final", "CF"));
}

public sealed record PlantillaV1(
    string Area,
    IReadOnlyList<PlantillaMedioPago> MediosDePago,
    PlantillaListaPrecio ListaPrecioGeneral,
    PlantillaClienteConsumidorFinal ClienteConsumidorFinal);

public sealed record PlantillaMedioPago(
    string Nombre, ComportamientoMedioPago Comportamiento, bool AdmiteVuelto, bool RequiereReferencia);

/// <summary>Se crea con <c>modo = fija</c>, <c>es_default = true</c> (design: Table Shapes).
/// <c>numero</c> no aparece acá: lo entrega
/// <c>AsignadorDeNumeroCliente.AsignarSiguienteAsync</c> sobre un contador recién creado,
/// siempre <c>1</c> por construcción.</summary>
public sealed record PlantillaListaPrecio(string Nombre);

/// <summary><paramref name="CodigoCondicionFiscal"/> resuelve contra <c>condiciones_fiscales</c>
/// (catálogo global, sembrado por <c>InicializadorDeBaseDeDatos.SembrarCatalogosFiscalesAsync</c>
/// — siempre disponible antes de que se aprovisione o backfillee cualquier tenant).</summary>
public sealed record PlantillaClienteConsumidorFinal(string Nombre, string CodigoCondicionFiscal);

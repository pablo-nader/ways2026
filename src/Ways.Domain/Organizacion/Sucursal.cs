using Ways.Domain.Common;

namespace Ways.Domain.Organizacion;

/// <summary>
/// El local físico de una empresa: el nivel entre la empresa y el punto de venta (tenant →
/// empresa → sucursal → punto de venta). En esta etapa cada punto de venta tiene la suya (1:1) y
/// el stock sigue colgado del punto de venta; las etapas siguientes mueven el stock acá.
/// </summary>
public class Sucursal : EntidadTenant
{
    public int Id { get; set; }

    /// <summary>FK compuesta a <see cref="Empresa"/> (ver configuración de EF): una fila de un
    /// tenant no puede referenciar la empresa de otro tenant ni por bug.</summary>
    public int IdEmpresa { get; set; }

    public required string Nombre { get; set; }

    /// <summary>La sucursal que absorbió a esta en una fusión; nula si nunca se fusionó. FK compuesta: misma empresa y
    /// mismo tenant. Una reversa de un movimiento guardado acá sigue la cadena hasta la sucursal sin fusión.</summary>
    public int? IdSucursalFusion { get; set; }

    /// <summary>Dirección estructurada, toda opcional (ver <see cref="DireccionDeSucursal"/>).</summary>
    public string? Calle { get; set; }

    public string? Altura { get; set; }

    public string? Ciudad { get; set; }

    /// <summary>Nombre canónico de una de las 24 jurisdicciones (<see cref="ProvinciasArgentinas"/>).</summary>
    public string? Provincia { get; set; }

    public string? CodigoPostal { get; set; }

    /// <summary>Latitud y longitud van juntas o ninguna.</summary>
    public decimal? Latitud { get; set; }

    public decimal? Longitud { get; set; }

    public string? Referencia { get; set; }

    public DireccionDeSucursal Direccion =>
        new(Calle, Altura, Ciudad, Provincia, CodigoPostal, Latitud, Longitud, Referencia);

    public void AplicarDireccion(DireccionDeSucursal direccion)
    {
        Calle = direccion.Calle;
        Altura = direccion.Altura;
        Ciudad = direccion.Ciudad;
        Provincia = direccion.Provincia;
        CodigoPostal = direccion.CodigoPostal;
        Latitud = direccion.Latitud;
        Longitud = direccion.Longitud;
        Referencia = direccion.Referencia;
    }
}

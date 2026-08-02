using Ways.Domain.Common;

namespace Ways.Domain.Proveedores;

/// <summary>
/// Proveedor del tenant (doc 10 §2, catálogo-scoped: <c>id_tenant</c> + <c>id_empresa</c>
/// opcional, doc 09). Entidad dedicada (design decision 1): no reusa
/// <c>ConfiguracionDeCatalogo&lt;T&gt;</c>/<c>ServicioDeCatalogo&lt;T&gt;</c> porque dedupe
/// por <see cref="Cuit"/> tenant-wide (spec: cuit Uniqueness Is Scoped Per Tenant), no por
/// nombre/empresa-par como esa base asume.
/// </summary>
public class Proveedor : EntidadTenant
{
    public int Id { get; set; }

    /// <summary><c>NULL</c> ⇒ compartido por todas las empresas del tenant (ADR-10).</summary>
    public int? IdEmpresa { get; set; }

    public required string RazonSocial { get; set; }
    public string? NombreFantasia { get; set; }

    /// <summary>Único por tenant (partial index, <c>NULL</c> permitido y no comparado) —
    /// no por <c>(id_tenant, id_empresa)</c>: el mismo proveedor puede repetirse entre
    /// empresas del mismo tenant sin que sea un duplicado de carga de datos.</summary>
    public string? Cuit { get; set; }

    public int IdCondicionFiscal { get; set; }

    public string? Domicilio { get; set; }
    public string? Telefono { get; set; }
    public string? Email { get; set; }

    public string? Vendedor { get; set; }
    public string? CelularVendedor { get; set; }
    public string? Supervisor { get; set; }
    public string? CelularSupervisor { get; set; }

    public decimal? Margen { get; set; }
    public string? Observaciones { get; set; }

    public bool Activo { get; set; } = true;
}

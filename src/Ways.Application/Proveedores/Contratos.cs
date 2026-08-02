namespace Ways.Application.Proveedores;

public record ProveedorListado(
    int Id,
    string RazonSocial,
    string? NombreFantasia,
    string? Cuit,
    int IdCondicionFiscal,
    string? Domicilio,
    string? Telefono,
    string? Email,
    string? Vendedor,
    string? CelularVendedor,
    string? Supervisor,
    string? CelularSupervisor,
    decimal? Margen,
    string? Observaciones,
    bool Activo,
    int? IdEmpresa);

/// <summary><see cref="Cuit"/> es único por tenant cuando se lo provee (spec: cuit Uniqueness
/// Is Scoped Per Tenant) — <c>NULL</c> permitido y nunca comparado contra otra fila.
/// <see cref="IdCondicionFiscal"/> es requerido, mismo criterio que <c>AltaCliente</c>.</summary>
public record AltaProveedor(
    string RazonSocial,
    string? NombreFantasia,
    string? Cuit,
    int IdCondicionFiscal,
    string? Domicilio,
    string? Telefono,
    string? Email,
    string? Vendedor,
    string? CelularVendedor,
    string? Supervisor,
    string? CelularSupervisor,
    decimal? Margen,
    string? Observaciones,
    int? IdEmpresa = null,
    bool Activo = true);

public record EdicionProveedor(
    string RazonSocial,
    string? NombreFantasia,
    string? Cuit,
    int IdCondicionFiscal,
    string? Domicilio,
    string? Telefono,
    string? Email,
    string? Vendedor,
    string? CelularVendedor,
    string? Supervisor,
    string? CelularSupervisor,
    decimal? Margen,
    string? Observaciones,
    int? IdEmpresa,
    bool Activo);

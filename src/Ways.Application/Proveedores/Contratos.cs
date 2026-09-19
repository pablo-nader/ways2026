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

/// <summary>JD-A1 (judgment-day): proyección MÍNIMA para selectores fuera de la gestión de
/// catálogo (<c>GET /api/proveedores/opciones</c>, <c>Politicas.OperacionDePos</c>) — nunca
/// <see cref="ProveedorListado.Margen"/>, <see cref="ProveedorListado.Cuit"/> ni ningún otro
/// dato de contacto/negocio; el listado completo sigue exigiendo
/// <c>Politicas.GestionDeCatalogo</c>.</summary>
public record OpcionDeProveedor(int Id, string RazonSocial, string? NombreFantasia);

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

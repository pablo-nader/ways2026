using Ways.Domain.Clientes;

namespace Ways.Application.Clientes;

public record ClienteListado(
    int Id,
    int Numero,
    string Nombre,
    string? Apellido,
    string? RazonSocial,
    TipoDocumento? TipoDocumento,
    string? NumeroDocumento,
    int IdCondicionFiscal,
    DateOnly? Nacimiento,
    string? Domicilio,
    string? Telefono,
    string? Celular,
    string? Email,
    string? Observaciones,
    int IdListaPrecio,
    decimal LimiteCredito,
    bool CreditoIlimitado,
    decimal Saldo,
    bool Activo,
    int? IdEmpresa,
    bool EsConsumidorFinal);

/// <summary><see cref="Numero"/> no aparece acá a propósito: lo asigna
/// <see cref="AsignadorDeNumeroCliente"/>, nunca es input de cliente (spec: Atomic Per-Tenant
/// Numero Assignment). <see cref="IdCondicionFiscal"/>/<see cref="IdListaPrecio"/> son
/// requeridos (spec: "id_lista_precio and id_condicion_fiscal are required") — sin default
/// automático cuando se omiten, el alta se rechaza antes de tocar la base.</summary>
public record AltaCliente(
    string Nombre,
    string? Apellido,
    string? RazonSocial,
    TipoDocumento? TipoDocumento,
    string? NumeroDocumento,
    int IdCondicionFiscal,
    DateOnly? Nacimiento,
    string? Domicilio,
    string? Telefono,
    string? Celular,
    string? Email,
    string? Observaciones,
    int IdListaPrecio,
    decimal LimiteCredito = 0,
    bool CreditoIlimitado = false,
    int? IdEmpresa = null,
    bool Activo = true);

/// <summary><see cref="Saldo"/> no aparece: no hay motor de cuenta corriente todavía (etapa
/// 7, doc 10 §2) — no es un campo editable por el ABM de esta etapa.</summary>
public record EdicionCliente(
    string Nombre,
    string? Apellido,
    string? RazonSocial,
    TipoDocumento? TipoDocumento,
    string? NumeroDocumento,
    int IdCondicionFiscal,
    DateOnly? Nacimiento,
    string? Domicilio,
    string? Telefono,
    string? Celular,
    string? Email,
    string? Observaciones,
    int IdListaPrecio,
    decimal LimiteCredito,
    bool CreditoIlimitado,
    int? IdEmpresa,
    bool Activo);

/// <summary>Referencia mínima para poblar el selector de lista de precios del formulario de
/// clientes — no es un listado de ABM (spec: listas_precio ABM Is Out of Scope This Stage,
/// design decision 1): mismo criterio que <c>RolListado</c>/<c>RolesAsignablesAsync</c> en
/// <see cref="Usuarios.ServicioDeUsuarios"/>, sin <c>Servicio</c>/<c>Endpoints</c> propios
/// para <c>listas_precio</c>.</summary>
public record ListaPrecioAsignable(int Id, string Nombre, bool EsDefault);

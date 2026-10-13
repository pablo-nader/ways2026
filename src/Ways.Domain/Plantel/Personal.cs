using Ways.Domain.Common;

namespace Ways.Domain.Plantel;

/// <summary>
/// Persona que trabaja para la empresa (doc 10, catálogo-scoped: <c>id_tenant</c> + <c>id_empresa</c>
/// opcional). El nombre evita "empleado" porque <c>id_empleado</c> ya significa el usuario que opera.
/// Los vínculos con un usuario y con un cliente son opcionales e independientes.
/// </summary>
public class Personal : EntidadTenant
{
    public int Id { get; set; }

    /// <summary><c>NULL</c> ⇒ compartido por todas las empresas del tenant (ADR-10).</summary>
    public int? IdEmpresa { get; set; }

    public required string Nombre { get; set; }
    public string? Documento { get; set; }
    public string? Telefono { get; set; }
    public string? Observaciones { get; set; }

    /// <summary>Valor por defecto de la próxima carga de horas; no es el valor de cargas ya hechas.</summary>
    public decimal ValorHora { get; set; }

    /// <summary>Cuenta de acceso del propio empleado. A lo sumo un personal vivo por usuario.</summary>
    public int? IdUsuario { get; set; }

    /// <summary>Cliente que representa al empleado como comprador. A lo sumo un personal vivo por cliente.</summary>
    public int? IdCliente { get; set; }

    /// <summary>Caché del libro <c>movimientos_personal</c>. Positivo: la empresa le debe al empleado.
    /// Solo la escritura atómica <c>UPDATE ... RETURNING</c> junto con el asiento la mueve.</summary>
    public decimal Saldo { get; set; }
}

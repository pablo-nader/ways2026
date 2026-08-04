using Ways.Domain.Common;

namespace Ways.Domain.Clientes;

/// <summary>
/// Cliente del tenant (doc 10 §2, catálogo-scoped: <c>id_tenant</c> + <c>id_empresa</c>
/// opcional, doc 09). Entidad dedicada (design decision 1): no reusa
/// <c>ConfiguracionDeCatalogo&lt;T&gt;</c>/<c>ServicioDeCatalogo&lt;T&gt;</c> porque su
/// identidad no es un <c>nombre</c> único por alcance (es nombre+apellido+razón social,
/// combinable) y su <see cref="Numero"/> lo asigna
/// <see cref="Application.Clientes.AsignadorDeNumeroCliente"/> (contador atómico), no es
/// input de usuario deduplicado por unicidad de nombre.
///
/// <see cref="Numero"/> == 1 identifica siempre al Consumidor Final protegido de su tenant
/// (<see cref="ReglaDeClientes"/>, <c>ck_clientes_cf_protegido</c>). <see cref="Saldo"/> deja
/// de estar dormant en esta etapa (stage-5-pos-ventas, Slice 3): pasa a ser el caché
/// mantenido del ledger de <c>movimientos_cuenta_corriente</c>, escrito dentro de la misma
/// transacción de venta/anulación (Slice 4/5).
/// </summary>
public class Cliente : EntidadTenant
{
    public int Id { get; set; }

    /// <summary><c>NULL</c> ⇒ compartido por todas las empresas del tenant (mismo criterio
    /// que <c>CatalogoSimple.IdEmpresa</c>, ADR-10).</summary>
    public int? IdEmpresa { get; set; }

    /// <summary>Correlativo atómico por tenant (design decision 2). <c>1</c> ⇒ Consumidor
    /// Final.</summary>
    public int Numero { get; set; }

    public required string Nombre { get; set; }
    public string? Apellido { get; set; }
    public string? RazonSocial { get; set; }

    /// <summary><c>NULL</c> para el Consumidor Final y para clientes históricos sin
    /// documento cargado.</summary>
    public TipoDocumento? TipoDocumento { get; set; }

    /// <summary>Sin restricción de unicidad a ningún alcance (spec: numero_documento Has No
    /// Uniqueness Constraint) — decisión de producto documentada, no un olvido.</summary>
    public string? NumeroDocumento { get; set; }

    public int IdCondicionFiscal { get; set; }

    public DateOnly? Nacimiento { get; set; }

    public string? Domicilio { get; set; }
    public string? Telefono { get; set; }
    public string? Celular { get; set; }
    public string? Email { get; set; }

    public string? Observaciones { get; set; }

    public int IdListaPrecio { get; set; }

    public decimal LimiteCredito { get; set; }
    public bool CreditoIlimitado { get; set; }

    /// <summary>Caché mantenido de <c>Σ movimientos_cuenta_corriente.importe</c> (stage-5-pos-ventas,
    /// Slice 3 crea la tabla; Slice 4/5 son los únicos escritores, dentro de la transacción de
    /// venta/anulación). Hasta entonces se mantiene en su valor de siembra/default.</summary>
    public decimal Saldo { get; set; }

    public bool Activo { get; set; } = true;

    public bool EsConsumidorFinal => ReglaDeClientes.EsConsumidorFinal(Numero);
}

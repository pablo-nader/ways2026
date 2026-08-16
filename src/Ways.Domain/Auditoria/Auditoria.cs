namespace Ways.Domain.Auditoria;

/// <summary>
/// Fila append-only del log de auditoría (design: Interfaces/Contracts, proposal §A del gate).
/// A propósito NO hereda de <see cref="Common.EntidadBase"/>/<see cref="Common.EntidadTenant"/>
/// — un hecho inmutable no tiene <c>updated_at</c> ni baja lógica, mismo criterio que
/// <see cref="Stock.MovimientoStock"/>. Tampoco expone mutadores: el único escritor legítimo es
/// <see cref="Ways.Application.Auditoria.ServicioDeAuditoria"/>, que arma la fila entera de una
/// vez (constructor de objeto, nunca una asignación posterior).
///
/// <see cref="IdTenant"/> es el tenant del SUJETO auditado, no del actor (proposal decisión 1) —
/// por eso esta clase escribe su propio filtro de tenant
/// (<c>WaysDbContext.AplicarFiltroDeTenantEnAuditoria</c>, design decisión 7) en vez de heredar
/// <see cref="Common.EntidadTenant"/>, cuyo <c>EstamparTenant()</c> pisaría el valor con el
/// tenant de la SESIÓN.
/// </summary>
public class Auditoria
{
    public long Id { get; set; }
    public int IdTenant { get; set; }

    /// <summary><c>NULL</c> para las acciones tenant-wide (<c>precio.*</c>, <c>usuario.*</c>) —
    /// design decisión 7, proposal §A.</summary>
    public int? IdPuntoVenta { get; set; }

    /// <summary>Siempre <c>contexto.UsuarioId</c>, estampado por el writer — nunca un parámetro
    /// del llamador (design decisión 2).</summary>
    public int IdActor { get; set; }

    /// <summary>'<c>&lt;dominio&gt;.&lt;operacion&gt;</c>' — el catálogo vive en
    /// <see cref="AccionAuditada"/>, la base solo exige no-vacío (proposal decisión 8).</summary>
    public string Accion { get; set; } = string.Empty;

    /// <summary>El agregado que un humano busca ('articulo', 'usuario', 'comprobante_venta', …)
    /// — nunca la fila del ledger que originó el registro.</summary>
    public string Entidad { get; set; } = string.Empty;

    public int IdEntidad { get; set; }

    /// <summary>Documento jsonb ya serializado por
    /// <see cref="Ways.Application.Auditoria.SerializadorDeAuditoria"/> — <c>NULL</c> si no había
    /// estado previo o la acción es un hecho puro (design decisión 3).</summary>
    public string? ValorAnterior { get; set; }

    /// <summary>Documento jsonb ya serializado — nunca vacío (proposal §A).</summary>
    public string ValorNuevo { get; set; } = string.Empty;

    /// <summary>Siempre <c>reloj.Ahora</c>, estampado por el writer — sin <c>DEFAULT</c> en la
    /// base (design decisión 2, proposal §A: <c>IRelojDelSistema</c> es la única fuente de
    /// tiempo).</summary>
    public DateTimeOffset CreadoEl { get; set; }
}

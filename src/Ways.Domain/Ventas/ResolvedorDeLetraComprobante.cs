namespace Ways.Domain.Ventas;

/// <summary>Los cinco códigos de <c>condiciones_fiscales</c> (doc 10 §1: RI, MONOTRIBUTO,
/// EXENTO, CF, NO_RESP) como enum cerrado — solo para la interfaz de
/// <see cref="ResolvedorDeLetraComprobante"/> (dormant): la tabla real sigue siendo
/// <c>citext codigo</c> (global, sin FK desde acá, ADR-11), este enum no se persiste.</summary>
public enum CondicionFiscalCodigo
{
    ResponsableInscripto,
    Monotributo,
    Exento,
    ConsumidorFinal,
    NoResponsable
}

/// <summary>
/// Resuelve la letra de un comprobante fiscal (A/B/C) por el cruce condición fiscal emisor ×
/// condición fiscal receptor (doc 10 §1 "Regla de la letra"; design decisión 8). Pura, sin acceso
/// a base de datos.
///
/// <b>YA NO DORMANT (stage-19a, Slice 5, task 5.8)</b>: <c>ServicioDeFacturacionFiscal</c> es su
/// PRIMER caller real (design.md data flow, "SU PRIMER CALLER") — la emisión fiscal end-to-end
/// contra mocks lo invoca después de los cinco gates (D10), para informar la letra efectiva del
/// comprobante emitido. El POS ordinario (<c>ServicioDeVentas</c>) sigue sin invocarlo: TX/NCX/TXR/RC
/// (<c>es_fiscal = false</c>) nunca tienen letra, esa mitad de la regla no cambió.
///
/// Regla explícita de doc 10: <c>RI → RI</c> emite A; <c>RI → </c>cualquier otra cosa emite B;
/// un emisor Monotributo emite C a todos. Doc 10 no especifica el resto de los emisores
/// (Exento/Consumidor Final/No Responsable como EMISOR es un caso de negocio atípico — esas
/// condiciones nunca discriminan IVA) — esta clase los trata igual que Monotributo (C a todos),
/// una extensión conservadora explícita, no una laguna silenciosa: ningún camino de escritura
/// depende de esta rama hoy.
/// </summary>
public static class ResolvedorDeLetraComprobante
{
    public static char Resolver(CondicionFiscalCodigo emisor, CondicionFiscalCodigo receptor) =>
        emisor switch
        {
            CondicionFiscalCodigo.ResponsableInscripto =>
                receptor == CondicionFiscalCodigo.ResponsableInscripto ? 'A' : 'B',

            // Monotributo emite C a todos (doc 10 §1) — Exento/ConsumidorFinal/NoResponsable
            // como emisor caen en la misma rama (ver doc de la clase: extensión conservadora).
            _ => 'C'
        };
}

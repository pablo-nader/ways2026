using Ways.Domain.Common;

namespace Ways.Domain.Fiscal;

/// <summary>
/// Certificado X.509 fiscal por empresa+ambiente (proposal.md §E, decisiones 1/5). Scoping
/// <c>id_tenant + id_empresa NOT NULL</c> — DESVIACIÓN DOCUMENTADA del catálogo doc-09
/// (<c>id_empresa NULL</c> = compartido): un certificado es de UN CUIT y nunca se comparte
/// (decisión 5), misma forma que <see cref="Organizacion.PuntoVenta"/>. <c>EntidadBase</c>: SÍ —
/// la rotación da de baja lógica la fila superada (<c>ServicioDeCertificados</c>, slice 4) y las
/// columnas de auditoría son exactamente lo que una tabla de material de clave necesita.
///
/// <c>ClavePrivadaCifrada</c>/<c>Nonce</c>/<c>TagAutenticacion</c> son AES-256-GCM (decisión 1):
/// nunca aparecen en un DTO, log ni respuesta de API — <c>dto-contract-honesty</c>. La clave
/// maestra que las descifra viene de configuración/entorno, jamás de esta fila ni del repo.
/// </summary>
public class CertificadoFiscal : EntidadTenant
{
    public int Id { get; set; }

    public int IdEmpresa { get; set; }

    public AmbienteFiscal Ambiente { get; set; }

    /// <summary>Etiqueta humana ("Homo 2026") — nunca usada para resolver el certificado activo.</summary>
    public required string Alias { get; set; }

    /// <summary>CUIT al que ARCA emitió el certificado (11 dígitos, sin guiones).</summary>
    public required string CuitTitular { get; set; }

    /// <summary>Parte PÚBLICA del X.509 — no es secreto, puede viajar en un DTO.</summary>
    public required string CertificadoPem { get; set; }

    /// <summary>AES-256-GCM (decisión 1). AAD = <c>v1|id_tenant|id_empresa|ambiente|huella_sha256</c>
    /// (design D5) — ata el ciphertext a SU fila; mover el blob a otra fila falla la
    /// autenticación.</summary>
    public required byte[] ClavePrivadaCifrada { get; set; }

    /// <summary>12 bytes — el tamaño de nonce de GCM (CHECK 6).</summary>
    public required byte[] Nonce { get; set; }

    /// <summary>16 bytes — el tamaño de tag de GCM (CHECK 6).</summary>
    public required byte[] TagAutenticacion { get; set; }

    /// <summary>Versión de la clave maestra que cifró esta fila (rotación fila por fila, sin
    /// downtime) — EXCLUIDA del AAD a propósito (design D5): la clave ya está seleccionada por
    /// esta columna, no es identidad de fila.</summary>
    public required string IdClaveMaestra { get; set; }

    /// <summary>Fingerprint SHA-256 del certificado — permite trazar sin descifrar, y forma
    /// parte del AAD (ata el ciphertext también al PEM público al que pertenece).</summary>
    public required string HuellaSha256 { get; set; }

    /// <summary>Del propio X.509 — nunca <c>DEFAULT now()</c>, <c>IRelojDelSistema</c> es la
    /// única fuente de tiempo de este programa.</summary>
    public DateTimeOffset VigenciaDesde { get; set; }

    public DateTimeOffset VigenciaHasta { get; set; }

    /// <summary>A lo sumo un certificado activo por empresa+ambiente
    /// (<c>ux_certificados_fiscales_activo</c>, UNIQUE PARCIAL). La rotación desactiva la fila
    /// vieja y activa la nueva dentro de una sola transacción.</summary>
    public bool Activo { get; set; }
}

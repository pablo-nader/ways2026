namespace Ways.Domain.Fiscal;

/// <summary>
/// Ambiente WSAA/WSFE de un <see cref="CertificadoFiscal"/> (proposal.md decisión 5, gate §A).
/// Enum nativo de Postgres (<c>ambiente_fiscal</c>). Sin valor <c>CAEA</c> ni ninguno de
/// contingencia — llegan en 19c con su propio escritor (la regla de la etapa 17: un valor de
/// catálogo nace con escritor). 19a acepta ambos valores en el endpoint de registro de
/// certificado, pero <c>produccion</c> no tiene certificado real hasta 19b — lo que falta es el
/// certificado, no el escritor.
/// </summary>
public enum AmbienteFiscal
{
    Homologacion,
    Produccion
}

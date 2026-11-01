namespace Ways.Domain.MercadoPago;

public enum EstadoConexionMercadoPago
{
    Activa,
    Desconectada,

    /// <summary>Mercado Pago rechazó la renovación del token (o devolvió otra cuenta): la conexión dejó de servir y hay que
    /// volver a autorizar. Como la desconectada, no conserva material de cifrado.</summary>
    RequiereReconexion
}

using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;
using Ways.Application.Catalogos;

namespace Ways.Api.OpenApi;

/// <summary>
/// <see cref="CampoOpcional{T}"/> viaja en el JSON como el valor mismo (o null), pero el generador de esquemas ve el
/// struct y lo describiría como un objeto con <c>presente</c> y <c>valor</c>. Acá se documenta como lo que el cliente
/// manda: un entero o null.
/// </summary>
public static class EsquemaDeCampoOpcional
{
    public static OpenApiOptions DocumentarCamposOpcionales(this OpenApiOptions opciones) =>
        opciones.AddSchemaTransformer((esquema, contexto, _) =>
        {
            var tipo = contexto.JsonTypeInfo.Type;

            if (tipo.IsGenericType
                && tipo.GetGenericTypeDefinition() == typeof(CampoOpcional<>)
                && Nullable.GetUnderlyingType(tipo.GetGenericArguments()[0]) == typeof(int))
            {
                esquema.Type = JsonSchemaType.Integer | JsonSchemaType.Null;
                esquema.Format = "int32";
                esquema.Properties?.Clear();
                esquema.AdditionalPropertiesAllowed = true;
            }

            return Task.CompletedTask;
        });
}

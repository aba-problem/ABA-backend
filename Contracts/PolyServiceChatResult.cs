namespace abaproblem.Contracts;

/// <summary>
/// Resultado de una llamada real a PolyService IA (v1/chat/completions).
/// TokensPrompt/TokensCompletion vienen del campo "usage" de la respuesta del
/// proveedor — se usan para auditar el consumo real (sp_RegistrarUsoApiKey),
/// nunca una estimación calculada en el backend.
/// </summary>
public sealed record PolyServiceChatResult(string Contenido, int TokensPrompt, int TokensCompletion);

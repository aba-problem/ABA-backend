namespace abaproblem.Services;

/// <summary>Nombres de esquemas de autenticación usados en el flujo OAuth → cookie JWT.</summary>
public static class AuthSchemes
{
    /// <summary>Esquema por defecto de la API: valida el JWT (leído desde cookie HttpOnly).</summary>
    public const string Jwt = "Jwt";

    /// <summary>Cookie temporal de correlación/sign-in externo usada por los handlers OAuth.</summary>
    public const string External = "External";

    public const string Google = "Google";
    public const string GitHub = "GitHub";
}

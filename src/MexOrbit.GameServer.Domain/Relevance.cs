// A que distancia el cliente empieza —y deja— de saber que algo existe.
//
// El server legado tenia UN umbral (`RenderRange = 2000`) y lo evaluaba cada
// tick. Eso hace que un jugador parado justo en el borde genere un spawn y un
// despawn cada 84 ms: doce naves apareciendo y desapareciendo por segundo, por
// cada entidad del borde. Aqui el umbral de SALIR es mayor que el de ENTRAR, asi
// que para volver a desaparecer hay que alejarse de verdad.
//
// Los valores viven en `server_setting` (spec del protocolo §relevancia por
// rango: "valores iniciales, calibrables en BD"). Los de aqui son solo el
// respaldo: un dial ausente jamas debe tumbar el arranque.
namespace MexOrbit.GameServer.Domain;

public sealed record RelevanceRanges(double Entities, double Objects, byte HysteresisPct)
{
    /// <summary>Los valores iniciales que fija la spec, por si las filas de
    /// `server_setting` no estan.</summary>
    public static readonly RelevanceRanges Fallback = new(2_000, 1_250, 10);

    private double WithMargin(double range) => range * (1 + HysteresisPct / 100.0);

    /// <summary>El umbral que toca segun si el cliente YA sabia de ello. Se entra
    /// al rango justo y se sale al rango con margen: esa banda es la histeresis.</summary>
    public double EntityThreshold(bool alreadySeen) => alreadySeen ? WithMargin(Entities) : Entities;

    public double ObjectThreshold(bool alreadySeen) => alreadySeen ? WithMargin(Objects) : Objects;
}

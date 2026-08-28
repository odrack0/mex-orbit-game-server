// Los diales del juego que NO viven en BD: cadencia, alcance y plazos.
//
// Estaban sueltos como `private const` dentro del World, mezclados con el estado
// y el bucle. Aqui son lo que son —las constantes de diseño de la simulacion— y
// se pueden leer, citar en una prueba y documentar en el README sin abrir un
// archivo de mil lineas.
//
// Los numeros de JUEGO (recompensas, drops, aggro, daño de cada bicho) NO estan
// aqui: viven en BD con su auditoria. Esto es cadencia y geometria.
namespace MexOrbit.GameServer.Domain;

public static class Diales
{
    // ─── combate del jugador ────────────────────────────────────────────────
    /// <summary>Alcance del laser. Fuera de rango el laser ESPERA, no se apaga.</summary>
    public const double LaserRange = 600;
    /// <summary>Cadencia de golpe (con ION-1 de 60: 120 dps, TTK del Vex ~10 s).</summary>
    public const int AttackIntervalMs = 500;

    // ─── recoleccion ────────────────────────────────────────────────────────
    public const double CollectRange = 250;
    /// <summary>§7 guidelines: despawn de caja 2-3 min.</summary>
    public const int BoxTtlMs = 150_000;

    // ─── sesion ─────────────────────────────────────────────────────────────
    /// <summary>Ventana de reconexion tras caida de socket (auth-v1).</summary>
    public const int GraceMs = 60_000;
    /// <summary>Tope de un mensaje de chat (el mismo `max_len` del esquema).</summary>
    public const int ChatMaxLen = 256;
    /// <summary>Cadencia maxima de persistencia de player_ship_state.</summary>
    public const int WriteBehindMs = 30_000;

    // ─── IA de NPCs (portada del legado; ver NpcAi.cs) ──────────────────────
    /// <summary>El legado pensaba una vez por segundo. Se conserva.</summary>
    public const int AiThinkMs = 1_000;
    public const int NpcAttackIntervalMs = 1_000;
    /// <summary>Igual que el laser del jugador.</summary>
    public const double NpcAttackRange = 600;
    /// <summary>`ALIEN_DISTANCE_TO_USER` del legado: se plantan en el CIRCULO de
    /// este radio alrededor de la presa, no encima de ella.</summary>
    public const double AproximacionRadio = 300;
    /// <summary>Se rinde a este multiplo de su radio de aggro.</summary>
    public const double DesaggroFactor = 1.8;
    /// <summary>10% de escudo por segundo...</summary>
    public const int NpcShieldRegenMs = 1_000;
    /// <summary>...tras 10 s sin recibir fuego.</summary>
    public const int NpcOutOfCombatMs = 10_000;
    /// <summary>Cuanto corre un cobarde antes de recomponerse.</summary>
    public const int HuidaMs = 12_000;
    /// <summary>Hasta donde se larga.</summary>
    public const double HuidaDistancia = 2_500;

    // ─── salto ──────────────────────────────────────────────────────────────
    /// <summary>Hay que estar JUNTO al portal para saltar.</summary>
    public const double JumpRange = 600;

    /// <summary>El margen que los NPC dejan a los bordes al elegir destino.</summary>
    public const int MargenDelMapa = 500;
}

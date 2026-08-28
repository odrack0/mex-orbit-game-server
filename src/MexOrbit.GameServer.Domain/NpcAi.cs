// La IA de los NPCs: la maquina de 3 estados del server legado (NpcAI.cs),
// portada con sus vicios corregidos.
//
// Lo que se conserva porque ERA el comportamiento del juego:
//   · Piensa UNA vez por segundo, no cada tick.
//   · Sin objetivo y quieto -> vuela a un punto CUALQUIERA del mapa. Esto es lo
//     que hacia que el mapa se sintiera vivo: los bichos cruzan el sector, no
//     tiemblan en su sitio.
//   · Con objetivo -> se coloca en un punto aleatorio del CIRCULO de radio
//     AproximacionRadio alrededor del jugador (no encima de el), y espera. Si
//     el jugador se mueve, vuelve a aproximarse.
//   · Recibir un golpe convierte a cualquier NPC en agresor, sea o no
//     `is_aggressive`: los pasivos se defienden.
//
// Lo que NO se copia:
//   · El legado sorteaba el destino en 20000x12800 a mano, con el mapa de
//     20800x12800: los bichos nunca visitaban la franja derecha. Aqui los
//     limites salen del mapa.
//   · Usaba RenderRange (2000, fijo en codigo) como radio de aggro. Aqui es
//     `npc_catalog.aggro_radius`, un dial por especie en BD.
//   · Su bucle recorria TODOS los jugadores en rango sin cortar, asi que
//     mandaba el ultimo de la lista. Aqui gana el mas cercano.
//   · DateTime.Now por todos lados; aqui el tiempo es el tick inyectado.
namespace MexOrbit.GameServer.Domain;

public enum NpcAiState
{
    /// <summary>Sin presa: vagabundea por el mapa.</summary>
    Buscando,
    /// <summary>Tiene presa: se aproxima a su circulo.</summary>
    VolandoAlEnemigo,
    /// <summary>Ya esta al lado: aguanta hasta que la presa se mueva.</summary>
    EsperandoQueSeMueva,
    /// <summary>Malherido: corre lejos y deja de disparar. Solo lo usan los
    /// bichos con `flee_hp_pct` &gt; 0 — el Vorax es el primero.</summary>
    Huyendo,
}

public sealed class NpcAi
{
    public NpcAiState Estado = NpcAiState.Buscando;
    /// <summary>entity_id del jugador perseguido (0 = ninguno).</summary>
    public ulong TargetId;
    /// <summary>Si dispara. Un NPC pasivo solo lo enciende al ser golpeado.</summary>
    public bool Atacando;
    public long ProximoPensamientoTick;
    public long ProximoDisparoTick;
    /// <summary>Tick hasta el que sigue corriendo sin replantearse nada.</summary>
    public long HuyendoHastaTick;

    public void Olvidar()
    {
        TargetId = 0;
        Atacando = false;
        Estado = NpcAiState.Buscando;
    }

    /// <summary>El ReceiveAttack del legado: quien te pega se vuelve tu objetivo,
    /// seas agresivo o no. Un cobarde en plena huida no se da la vuelta a pelear.</summary>
    public void Devolver(ulong atacanteId)
    {
        if (Estado == NpcAiState.Huyendo) return;
        TargetId = atacanteId;
        Atacando = true;
        Estado = NpcAiState.VolandoAlEnemigo;
    }
}

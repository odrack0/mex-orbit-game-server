// Relevancia por rango: el cliente solo sabe de lo que tiene cerca.
//
// Antes el mundo hacia `Broadcast` de todo a todos — los 54 bichos del 1-1, sus
// movimientos y cada caja, a cada jugador esté donde esté. Ahora cada jugador
// tiene un conjunto de lo que SU cliente cree que existe, y cada tick se calcula
// el diff: lo que entra se anuncia, lo que sale se retira.
//
// Dos cosas se hacen distinto del legado a proposito:
//
//   · **Se difunde a quien ME VE, no a quien VEO YO.** El legado recorria el
//     conjunto del emisor (`SendCommandToInRangePlayers` sobre sus propios
//     `InRangeCharacters`). Con rangos simetricos coincide, pero en cuanto uno
//     tenia el rango doblado (skill Recon) mandaba sus movimientos a gente que
//     nunca habia recibido su `ShipCreate`: el cliente recibia un Move de una
//     nave que para el no existia.
//
//   · **Solo observan los JUGADORES.** El legado calculaba el conjunto para
//     todos los personajes, NPC contra NPC incluidos — 54x54 comparaciones para
//     alimentar a una IA que aqui ya usa `aggro_radius` de BD y no necesita el
//     conjunto para nada.
using MexOrbit.GameServer.Domain;

namespace MexOrbit.GameServer.Application;

public sealed partial class World
{
    /// <summary>El diff de un tick, para cada jugador con socket vivo.</summary>
    private void ActualizarRelevancia()
    {
        foreach (var observador in _players.Values)
        {
            // sin socket no hay a quien contarle nada; al volver, `SincronizarMundo`
            // resiembra el conjunto entero
            if (observador.Desconectado) continue;
            RevisarEntidades(observador);
            RevisarCajas(observador);
        }
    }

    private void RevisarEntidades(PlayerSlot observador)
    {
        // Lo que dejo de existir por su cuenta (murio, salto, cerro sesion) ya
        // recibio su propio aviso —EntityDestroyed o EntityDespawn— asi que aqui
        // solo se limpia el rastro, sin mandar nada. Importa porque los NPC
        // REUTILIZAN su id al reaparecer: un id fantasma en este conjunto haria
        // que el bicho volviera al mapa sin que su cliente se enterase.
        observador.Vistas.RemoveWhere(id => !SigueEnElMundo(id));

        foreach (var npc in _npcs.Values) Revisar(observador, npc);
        foreach (var otro in _players.Values)
        {
            // un destruido no se ve: ya se anuncio con EntityDestroyed y vuelve
            // a existir cuando elija reaparicion
            if (otro == observador || otro.Muerto) continue;
            Revisar(observador, otro.Entity);
        }
    }

    private void Revisar(PlayerSlot observador, Entity objetivo)
    {
        var yaVisto = observador.Vistas.Contains(objetivo.Id);
        // El objetivo seleccionado NUNCA sale de relevancia (spec del protocolo).
        // Si no, perseguir a un bicho que huye seria verlo evaporarse justo
        // cuando importa, y el server seguiria diciendo que lo tienes fichado.
        var visible = objetivo.Id == observador.TargetId
            || Geometria.Distancia(observador.Entity, objetivo) <= rangos.UmbralEntidad(yaVisto);

        if (visible == yaVisto) return;

        if (visible)
        {
            observador.Vistas.Add(objetivo.Id);
            Enviar(observador, new EntitySpawned(objetivo));
            // ...y su rumbo si venia volando. `EntitySpawn` no lleva destino, asi
            // que sin esto una nave que entra en rango en pleno vuelo aparece
            // congelada hasta su siguiente movimiento — que puede tardar
            // segundos, o no llegar nunca si ya iba camino de su destino.
            if (objetivo.Moving) Enviar(observador, new EntityMoved(objetivo));
        }
        else
        {
            observador.Vistas.Remove(objetivo.Id);
            Enviar(observador, new EntityDespawned(objetivo.Id, DespawnReason.Range));
        }
    }

    private void RevisarCajas(PlayerSlot observador)
    {
        observador.CajasVistas.RemoveWhere(id => !_boxes.ContainsKey(id));

        foreach (var caja in _boxes.Values)
        {
            var yaVista = observador.CajasVistas.Contains(caja.Id);
            var visible = Geometria.Distancia(caja.X, caja.Y, observador.Entity.X, observador.Entity.Y)
                <= rangos.UmbralObjeto(yaVista);
            if (visible == yaVista) continue;

            if (visible)
            {
                observador.CajasVistas.Add(caja.Id);
                Enviar(observador, new BoxSpawned(caja.Id, "from_ship", caja.X, caja.Y));
            }
            else
            {
                observador.CajasVistas.Remove(caja.Id);
                Enviar(observador, new BoxDespawned(caja.Id, BoxDespawnReason.Range));
            }
        }
    }

    /// <summary>Siembra el conjunto de un jugador que acaba de entrar o volver:
    /// recibe spawns de lo que este en rango AHORA, no del mapa entero.</summary>
    private void SembrarRelevancia(PlayerSlot observador)
    {
        observador.Vistas.Clear();
        observador.CajasVistas.Clear();
        RevisarEntidades(observador);
        RevisarCajas(observador);
    }

    private bool SigueEnElMundo(ulong entityId) =>
        _npcs.ContainsKey(entityId)
        || _players.Values.Any(s => s.Entity.Id == entityId && !s.Muerto);

    /// <summary>Saca una entidad del conjunto de TODOS. Se llama cuando deja de
    /// existir de verdad: su aviso (EntityDestroyed / EntityDespawn) ya salio, y
    /// el cliente ya borro el nodo.</summary>
    private void OlvidarEntidad(ulong entityId)
    {
        foreach (var slot in _players.Values) slot.Vistas.Remove(entityId);
    }

    private void OlvidarCaja(ulong boxId)
    {
        foreach (var slot in _players.Values) slot.CajasVistas.Remove(boxId);
    }

    // ─── difusion por relevancia ────────────────────────────────────────────

    /// <summary>A quienes VEN esa entidad, y a ella misma si es un jugador.
    ///
    /// El frame se codifica de forma perezosa: si no la ve nadie, el evento no
    /// llega siquiera a serializarse.</summary>
    private void AQuienesVen(ulong entityId, ServerEvent evento)
    {
        byte[]? frame = null;
        foreach (var slot in _players.Values)
        {
            if (slot.Entity.Id != entityId && !slot.Vistas.Contains(entityId)) continue;
            frame ??= codec.Encode(evento);
            slot.Port.Send(frame);
        }
    }

    /// <summary>Un suceso entre dos —un disparo— lo recibe quien vea a cualquiera
    /// de los dos: si ves al que dispara pero no a su blanco, el laser tiene que
    /// salir igual.</summary>
    private void AQuienesVen(ulong unaId, ulong otraId, ServerEvent evento)
    {
        byte[]? frame = null;
        foreach (var slot in _players.Values)
        {
            var laVe = slot.Entity.Id == unaId || slot.Vistas.Contains(unaId)
                    || slot.Entity.Id == otraId || slot.Vistas.Contains(otraId);
            if (!laVe) continue;
            frame ??= codec.Encode(evento);
            slot.Port.Send(frame);
        }
    }

    private void AQuienesVenCaja(ulong boxId, ServerEvent evento)
    {
        byte[]? frame = null;
        foreach (var slot in _players.Values)
        {
            if (!slot.CajasVistas.Contains(boxId)) continue;
            frame ??= codec.Encode(evento);
            slot.Port.Send(frame);
        }
    }
}

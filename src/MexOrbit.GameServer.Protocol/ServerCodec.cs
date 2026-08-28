// El traductor de salida: un evento del mundo, un frame del protocolo.
//
// Este `switch` es la unica pieza del server que sabe a la vez lo que pasa en el
// juego y como se dice por el cable. Todo lo demas ignora una de las dos cosas, y
// eso es exactamente lo que se buscaba: un mensaje nuevo en el esquema se
// resuelve aqui, y una regla nueva del juego no se resuelve aqui.
using MexOrbit.GameServer.Application;
using W = MexOrbit.Protocol;

namespace MexOrbit.GameServer.Protocol;

public sealed class ServerCodec : IServerCodec
{
    public byte[] Encode(ServerEvent evento) => evento switch
    {
        EntitySpawned e => new W.EntitySpawn
        {
            EntityId = e.Entidad.Id,
            Kind = Traduccion.AlCable(e.Entidad.Kind),
            TypeId = e.Entidad.TypeId,
            Name = e.Entidad.Name,
            Faction = e.Entidad.Faction,
            X = Traduccion.Redondear(e.Entidad.X),
            Y = Traduccion.Redondear(e.Entidad.Y),
            HpPct = e.Entidad.HpPct,
            Speed = e.Entidad.Speed,
            // casco y escudo viajan por separado: son dos barras, no una suma
            ShieldPct = e.Entidad.ShieldPct,
        }.Encode(),

        EntityMoved e => new W.EntityMove
        {
            EntityId = e.Entidad.Id,
            X = Traduccion.Redondear(e.Entidad.X),
            Y = Traduccion.Redondear(e.Entidad.Y),
            TargetX = Traduccion.Redondear(e.Entidad.TargetX),
            TargetY = Traduccion.Redondear(e.Entidad.TargetY),
            Speed = e.Entidad.Speed,
            Teleport = e.Teleport,
        }.Encode(),

        EntityDespawned e => new W.EntityDespawn
        {
            EntityId = e.EntityId,
            Reason = Traduccion.AlCable(e.Reason),
        }.Encode(),

        EntityDestroyed e => new W.EntityDestroyed
        {
            EntityId = e.EntityId,
            KillerId = e.KillerId,
        }.Encode(),

        AttackLanded e => new W.AttackEvent
        {
            AttackerId = e.AttackerId,
            TargetId = e.TargetId,
            Weapon = Traduccion.AlCable(e.Weapon),
            Damage = e.Damage,
            TargetHp = e.TargetHp,
            TargetShield = e.TargetShield,
            Missed = e.Missed,
            AmmoId = e.AmmoId,
            Skilled = e.Skilled,
        }.Encode(),

        BoxSpawned e => new W.BoxSpawn
        {
            BoxId = e.BoxId,
            BoxType = e.BoxType,
            X = Traduccion.Redondear(e.X),
            Y = Traduccion.Redondear(e.Y),
        }.Encode(),

        BoxDespawned e => new W.BoxDespawn
        {
            BoxId = e.BoxId,
            Reason = Traduccion.AlCable(e.Reason),
        }.Encode(),

        MapEntered e => EnterMap(e),

        PricesPublished e => Precios(e),

        HeroStatsUpdated e => new W.HeroStats
        {
            Hp = e.Hp, MaxHp = e.MaxHp, Shield = e.Shield, MaxShield = e.MaxShield,
            Cargo = e.Cargo, MaxCargo = e.MaxCargo,
            Credits = e.Credits, Experience = e.Experience, Level = e.Level,
        }.Encode(),

        TargetAcquired e => new W.TargetInfo
        {
            EntityId = e.EntityId, Hp = e.Hp, MaxHp = e.MaxHp,
            Shield = e.Shield, MaxShield = e.MaxShield,
        }.Encode(),

        StationRangeChanged e => new W.StationRange
        {
            InRange = e.InRange, StationId = e.StationId,
        }.Encode(),

        RespawnOffered e => Respawn(e),

        Collected e => Recogida(e),

        Unloaded e => Descarga(e),

        Sold e => new W.SellResult
        {
            RequestId = e.RequestId,
            CreditsGained = e.CreditsGained,
            NewCredits = e.NewCredits,
        }.Encode(),

        StorageSynced e => Almacen(e),

        Welcomed e => new W.Welcome
        {
            AccountId = (ulong)e.AccountId,
            ReconnectToken = e.ReconnectToken,
            ServerTimeMs = e.ServerTimeMs,
            TickRate = e.TickRate,
        }.Encode(),

        ResumeAccepted => new W.ResumeOk().Encode(),

        SessionTakenOver => new W.SessionReplaced().Encode(),

        Pinged e => new W.Ping { Nonce = e.Nonce }.Encode(),

        Failed e => new W.ErrorReply
        {
            RequestId = e.RequestId,
            Code = Traduccion.AlCable(e.Code),
            Detail = e.Detail,
        }.Encode(),

        ChatBroadcast e => new W.ChatMessage
        {
            Channel = Traduccion.AlCable(e.Channel),
            FromName = e.FromName,
            FromClan = e.FromClan,
            Text = e.Text,
            ServerTimeMs = e.ServerTimeMs,
        }.Encode(),

        JumpHandedOff e => new W.JumpHandoff
        {
            MapCode = e.MapCode,
            Host = e.Servidor.Host,
            Port = e.Servidor.Port,
            IsTls = e.Servidor.IsTls,
        }.Encode(),

        // un evento sin traduccion es un error de programacion, no un caso raro
        _ => throw new ArgumentOutOfRangeException(nameof(evento), evento,
            $"el codec no sabe poner en el cable un {evento.GetType().Name}"),
    };

    private static byte[] EnterMap(MapEntered e)
    {
        var mapa = e.Mapa;
        var msg = new W.EnterMap
        {
            MapId = (ulong)mapa.Id, MapCode = mapa.Code,
            LimitsX = mapa.BoundsX, LimitsY = mapa.BoundsY, CargoRiskPct = e.CargoRiskPct,
            StationX = mapa.StationX, StationY = mapa.StationY, StationRange = mapa.SecureRange,
        };
        // los portales van completos aqui: son mobiliario del mapa, no entidades
        foreach (var p in e.Portales)
            msg.Portals.Add(new W.MapPortal
            {
                PortalId = (ulong)p.Id, X = p.X, Y = p.Y,
                TargetMapCode = p.TargetMapCode, IsWorking = p.IsWorking,
            });
        return msg.Encode();
    }

    private static byte[] Precios(PricesPublished e)
    {
        var msg = new W.NpcPrices();
        foreach (var p in e.Precios)
            msg.Prices.Add(new W.MaterialPrice
            {
                MaterialId = p.LootId, PriceCredits = (ulong)p.PriceCredits,
            });
        return msg.Encode();
    }

    private static byte[] Respawn(RespawnOffered e)
    {
        var msg = new W.RespawnOptions
        {
            Cause = Traduccion.AlCable(e.Cause),
            KillerName = e.KillerName,
        };
        foreach (var o in e.Opciones)
            msg.Options.Add(new W.RespawnOption
            {
                OptionId = o.OptionId, LabelKey = o.LabelKey,
                CostCredits = o.CostCredits, Available = o.Available,
            });
        return msg.Encode();
    }

    private static byte[] Recogida(Collected e)
    {
        var msg = new W.CollectResult { RequestId = e.RequestId };
        foreach (var d in e.Drops) msg.Drops.Add(Material(d));
        return msg.Encode();
    }

    private static byte[] Descarga(Unloaded e)
    {
        var msg = new W.UnloadResult { RequestId = e.RequestId };
        foreach (var d in e.Stored) msg.Stored.Add(Material(d));
        foreach (var d in e.Refined) msg.Refined.Add(Material(d));
        return msg.Encode();
    }

    private static byte[] Almacen(StorageSynced e)
    {
        var msg = new W.StorageState();
        foreach (var m in e.Materiales) msg.Materials.Add(Material(m));
        return msg.Encode();
    }

    private static W.MaterialAmount Material(MexOrbit.GameServer.Domain.MaterialAmount m) =>
        new() { MaterialId = m.MaterialId, Amount = m.Amount };
}

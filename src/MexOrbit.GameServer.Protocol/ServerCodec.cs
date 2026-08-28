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
    public byte[] Encode(ServerEvent serverEvent) => serverEvent switch
    {
        EntitySpawned e => new W.EntitySpawn
        {
            EntityId = e.Entity.Id,
            Kind = WireMapping.ToWire(e.Entity.Kind),
            TypeId = e.Entity.TypeId,
            Name = e.Entity.Name,
            Faction = e.Entity.Faction,
            X = WireMapping.Round(e.Entity.X),
            Y = WireMapping.Round(e.Entity.Y),
            HpPct = e.Entity.HpPct,
            Speed = e.Entity.Speed,
            // casco y escudo viajan por separado: son dos barras, no una suma
            ShieldPct = e.Entity.ShieldPct,
        }.Encode(),

        EntityMoved e => new W.EntityMove
        {
            EntityId = e.Entity.Id,
            X = WireMapping.Round(e.Entity.X),
            Y = WireMapping.Round(e.Entity.Y),
            TargetX = WireMapping.Round(e.Entity.TargetX),
            TargetY = WireMapping.Round(e.Entity.TargetY),
            Speed = e.Entity.Speed,
            Teleport = e.Teleport,
        }.Encode(),

        EntityDespawned e => new W.EntityDespawn
        {
            EntityId = e.EntityId,
            Reason = WireMapping.ToWire(e.Reason),
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
            Weapon = WireMapping.ToWire(e.Weapon),
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
            X = WireMapping.Round(e.X),
            Y = WireMapping.Round(e.Y),
        }.Encode(),

        BoxDespawned e => new W.BoxDespawn
        {
            BoxId = e.BoxId,
            Reason = WireMapping.ToWire(e.Reason),
        }.Encode(),

        MapEntered e => EnterMap(e),

        PricesPublished e => Prices(e),

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

        Collected e => EncodeCollected(e),

        Unloaded e => EncodeUnloaded(e),

        Sold e => new W.SellResult
        {
            RequestId = e.RequestId,
            CreditsGained = e.CreditsGained,
            NewCredits = e.NewCredits,
        }.Encode(),

        StorageSynced e => EncodeStorage(e),

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
            Code = WireMapping.ToWire(e.Code),
            Detail = e.Detail,
        }.Encode(),

        ChatBroadcast e => new W.ChatMessage
        {
            Channel = WireMapping.ToWire(e.Channel),
            FromName = e.FromName,
            FromClan = e.FromClan,
            Text = e.Text,
            ServerTimeMs = e.ServerTimeMs,
        }.Encode(),

        JumpHandedOff e => new W.JumpHandoff
        {
            MapCode = e.MapCode,
            Host = e.Server.Host,
            Port = e.Server.Port,
            IsTls = e.Server.IsTls,
        }.Encode(),

        // un evento sin traduccion es un error de programacion, no un caso raro
        _ => throw new ArgumentOutOfRangeException(nameof(serverEvent), serverEvent,
            $"el codec no sabe poner en el cable un {serverEvent.GetType().Name}"),
    };

    private static byte[] EnterMap(MapEntered e)
    {
        var map = e.Map;
        var msg = new W.EnterMap
        {
            MapId = (ulong)map.Id, MapCode = map.Code,
            LimitsX = map.BoundsX, LimitsY = map.BoundsY, CargoRiskPct = e.CargoRiskPct,
            StationX = map.StationX, StationY = map.StationY, StationRange = map.SecureRange,
        };
        // los portales van completos aqui: son mobiliario del mapa, no entidades
        foreach (var p in e.Portals)
            msg.Portals.Add(new W.MapPortal
            {
                PortalId = (ulong)p.Id, X = p.X, Y = p.Y,
                TargetMapCode = p.TargetMapCode, IsWorking = p.IsWorking,
            });
        return msg.Encode();
    }

    private static byte[] Prices(PricesPublished e)
    {
        var msg = new W.NpcPrices();
        foreach (var p in e.Prices)
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
            Cause = WireMapping.ToWire(e.Cause),
            KillerName = e.KillerName,
        };
        foreach (var o in e.Options)
            msg.Options.Add(new W.RespawnOption
            {
                OptionId = o.OptionId, LabelKey = o.LabelKey,
                CostCredits = o.CostCredits, Available = o.Available,
            });
        return msg.Encode();
    }

    private static byte[] EncodeCollected(Collected e)
    {
        var msg = new W.CollectResult { RequestId = e.RequestId };
        foreach (var d in e.Drops) msg.Drops.Add(Material(d));
        return msg.Encode();
    }

    private static byte[] EncodeUnloaded(Unloaded e)
    {
        var msg = new W.UnloadResult { RequestId = e.RequestId };
        foreach (var d in e.Stored) msg.Stored.Add(Material(d));
        foreach (var d in e.Refined) msg.Refined.Add(Material(d));
        return msg.Encode();
    }

    private static byte[] EncodeStorage(StorageSynced e)
    {
        var msg = new W.StorageState();
        foreach (var m in e.Materials) msg.Materials.Add(Material(m));
        return msg.Encode();
    }

    private static W.MaterialAmount Material(MexOrbit.GameServer.Domain.MaterialAmount m) =>
        new() { MaterialId = m.MaterialId, Amount = m.Amount };
}

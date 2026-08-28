// Un vuelo largo por el sector de verdad, mirando lo que veria el cliente.
//
// Las otras pruebas de relevancia comprueban reglas sueltas con montajes
// controlados. Esta hace lo contrario: pone el 1-1 tal como es —54 bichos de
// nueve especies, con sus velocidades y su IA— y vuela por el reconstruyendo,
// frame a frame, el mundo que el cliente tendria en pantalla. Lo que busca son
// las dos cosas que un jugador SI nota:
//
//   · que algo aparezca o desaparezca dentro del encuadre, y
//   · que pinchar un bicho que se esta viendo no inicie el ataque.
using MexOrbit.GameServer.Application;
using MexOrbit.GameServer.Domain;
using MexOrbit.Protocol;

namespace MexOrbit.GameServer.Tests;

public class LongFlightTests
{
    /// <summary>Lo que cabe en pantalla. La camara muestra 720 unidades logicas
    /// de alto al zoom de juego (0,621), o sea 1159 de mundo; el ancho depende
    /// del aspecto. La semidiagonal en 16:9 son ~1242 unidades: dentro de ese
    /// radio, aparecer o desaparecer se VE.</summary>
    private const double OnScreenRadius = 1_250;

    /// <summary>El bestiario real del 1-1 (migraciones .1 a .10).</summary>
    private static TestWorld RealSector()
    {
        var m = new TestWorld().WithMap(20_800, 12_800, 10_000, 6_000, 1_500);
        foreach (var (code, hp, vel, aggressive, huye, aggro, howMany) in new[]
        {
            ("vex",     800u, (ushort)270, false, (byte)0,  500u, (ushort)15),
            ("vexor",  1_400u, (ushort)260, false, (byte)0,  500u, (ushort)8),
            ("skarn",  2_000u, (ushort)250, false, (byte)0,  600u, (ushort)5),
            ("ferox",  2_600u, (ushort)300, true,  (byte)0,  700u, (ushort)4),
            ("skarnox",4_000u, (ushort)240, false, (byte)0,  600u, (ushort)2),
            ("gravit", 1_100u, (ushort)280, false, (byte)0,  500u, (ushort)9),
            ("mordax", 1_800u, (ushort)255, false, (byte)0,  600u, (ushort)5),
            ("gravon", 1_500u, (ushort)265, false, (byte)0,  500u, (ushort)3),
            ("vorax",  2_200u, (ushort)270, false, (byte)30, 600u, (ushort)3),
        })
        {
            m.WithNpc(new NpcSpawnInfo(1, code, code, hp, 0, vel, 50, aggressive, huye, aggro,
                30, howMany, 0, 0, 100, 30, 60));
        }
        return m.Build();
    }

    /// <summary>El mundo que el cliente tendria montado: se arma aplicando los
    /// frames en el mismo orden en que llegan, igual que `world.gd`.</summary>
    private sealed class ClientMirror(FakePort port)
    {
        private int _consumidos;
        private bool _sembrando = true;
        public readonly HashSet<ulong> Entities = [];
        public readonly List<string> Oddities = [];

        /// <summary>La sincronizacion del Join manda todo lo que hay en rango, y
        /// eso incluye cosas a 300 unidades. No es aparecer de la nada: es
        /// acabar de entrar al mapa. Se traga sin anotar y a partir de ahi si.</summary>
        public void Seed()
        {
            Consume(_ => double.MaxValue);
            _sembrando = false;
        }

        public void Consume(Func<ulong, double> distanciaA)
        {
            var frames = port.Frames;
            for (; _consumidos < frames.Count; _consumidos++)
            {
                var f = frames[_consumidos];
                switch (Wire.MsgIdOf(f))
                {
                    case EntitySpawn.MsgId:
                        var sp = EntitySpawn.Decode(f);
                        Note(sp.EntityId, distanciaA(sp.EntityId), "aparecio");
                        Entities.Add(sp.EntityId);
                        break;
                    case EntityDespawn.MsgId:
                        var dp = EntityDespawn.Decode(f);
                        Note(dp.EntityId, distanciaA(dp.EntityId), "desaparecio");
                        Entities.Remove(dp.EntityId);
                        break;
                    case EntityDestroyed.MsgId:
                        // reventar algo SI se ve, y debe verse: eso no es una rareza
                        Entities.Remove(EntityDestroyed.Decode(f).EntityId);
                        break;
                }
            }
        }

        private void Note(ulong id, double dist, string what)
        {
            if (!_sembrando && dist <= OnScreenRadius)
                Oddities.Add($"{id} {what} a {dist:F0} u — dentro del encuadre");
        }
    }

    [Fact]
    public void Flying_the_sector_nothing_appears_or_vanishes_on_screen()
    {
        var m = RealSector();
        var p = m.Enter(1, data: m.Pilot(1, x: 10_000, y: 6_000));
        var mirror = new ClientMirror(p);
        mirror.Seed();

        Fly(m, p, mirror, (id, _) => { });

        Assert.Empty(mirror.Oddities);
    }

    [Fact]
    public void Clicking_a_beast_you_can_see_always_starts_the_attack()
    {
        // Es el sintoma que se nota jugando: el server rechaza en silencio un
        // objetivo que no tiene por visto, el cliente cree que si lo tiene, y la
        // tecla de disparo no hace nada.
        var m = RealSector();
        var p = m.Enter(1, data: m.Pilot(1, x: 10_000, y: 6_000));
        var mirror = new ClientMirror(p);
        mirror.Seed();
        var rejections = new List<string>();

        Fly(m, p, mirror, (id, dist) =>
        {
            var before = p.All<TargetInfo>().Count;
            m.W.Post(new SelectTargetCmd(p, id));
            m.Tick();
            if (p.All<TargetInfo>().Count == before)
                rejections.Add($"{id} a {dist:F0} u: el cliente lo ve y el server lo rechaza");
        });

        Assert.Empty(rejections);
    }

    /// <summary>Cruza el sector de esquina a esquina pasando por el centro, un
    /// tick cada vez, y en cada uno deja mirar lo que acaba de recibir.</summary>
    private static void Fly(TestWorld m, FakePort p, ClientMirror mirror,
        Action<ulong, double> onEachVisible)
    {
        Vector[] route =
        [
            new(4_000, 3_000), new(16_000, 3_000), new(16_000, 9_500),
            new(4_000, 9_500), new(10_400, 6_400),
        ];
        var seq = 0ul;

        foreach (var destination in route)
        {
            m.W.Post(new MoveIntentCmd(p, ++seq, (uint)destination.X, (uint)destination.Y));
            for (var i = 0; i < 900; i++)
            {
                m.Tick();
                var ship = m.W.ShipOf(1)!;
                mirror.Consume(id => DistanceTo(m, ship, id));
                if (!ship.Moving) break;

                // Se pincha lo que se estaria VIENDO, no lo que el cliente tenga en
                // memoria: un bicho que quedo lejos —el objetivo seleccionado nunca
                // sale de relevancia— sigue en la lista pero nadie va a hacerle
                // click, porque no esta en pantalla. Y el heroe tampoco: nadie se
                // ficha a si mismo.
                if (i % 25 != 0) continue;
                var onScreen = mirror.Entities
                    .Where(e => e != ship.Id && DistanceTo(m, ship, e) <= OnScreenRadius)
                    .Take(3).ToList();
                foreach (var chosen in onScreen)
                {
                    var actual = m.W.ShipOf(1)!;
                    mirror.Consume(id => DistanceTo(m, actual, id));
                    if (!mirror.Entities.Contains(chosen)) continue;
                    onEachVisible(chosen, DistanceTo(m, actual, chosen));
                }
            }
        }
    }

    /// <summary>Distancia del heroe a una entidad; `MaxValue` si ya no existe
    /// (murio o se fue), que es justo cuando no hay nada que reprochar.</summary>
    private static double DistanceTo(TestWorld m, Entity ship, ulong id) =>
        m.W.LiveNpcs.TryGetValue(id, out var e)
            ? Geometry.Distance(ship, e)
            : double.MaxValue;

    private readonly record struct Vector(int X, int Y);
}

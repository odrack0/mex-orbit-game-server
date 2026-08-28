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

public class VueloLargoTests
{
    /// <summary>Lo que cabe en pantalla. La camara muestra 720 unidades logicas
    /// de alto al zoom de juego (0,621), o sea 1159 de mundo; el ancho depende
    /// del aspecto. La semidiagonal en 16:9 son ~1242 unidades: dentro de ese
    /// radio, aparecer o desaparecer se VE.</summary>
    private const double RadioEnPantalla = 1_250;

    /// <summary>El bestiario real del 1-1 (migraciones .1 a .10).</summary>
    private static Mundo SectorReal()
    {
        var m = new Mundo().ConMapa(20_800, 12_800, 10_000, 6_000, 1_500);
        foreach (var (code, hp, vel, agresivo, huye, aggro, cuantos) in new[]
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
            m.ConNpc(new NpcSpawnInfo(1, code, code, hp, 0, vel, 50, agresivo, huye, aggro,
                30, cuantos, 0, 0, 100, 30, 60));
        }
        return m.Construir();
    }

    /// <summary>El mundo que el cliente tendria montado: se arma aplicando los
    /// frames en el mismo orden en que llegan, igual que `world.gd`.</summary>
    private sealed class ClienteEspejo(PuertoFalso puerto)
    {
        private int _consumidos;
        private bool _sembrando = true;
        public readonly HashSet<ulong> Entidades = [];
        public readonly List<string> Rarezas = [];

        /// <summary>La sincronizacion del Join manda todo lo que hay en rango, y
        /// eso incluye cosas a 300 unidades. No es aparecer de la nada: es
        /// acabar de entrar al mapa. Se traga sin anotar y a partir de ahi si.</summary>
        public void Sembrar()
        {
            Consumir(_ => double.MaxValue);
            _sembrando = false;
        }

        public void Consumir(Func<ulong, double> distanciaA)
        {
            var frames = puerto.Frames;
            for (; _consumidos < frames.Count; _consumidos++)
            {
                var f = frames[_consumidos];
                switch (Protocolo.MsgIdDe(f))
                {
                    case EntitySpawn.MsgId:
                        var sp = EntitySpawn.Decode(f);
                        Anotar(sp.EntityId, distanciaA(sp.EntityId), "aparecio");
                        Entidades.Add(sp.EntityId);
                        break;
                    case EntityDespawn.MsgId:
                        var dp = EntityDespawn.Decode(f);
                        Anotar(dp.EntityId, distanciaA(dp.EntityId), "desaparecio");
                        Entidades.Remove(dp.EntityId);
                        break;
                    case EntityDestroyed.MsgId:
                        // reventar algo SI se ve, y debe verse: eso no es una rareza
                        Entidades.Remove(EntityDestroyed.Decode(f).EntityId);
                        break;
                }
            }
        }

        private void Anotar(ulong id, double dist, string que)
        {
            if (!_sembrando && dist <= RadioEnPantalla)
                Rarezas.Add($"{id} {que} a {dist:F0} u — dentro del encuadre");
        }
    }

    [Fact]
    public void Volando_por_el_sector_nada_aparece_ni_desaparece_dentro_del_encuadre()
    {
        var m = SectorReal();
        var p = m.Entrar(1, datos: m.Piloto(1, x: 10_000, y: 6_000));
        var espejo = new ClienteEspejo(p);
        espejo.Sembrar();

        Volar(m, p, espejo, (id, _) => { });

        Assert.Empty(espejo.Rarezas);
    }

    [Fact]
    public void Pinchar_un_bicho_que_se_esta_viendo_siempre_inicia_el_ataque()
    {
        // Es el sintoma que se nota jugando: el server rechaza en silencio un
        // objetivo que no tiene por visto, el cliente cree que si lo tiene, y la
        // tecla de disparo no hace nada.
        var m = SectorReal();
        var p = m.Entrar(1, datos: m.Piloto(1, x: 10_000, y: 6_000));
        var espejo = new ClienteEspejo(p);
        espejo.Sembrar();
        var rechazos = new List<string>();

        Volar(m, p, espejo, (id, dist) =>
        {
            var antes = p.Todos<TargetInfo>().Count;
            m.W.Post(new SelectTargetCmd(p, id));
            m.Tick();
            if (p.Todos<TargetInfo>().Count == antes)
                rechazos.Add($"{id} a {dist:F0} u: el cliente lo ve y el server lo rechaza");
        });

        Assert.Empty(rechazos);
    }

    /// <summary>Cruza el sector de esquina a esquina pasando por el centro, un
    /// tick cada vez, y en cada uno deja mirar lo que acaba de recibir.</summary>
    private static void Volar(Mundo m, PuertoFalso p, ClienteEspejo espejo,
        Action<ulong, double> conCadaVisible)
    {
        Vector[] ruta =
        [
            new(4_000, 3_000), new(16_000, 3_000), new(16_000, 9_500),
            new(4_000, 9_500), new(10_400, 6_400),
        ];
        var seq = 0ul;

        foreach (var destino in ruta)
        {
            m.W.Post(new MoveIntentCmd(p, ++seq, (uint)destino.X, (uint)destino.Y));
            for (var i = 0; i < 900; i++)
            {
                m.Tick();
                var nave = m.W.NaveDe(1)!;
                espejo.Consumir(id => DistanciaA(m, nave, id));
                if (!nave.Moving) break;

                // Se pincha lo que se estaria VIENDO, no lo que el cliente tenga en
                // memoria: un bicho que quedo lejos —el objetivo seleccionado nunca
                // sale de relevancia— sigue en la lista pero nadie va a hacerle
                // click, porque no esta en pantalla. Y el heroe tampoco: nadie se
                // ficha a si mismo.
                if (i % 25 != 0) continue;
                var enPantalla = espejo.Entidades
                    .Where(e => e != nave.Id && DistanciaA(m, nave, e) <= RadioEnPantalla)
                    .Take(3).ToList();
                foreach (var elegido in enPantalla)
                {
                    var actual = m.W.NaveDe(1)!;
                    espejo.Consumir(id => DistanciaA(m, actual, id));
                    if (!espejo.Entidades.Contains(elegido)) continue;
                    conCadaVisible(elegido, DistanciaA(m, actual, elegido));
                }
            }
        }
    }

    /// <summary>Distancia del heroe a una entidad; `MaxValue` si ya no existe
    /// (murio o se fue), que es justo cuando no hay nada que reprochar.</summary>
    private static double DistanciaA(Mundo m, Entity nave, ulong id) =>
        m.W.NpcsVivos.TryGetValue(id, out var e)
            ? Geometria.Distancia(nave, e)
            : double.MaxValue;

    private readonly record struct Vector(int X, int Y);
}

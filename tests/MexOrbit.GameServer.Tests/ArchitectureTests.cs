// La cebolla, comprobada por el build.
//
// Un diagrama en un README no impide nada. Estas pruebas si: el dia que alguien
// escriba `using MySqlConnector` dentro del dominio, o meta el protocolo binario
// en las reglas del juego, la suite se pone roja antes que la revision.
//
// Miran las referencias REALES del ensamblado compilado, no los .csproj: lo que
// importa no es lo que el proyecto declara, sino lo que el codigo acabo usando.
using System.Reflection;
using MexOrbit.GameServer.Application;
using MexOrbit.GameServer.Domain;
using Domain = MexOrbit.GameServer.Domain;

namespace MexOrbit.GameServer.Tests;

public class ArchitectureTests
{
    private static readonly Assembly DomainAsm = typeof(Entity).Assembly;
    private static readonly Assembly ApplicationAsm = typeof(World).Assembly;

    private static List<string> ReferencesOf(Assembly a) =>
        [.. a.GetReferencedAssemblies().Select(r => r.Name ?? "")];

    [Fact]
    public void The_domain_does_not_know_a_database_exists()
    {
        Assert.DoesNotContain(ReferencesOf(DomainAsm),
            r => r.Contains("MySql", StringComparison.OrdinalIgnoreCase)
                 || r.Contains("Dapper", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void The_domain_does_not_know_a_binary_protocol_exists()
    {
        // las reglas del juego no pueden depender de como viajan las cosas
        Assert.DoesNotContain(ReferencesOf(DomainAsm),
            r => r.Contains("Protocol", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void The_domain_knows_no_outer_layer()
    {
        Assert.DoesNotContain(ReferencesOf(DomainAsm),
            r => r.StartsWith("MexOrbit.GameServer.", StringComparison.Ordinal));
    }

    [Fact]
    public void The_application_only_knows_the_domain()
    {
        var own = ReferencesOf(ApplicationAsm)
            .Where(r => r.StartsWith("MexOrbit", StringComparison.Ordinal))
            .ToList();
        Assert.Equal(["MexOrbit.GameServer.Domain"], own);
    }

    [Fact]
    public void The_application_touches_neither_the_db_nor_the_wire()
    {
        // habla con ellos por PUERTOS: IEconomyRepository, IServerCodec, IClock
        Assert.DoesNotContain(ReferencesOf(ApplicationAsm),
            r => r.Contains("MySql", StringComparison.OrdinalIgnoreCase)
                 || r.Contains("Dapper", StringComparison.OrdinalIgnoreCase)
                 || r.Contains("BouncyCastle", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Everything_the_world_reports_can_be_put_on_the_wire()
    {
        // un evento nuevo sin traduccion es un fallo que solo aparecia el dia que
        // se disparaba en produccion; aqui aparece al añadirlo
        var codec = new global::MexOrbit.GameServer.Protocol.ServerCodec();
        var events = ApplicationAsm.GetTypes()
            .Where(t => t.IsSubclassOf(typeof(ServerEvent)) && !t.IsAbstract)
            .ToList();

        Assert.NotEmpty(events);
        var untranslated = events
            .Where(t => !CanBeEncoded(codec, t))
            .Select(t => t.Name)
            .ToList();
        Assert.Empty(untranslated);
    }

    /// <summary>Se arma el evento con valores por defecto y se le pide al codec
    /// que lo codifique. Solo interesa que NO responda "no se que es esto".</summary>
    private static bool CanBeEncoded(
        global::MexOrbit.GameServer.Protocol.ServerCodec codec, Type tipo)
    {
        try
        {
            codec.Encode((ServerEvent)SampleEvent(tipo));
            return true;
        }
        catch (ArgumentOutOfRangeException e) when (e.ParamName == "evento")
        {
            return false;   // el codec no lo conoce: es lo que se busca detectar
        }
        catch
        {
            // cualquier otro fallo (un campo nulo, un dato absurdo) significa que
            // el codec SI entro a traducirlo, que es lo unico que se comprueba
            return true;
        }
    }

    private static object SampleEvent(Type tipo)
    {
        var ctor = tipo.GetConstructors()[0];
        var args = ctor.GetParameters().Select(p => SampleOf(p.ParameterType)).ToArray();
        return ctor.Invoke(args);
    }

    private static object? SampleOf(Type t)
    {
        if (t == typeof(string)) return "";
        if (t == typeof(Entity))
            // `EntityKind` esta aliasado al del cable en GlobalUsings: aqui hace falta el del dominio
            return new Entity
            {
                Id = 1, Kind = Domain.EntityKind.Npc, TypeId = "x", Name = "x", Speed = 1,
            };
        if (t == typeof(MapInfo)) return new MapInfo(1, "1-1", "x", 1, 1, 0, 0, 0, "core");
        if (t == typeof(MapServer)) return new MapServer("h", 1, false);
        if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(IReadOnlyList<>))
            return Activator.CreateInstance(
                typeof(List<>).MakeGenericType(t.GetGenericArguments()[0]));
        return t.IsValueType ? Activator.CreateInstance(t) : null;
    }
}

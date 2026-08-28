// El tick (`World.Paso`) es `internal` a proposito: nadie fuera del server lo
// llama. Las pruebas SI necesitan avanzar el reloj a mano, que es justo lo que
// permite comprobar la simulacion sin esperar 80 ms de verdad por tick.
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("MexOrbit.GameServer.Tests")]

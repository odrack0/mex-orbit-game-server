# mex-orbit-game-server

El servidor de simulación en tiempo real del juego: el dueño del mundo, del combate y de la verdad.

> **MexOrbit** es nombre temporal del proyecto. Documentación en español; **código en inglés, comentarios en español**.

## Qué es

- La **simulación autoritativa**: mapas, naves, NPCs (taxonomía Vex→Imperator), combate, muerte (Black Box, durabilidad), pods, recolección, Eclipses e incursiones, eventos de defensa y Arena.
- **Loop de tick fijo** con tiempo inyectado (cero `DateTime.Now` disperso), concurrencia diseñada desde el día uno y estado testeable sin red.
- El **protocolo nuevo** del lado servidor: mensajes binarios tipados con framing por longitud, versionados (transporte por confirmar: WebSocket vs TCP).

## Qué NO es

- No maneja cuentas, login web ni pagos (eso es `mex-orbit-api`).
- No expone superficies de administración (eso es `mex-orbit-api-admin`).
- **No hereda código del emulador legado**: el server anterior (`MexOrbit.Server`) es referencia de comportamiento, no base. Sus 9 familias de bugs documentadas (TickManager, sesiones zombi, clamps) son la lista de lo que aquí no puede existir por diseño.

## Stack

- .NET (versión por confirmar al arrancar el pilar).
- Base de datos: esquema nuevo, migraciones versionadas como disciplina (motor por confirmar: MySQL vs PostgreSQL).

## Relación con otros repos

| Repo | Relación |
|---|---|
| `mex-orbit-client` | Su único consumidor en tiempo real, vía el protocolo nuevo |
| `mex-orbit-api` | Frontera por definir: quién es dueño del Mercado de órdenes, el almacén y la persistencia compartida |
| `mex-orbit-docs` | El diseño rector (Guidelines generales) y el documento del pilar 01-protocolo / 02-servidor |

## Referencia rectora

Los **Guidelines generales del juego** (`mex-orbit-docs`): toda mecánica implementada aquí se valida contra ellos.

## Estado

Repo recién creado. Primer paso: el documento de diseño del protocolo y de la arquitectura del server, antes de la primera línea de código.

## Diales

Constantes calibrables del codigo (los numeros de JUEGO viven en BD). **Regla del repo: todo dial nuevo se documenta aqui en el mismo commit que lo crea.**

| Dial | Donde | Valor | Que hace |
|---|---|---|---|
| `TickMs` | `appsettings.json` | 80 ms | Tick fijo de simulacion (12.5 Hz, herencia del prototipo) |
| `PingIntervalSeconds` y `PingMissesToDrop` | `appsettings.json` | 10 s, 3 fallos | Heartbeat: 3 pings sin Pong = socket muerto |
| `LaserRange` | `Game-World.cs` | 600 | Alcance del laser; fuera de rango el laser espera, no se apaga |
| `AttackIntervalMs` | `Game-World.cs` | 500 ms | Cadencia de golpe (con ION-1 de 60: 120 dps, TTK del Vex ~10 s) |
| `CollectRange` | `Game-World.cs` | 250 | Distancia maxima para recolectar una caja |
| `BoxTtlMs` | `Game-World.cs` | 150 s | Vida de la caja (2-3 min, guidelines seccion 7) |
| Write-behind | `Game-World.cs (Tick)` | 30 s | Cadencia maxima de persistencia de player_ship_state |
| Deambular de NPCs | `Game-World.cs (Tick)` | p=0.004 por tick, radio 800 | El wander perezoso |
| Rango de la estacion | BD `map_station.secure_range` | 1500 | Dentro de este radio se puede descargar y vender (dato, no constante) |
| `GraceMs` | `Game-World.cs` | 60 s | Ventana de reconexion tras caida de socket (auth-v1) |
| `ChatMaxLen` | `Game-World.cs` | 256 | Tope de un mensaje de chat (el mismo `max_len` del esquema) |

## El escudo del jugador

**v1 no tiene nano-casco**: las stats defensivas son dos, casco y escudo, y viajan
**por separado** (`EntitySpawn.hp_pct` + `shield_pct`, `HeroStats`, `TargetInfo`,
`AttackEvent`). El cliente nunca recibe la suma: cada barra se lee contra su maximo.

La **capacidad de escudo** no esta en el casco: la Phoenix trae `base_shield = 0` y
todo su escudo sale de los **generadores equipados** (`NAN-1` = 1000). `LoadShieldCapacity`
la calcula como `ship_catalog.base_shield + SUM(server_item_stat 'shield')` de los
slots `GENERATOR` de la config activa — el mismo patron que `LoadLaserDamage`.

**En E2 se entra con el escudo lleno.** La regeneracion en vuelo todavia no existe;
arrastrar un `current_shield` guardado en 0 dejaria al jugador sin escudo para
siempre. `current_shield` se persiste igual (write-behind y al salir), para que el
dato ya este cuando la regeneracion llegue y solo haya que dejar de sobrescribirlo.

## Reconexion y chat (I7)

**Reconexion con ventana de gracia.** Una caida de socket **no** saca la nave del mundo:

- `ClientConnection` postea `LeaveCmd(this, "DROPPED")` en su `finally`. Solo un
  `LogoutRequest` explicito manda `"LOGOUT"`, y ese si hace `Drop`.
- `OnLeave` con motivo distinto de `LOGOUT` marca `slot.GraceUntilTick = _tick + GraceMs/tickMs`
  y apaga el laser. La nave sigue en el mundo, visible para los demas.
- El heartbeat tambien abre gracia en vez de dropear: 3 pings sin Pong cierran el
  socket y arrancan la cuenta atras.
- El tick barre los slots con la gracia agotada y los dropea con motivo `TIMEOUT`.
- Mientras dura la gracia, la sesion **sigue abierta en BD**: por eso el token de
  reconexion todavia resuelve.

**El regreso.** El primer frame de una conexion puede ser `Hello` **o** `Resume`:

- `Resume` trae el `reconnect_token` que se entrego en el `Welcome`.
  `Repo.FindSessionByToken` busca la sesion viva por hash del token (nunca se
  guarda el token en claro). Si no existe: `ErrorReply { RESUME_EXPIRED }`.
- `OnResume` **intercambia el puerto** del slot (`slot.Port = cmd.Port`), cierra la
  gracia, responde `ResumeOk` y llama a `SincronizarMundo(slot)` — el mismo metodo
  que usa el join, asi que el cliente recibe `EnterMap` + spawns + estado completo.
- La nave, su carga y su posicion no se tocan: es el mismo slot, no uno nuevo.

**Chat.** Viaja **tipado por el mismo socket del juego** (el legado tenia un socket
aparte y una gramatica de texto con separadores sin escapar). `OnChatSend` recorta a
`ChatMaxLen`, descarta el vacio y reparte: `GLOBAL` a todos, `FACTION` solo a los de
la misma faccion. `CLAN` llega en E5. El eco vuelve tambien al emisor: el cliente
nunca pinta un mensaje que el server no confirmo.

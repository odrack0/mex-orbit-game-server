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
| Deambular de NPCs | `Game-NpcAi.cs` | destino en todo el mapa | Sustituido por la IA portada del legado (ver abajo): ya no es un tembleque de radio 800 |
| Rango de la estacion | BD `map_station.secure_range` | 1500 | Dentro de este radio se puede descargar y vender (dato, no constante) |
| `GraceMs` | `Game-World.cs` | 60 s | Ventana de reconexion tras caida de socket (auth-v1) |
| `ChatMaxLen` | `Game-World.cs` | 256 | Tope de un mensaje de chat (el mismo `max_len` del esquema) |
| `AiThinkMs` | `Game-World.cs` | 1 s | Cada cuanto PIENSA un NPC (el legado tambien pensaba 1 vez por segundo) |
| `NpcAttackIntervalMs` | `Game-World.cs` | 1 s | Cadencia de disparo del NPC |
| `NpcAttackRange` | `Game-World.cs` | 600 | Alcance de su laser (igual que el del jugador) |
| `AproximacionRadio` | `Game-World.cs` | 300 | A que distancia se planta junto a su presa (`ALIEN_DISTANCE_TO_USER` del legado) |
| `DesaggroFactor` | `Game-World.cs` | 1.8 | Se rinde a este multiplo de su radio de aggro |
| `NpcShieldRegenMs` / `NpcOutOfCombatMs` | `Game-World.cs` | 1 s / 10 s | 10% de escudo por segundo tras 10 s sin recibir fuego |
| Aggro y agresividad | BD `npc_catalog.aggro_radius` / `is_aggressive` | 500-700 · solo el Ferox | Datos, no constantes: el radio y si caza son del catalogo |
| Daño del NPC | BD `npc_catalog.damage` | 25-75 | Calibrado en la migracion `.7` por cuanto te cuesta matarlo |

## La IA de los NPCs

Portada del server legado (`Game/Objects/AI/NpcAI.cs`), en `Game/NpcAi.cs`. **Maquina de
tres estados**, un pensamiento por segundo:

1. **Buscando** — barre jugadores dentro de su `aggro_radius`. Sin presa y quieto, elige un
   punto **cualquiera del mapa** y vuela hasta el. Esto es lo que hace que el sector se
   sienta vivo: los bichos lo cruzan, no tiemblan en su sitio.
2. **VolandoAlEnemigo** — se coloca en un punto aleatorio del **circulo** de radio 300
   alrededor del jugador, no encima de el: asi rodean en vez de amontonarse en un pixel.
3. **EsperandoQueSeMueva** — aguanta ahi; si el jugador se mueve, vuelve a aproximarse.

**Pasivo no es inofensivo.** Recibir un golpe convierte a cualquier NPC en agresor (el
`ReceiveAttack` del legado), sea o no `is_aggressive`. En el 1-1 solo el **Ferox** caza por
iniciativa propia; los otros cuatro devuelven el fuego.

**La zona segura de la estacion es el DMZ del legado**: dentro de ella no se entra ni se
elige presa.

Lo que **no** se copio del legado:

- Sorteaba el destino en `20000x12800` a mano teniendo un mapa de `20800x12800`, asi que sus
  bichos nunca visitaban la franja derecha. Aqui los limites salen del mapa.
- Usaba `RenderRange` (2000, fijo en codigo) como radio de aggro. Aqui es
  `npc_catalog.aggro_radius`, un dial por especie en BD.
- Su bucle recorria **todos** los jugadores en rango sin cortar, asi que mandaba el ultimo de
  la lista. Aqui gana el mas cercano.
- `DateTime.Now` disperso; aqui el tiempo es el tick inyectado.

## Muerte del jugador

Cuando el casco llega a 0: `EntityDestroyed`, y la **bodega volante** se queda en el sitio
dentro de una caja — *transferencia, no destruccion* (guidelines §7). El **almacen de la base
no se toca**: para eso `player_cargo_hold` esta separado de `player_resource_balance` desde el
dia uno. La salida se asienta en el ledger como `CARGO_LOST` con la caja como referencia.

Despues el server manda `RespawnOptions`. En el slice hay una sola opcion (volver a la base,
entera y gratis); el contrato ya transporta coste y disponibilidad para las demas. Mientras
esta muerto, el jugador no vuela ni dispara, y los NPCs que lo tenian fichado lo olvidan.

## Mobiliario del mapa

Portales, estacion y POIs **no entran por relevancia**: viajan completos dentro de
`EnterMap` al entrar al mapa (spec del protocolo). `LoadPortals` lee `map_portal`
(solo los `is_visible`) y resuelve el `code` del mapa destino, para que el cliente
sepa a donde lleva cada uno sin adivinar ids. `map_portal.target_map_id` es FK real
—"el destino existe por construccion"— asi que crear un portal implica crear su
mapa destino: por eso la migracion `.3` declara `1-2` junto al portal del `1-1`.

El **salto** en si (con `portal_jump_delay_ms`, ya en `game_setting`) es de E3; hoy
el portal es mobiliario visible y navegable.

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

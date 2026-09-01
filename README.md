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

- .NET 10, C# con `Nullable` y `ImplicitUsings` activos.
- Base de datos: MySQL con Dapper, esquema nuevo y migraciones versionadas como disciplina.
- Transporte: WebSocket binario (`ws://` en dev; el TLS lo aporta la infraestructura en prod).

## Relación con otros repos

| Repo | Relación |
|---|---|
| `mex-orbit-client` | Su único consumidor en tiempo real, vía el protocolo nuevo |
| `mex-orbit-api` | Frontera por definir: quién es dueño del Mercado de órdenes, el almacén y la persistencia compartida |
| `mex-orbit-docs` | El diseño rector (Guidelines generales) y el documento del pilar 01-protocolo / 02-servidor |

## Referencia rectora

Los **Guidelines generales del juego** (`mex-orbit-docs`): toda mecánica implementada aquí se valida contra ellos.

## Estado

Vertical slice E2/I7 jugable: mapas bajo demanda, combate, NPCs con IA portada del legado,
bodega y cajas, base con descarga y venta, reconexión con ventana de gracia, chat y salto de
sector, con relevancia por rango. Repartido en cinco proyectos (ver **Arquitectura**) y 88
pruebas de caracterización.

## Convenciones de codigo

> **Codigo en ingles, comentarios en español.** Esta es la regla del repo, y lleva
> frontera propia porque sin ella se cuela sola.

| Que | Idioma | Por que |
|---|---|---|
| Tipos, metodos, propiedades, campos, **parametros y variables locales** | ingles | Es codigo. Un `presa` entre `prey` y `target` es exactamente el ruido que la regla evita |
| **Nombres de prueba** (`The_shield_absorbs_before_the_hull`) | ingles | Tambien son codigo. Aqui estuvo la duda y se cerro a proposito: una excepcion "porque se leen como frases" habria dejado la frontera al gusto de cada uno |
| Nombres de archivo | ingles | Siguen al tipo que contienen |
| Comentarios y `<summary>` | **español** | Son la documentacion, y esa va en español |
| Mensajes de log y de error | **español** | Los lee una persona, no un compilador |
| Esquema de BD y `@parametros` de Dapper | ingles | Los fija `mex-orbit-data-base`; Dapper enlaza por el NOMBRE de la propiedad, asi que renombrar una variable rompe su `@parametro` sin que el compilador diga nada |
| Campos del protocolo | ingles | Los genera `mex-orbit-protocol` desde el esquema |

Renombrar en masa con un `sed` a pelo **no vale**: destroza justo la mitad que hay
que conservar —convierte "Mapa sin estacion" en "Map sin estacion"— y se mete
dentro de las SQL multilinea dejando los `@parametros` a medias. Si hay que hacer
una pasada grande, el barrido tiene que partir cada linea en tramos (codigo,
cadena, comentario) y tocar solo los de codigo.

## Arquitectura

**Cebolla de cinco proyectos.** La direccion de las dependencias la impone el compilador, no
la disciplina: `MexOrbit.GameServer.Domain` no tiene **ni una sola** referencia —ni a MySQL,
ni al protocolo, ni al logging— y cada capa de fuera solo puede apuntar hacia dentro.

```
                     ┌──────────────────────────────┐
                     │  MexOrbit.GameServer  (host) │  raiz de composicion
                     └───────┬──────────────┬───────┘
                             │              │
           ┌─────────────────▼───┐   ┌──────▼──────────────┐
           │   Infrastructure    │   │      Protocol       │   adaptadores
           │  MySQL · Ed25519    │   │  codec del cable    │
           └─────────────────┬───┘   └──────┬──────────────┘
                             │              │
                     ┌───────▼──────────────▼───────┐
                     │        Application           │   casos de uso + puertos
                     │  World · Universe · Handshake│
                     └───────────────┬──────────────┘
                                     │
                     ┌───────────────▼──────────────┐
                     │           Domain             │   reglas del juego
                     │  Entity · NpcAi · Dials      │   (cero dependencias)
                     └──────────────────────────────┘
```

| Proyecto | Que vive ahi | Que NO puede saber |
|---|---|---|
| `Domain` | `Entity`, `NpcAi`, `Dials`, `Rules` (combate, botin, geometria), los modelos del juego | Que existe una BD, un socket o un protocolo binario |
| `Application` | `World` (el tick), `Universe` (los mapas), `Handshake`, los **puertos** y los **eventos** | Que la BD es MySQL o que el cable es protobuf-like |
| `Infrastructure` | Los seis repositorios Dapper/MySQL, el verificador Ed25519, el reloj | Nada del juego que no venga por un puerto |
| `Protocol` | El `ServerCodec` y el lector de frames. El **unico** que compila `Messages.g.cs` | Las reglas del juego |
| `MexOrbit.GameServer` | `Program.cs` y el WebSocket. Solo transporte y cableado | — |

**Los puertos.** La simulacion pide lo que necesita en su propio idioma: `IMapCatalog`,
`IGameCatalog`, `IServerSettings`, `IPlayerRepository`, `ISessionRepository`,
`IEconomyRepository`, `IClientPort`, `IServerCodec`, `ITicketVerifier`, `IClock`. Estan
partidos por **motivo**, no por tabla: quien lee catalogos al levantar un mapa no tiene nada
que ver con quien mueve credits dentro de una transaccion.

**El protocolo, fuera del juego.** El mundo emite **eventos** (`AttackLanded`, `BoxSpawned`,
`RespawnOffered`...) y el `ServerCodec` los pone en el cable. Antes las reglas de combate
llamaban a `.Encode()` a media funcion. El broadcast sigue costando **una** serializacion:
el codec devuelve el frame y el mismo array viaja a todos.

**La cebolla la comprueba el build.** `ArquitecturaTests` lee las referencias reales de los
ensamblados compilados y falla si el dominio acaba dependiendo de MySQL o del protocolo, o si
la aplicacion conoce a alguien que no sea el dominio. Un diagrama no impide nada; una prueba
roja si.

## Pruebas

```bash
dotnet test MexOrbit.GameServer.slnx
```

**88 pruebas, ~140 ms, sin MySQL y sin socket.** Son de *caracterizacion*: se escribieron
contra el codigo ANTES de repartirlo en capas, para que el refactor no pudiera cambiar el
juego sin que nadie se enterara. Fijan el escudo antes que el casco, la cadencia de 500 ms,
la maquina de tres estados de la IA, la huida del Vorax, el DMZ de la estacion, la recogida
parcial, la ventana de gracia, el heartbeat, el salto, el chat y la relevancia por rango.

El banco (`tests/.../Mundo.cs`) arma un `World` de verdad con la BD y el socket sustituidos
por dobles, y **el codec autentico**: lo que las pruebas afirman son los mismos bytes que
recibiria el cliente de Godot.

## Relevancia por rango

El cliente **solo sabe de lo que tiene cerca**. Antes el mundo difundia todo a todos —los 54
bichos del 1-1, sus movimientos y cada caja, a cada jugador este donde este— y eso no escala:
con 29 mapas el trafico crece con el producto de entidades por jugadores, no con lo que se ve.

| Que | Rango | De donde sale |
|---|---|---|
| Naves y NPCs | 2000 | `server_setting.render_range_entities` |
| Cajas | 1250 | `server_setting.render_range_objects` |
| Portales, estacion y limites | — | **No entran por relevancia**: viajan completos en `EnterMap`, son mobiliario del mapa |

Cada jugador lleva el conjunto de lo que **su cliente cree que existe**; en cada tick se calcula
el diff y se manda lo que entra (`EntitySpawn`) y lo que sale (`EntityDespawn` con motivo
`RANGE`). El motivo **viaja**: para el cliente no es lo mismo que algo se haya ido de la pantalla
a que se lo hayan reventado.

**El objetivo seleccionado nunca sale de relevancia** (spec del protocolo). Si no, perseguir a un
Vorax que huye seria verlo evaporarse justo cuando importa, con el server diciendo todavia que lo
tienes fichado. Y al reves: **no se puede fichar lo que no se ve** — el cliente solo puede pinchar
lo que recibio, asi que esto no cambia nada jugando limpio; lo que cierra es el atajo de mandar un
id cualquiera para que el server te informe de un bicho al otro lado del mapa.

**Lo que entra en rango volando trae su rumbo.** `EntitySpawn` no lleva destino, asi que a un
spawn de una nave en pleno vuelo le sigue su `EntityMove`. Sin eso apareceria congelada hasta su
siguiente movimiento — que puede tardar segundos, o no llegar nunca si ya iba camino de su
destino. Es el mismo remate que hacia el legado (`ShipCreate` + `MoveCommand` con el tiempo
restante).

Tres cosas se hacen **distinto** del server legado, a proposito:

1. **Se difunde a quien ME VE, no a quien VEO YO.** El legado recorria el conjunto del emisor
   (`SendCommandToInRangePlayers` sobre sus propios `InRangeCharacters`). Con rangos simetricos
   coincide, pero en cuanto uno tenia el rango doblado —la skill Recon— mandaba sus movimientos a
   gente que nunca habia recibido su `ShipCreate`: el cliente recibia un Move de una nave que para
   el no existia.
2. **Hay histeresis.** El legado tenia un umbral y lo evaluaba cada tick, asi que un jugador
   parado justo en el borde generaba un spawn y un despawn **cada 84 ms**. Aqui se entra a 2000 y
   no se sale hasta 2200.
3. **Solo observan los jugadores.** El legado calculaba el conjunto para todos los personajes,
   NPC contra NPC incluidos —54x54 comparaciones— para alimentar a una IA que aqui ya usa
   `npc_catalog.aggro_radius` y no necesita el conjunto para nada.

**Los ids se reutilizan.** Un NPC que cae vuelve con el MISMO `entity_id`, asi que al morir hay que
olvidarlo del conjunto de todos: si no, reaparecia en el mapa sin que su cliente se enterase nunca.

**Coste.** Un recorrido por tick de jugadores x (NPCs + jugadores). Con 54 bichos y los jugadores
de un sector es despreciable, y es lo mismo que hacia el legado a 84 ms. Si un mapa se llena, el
siguiente paso es una rejilla espacial — no bajar la cadencia, que es lo que se nota.

**Consecuencia visible:** el minimapa deja de mostrar el sector entero. Es fiel al original —el
minimapa es tu radar, no un mapa de calor omnisciente— y se decidio a proposito.

## Diales

Constantes calibrables del codigo (los numeros de JUEGO viven en BD). **Regla del repo: todo dial nuevo se documenta aqui en el mismo commit que lo crea.**

| Dial | Donde | Valor | Que hace |
|---|---|---|---|
| `TickMs` | `appsettings.json` | 80 ms | Tick fijo de simulacion (12.5 Hz, herencia del prototipo) |
| `PingIntervalSeconds` y `PingMissesToDrop` | `appsettings.json` | 10 s, 3 fallos | Heartbeat: 3 pings sin Pong = socket muerto |
| `LaserRange` | `Domain/Dials.cs` | 600 | Alcance del laser; fuera de rango el laser espera, no se apaga |
| `AttackIntervalMs` | `Domain/Dials.cs` | 500 ms | Cadencia de golpe (con ION-1 de 60: 120 dps, TTK del Vex ~10 s) |
| `CollectRange` | `Domain/Dials.cs` | 250 | Distancia maxima para recolectar una caja |
| `BoxTtlMs` | `Domain/Dials.cs` | 150 s | Vida de la caja (2-3 min, guidelines seccion 7) |
| Write-behind | `Domain/Dials.cs` | 30 s | Cadencia maxima de persistencia de player_ship_state |
| Deambular de NPCs | `Domain/NpcAi.cs` | destino en todo el mapa | Sustituido por la IA portada del legado (ver abajo): ya no es un tembleque de radio 800 |
| Rango de la estacion | BD `map_station.secure_range` | 1500 | Dentro de este radio se puede descargar y vender (dato, no constante) |
| `GraceMs` | `Domain/Dials.cs` | 60 s | Ventana de reconexion tras caida de socket (auth-v1) |
| `ChatMaxLen` | `Domain/Dials.cs` | 256 | Tope de un mensaje de chat (el mismo `max_len` del esquema) |
| `AiThinkMs` | `Domain/Dials.cs` | 1 s | Cada cuanto PIENSA un NPC (el legado tambien pensaba 1 vez por segundo) |
| `NpcAttackIntervalMs` | `Domain/Dials.cs` | 1 s | Cadencia de disparo del NPC |
| `NpcAttackRange` | `Domain/Dials.cs` | 600 | Alcance de su laser (igual que el del jugador) |
| `AproximacionRadio` | `Domain/Dials.cs` | 300 | A que distancia se planta junto a su presa (`ALIEN_DISTANCE_TO_USER` del legado) |
| `DesaggroFactor` | `Domain/Dials.cs` | 1.8 | Se rinde a este multiplo de su radio de aggro |
| `NpcShieldRegenMs` / `NpcOutOfCombatMs` | `Domain/Dials.cs` | 1 s / 10 s | 10% de escudo por segundo tras 10 s sin recibir fuego |
| Aggro y agresividad | BD `npc_catalog.aggro_radius` / `is_aggressive` | 500-700 · solo el Ferox | Datos, no constantes: el radio y si caza son del catalogo |
| Daño del NPC | BD `npc_catalog.damage` | **10 (plano, temporal)** | Los 25-85 de la migracion `.7` se calibraron contra un jugador al que no se podia perseguir; con eso arreglado, la dificultad esta sin medir (migracion `.28.2`) |
| Huida | BD `npc_catalog.flee_hp_pct` | 30 solo en el Vorax | Debajo de ese % de casco, el bicho se larga |
| Combate NPC->jugador | BD `server_setting.npc_combat_enabled` | **1 (encendido)** | Apagado, los NPC persiguen pero no disparan |
| Relevancia de entidades | BD `server_setting.render_range_entities` | 2000 | A que distancia el cliente empieza a recibir naves y NPCs |
| Relevancia de cajas | BD `server_setting.render_range_objects` | 1250 | Lo mismo para las cajas: mobiliario menudo, rango mas corto |
| Histeresis de relevancia | BD `server_setting.render_range_hysteresis_pct` | 10 % | Margen extra para SALIR; 0 = un solo umbral, como el legado |
| `HuidaMs` / `HuidaDistancia` | `Domain/Dials.cs` | 12 s / 2500 | Cuanto corre un cobarde y hasta donde |
| `JumpRange` | `Domain/Dials.cs` | 600 | Hay que estar JUNTO al portal para saltar (se valida en el server) |
| `MargenDelMapa` | `Domain/Dials.cs` | 500 | Margen que los NPC dejan a los bordes al elegir destino |
| `RadiationMargin` | `Domain/Dials.cs` | 1000 | Cuanto se puede rebasar el limite del mapa antes del borde de verdad, por los cuatro lados (negativo por el lado del 0) — **mismo numero en el cliente** (`world.gd`, `RADIACION_MARGEN`) |
| `RadiationTickMs` | `Domain/Dials.cs` | 1 s | Cadencia del daño por radiacion |
| `RadiationInitialPct` / `RadiationEscalationPct` | `Domain/Dials.cs` | 10 % / +1 %/s | % del casco MAXIMO que cobra la zona radiactiva: 10, 11, 12... por segundo CONTINUO fuera del limite; directo al casco, el escudo no absorbe nada. Primer numero de diseño, sin calibrar contra el juego real (ver «Zona radiactiva» abajo) |

## La IA de los NPCs

Portada del server legado (`Game/Objects/AI/NpcAI.cs`), en `Game/NpcAi.cs`. **Maquina de
tres estados**, un pensamiento por segundo:

1. **Buscando** — barre jugadores dentro de su `aggro_radius`. Sin presa y quieto, elige un
   punto **cualquiera del mapa** y vuela hasta el. Esto es lo que hace que el sector se
   sienta vivo: los bichos lo cruzan, no tiemblan en su sitio.
2. **VolandoAlEnemigo** — se coloca en un punto aleatorio del **circulo** de radio 300
   alrededor del jugador, no encima de el: asi rodean en vez de amontonarse en un pixel.
3. **EsperandoQueSeMueva** — aguanta ahi; si el jugador se mueve, vuelve a aproximarse.

**El combate NPC→jugador tiene interruptor.** `server_setting.npc_combat_enabled` (hoy en **1**)
apaga solo el disparo: los bichos siguen vagabundeando, fichandote, persiguiendote, y el Vorax
sigue huyendo malherido. Lo unico que no ocurre es el daño. Esta en BD y no en `appsettings.json`
porque es una decision de JUEGO, no de despliegue — y asi queda asentado en
`server_setting_audit` quien lo movio. Se lee al arrancar: cambiarlo pide reiniciar el server,
que lo anuncia en su log de arranque.

**Los cobardes huyen.** `npc_catalog.flee_hp_pct` (0 = jamas huye) manda al NPC a correr
en direccion contraria a quien le pega cuando su casco baja de ese porcentaje: suelta la presa,
deja de disparar y no se da la vuelta ni aunque le sigas pegando. El **Vorax** es el primero
(30%). Es un dial de BD, no un `if` por especie en el codigo: cualquier bicho futuro puede ser
cobarde sin tocar el server.

**Pasivo no es inofensivo.** Recibir un golpe convierte a cualquier NPC en agresor (el
`ReceiveAttack` del legado), sea o no `is_aggressive`. En el 1-1 solo el **Ferox**, el **Mordax**
y el **Vorax** cazan por iniciativa propia; el resto devuelve el fuego.

**Y el golpe NO lo frena.** Lo hizo durante unas horas: el frenazo entro cuando todavia no habia
IA y un bicho golpeado seguia paseando hasta salirse del alcance del laser. Esa misma tarde
llego la maquina de estados con `FightBack`, que resuelve lo mismo por el buen camino —el bicho
viene A POR TI— y desde entonces el frenazo cancelaba la persecucion que acababa de empezar.
Peor: `Approach` ya habia dejado el estado en `WaitingForPrey`, que NO vuelve a emitir destino,
asi que el bicho se quedaba plantado donde le pillo el primer disparo. Se veia como "no me
persigue". El legado nunca lo hizo: su `ReceiveAttack` son dos lineas y ninguna toca el
movimiento.

**La zona segura de la estacion protege a quien NO ha disparado.** Es el DMZ del legado:
dentro de ella nadie te ficha ni te dispara, por agresivo que sea. Pero si TU abres fuego, te
lo devuelve y te sigue hasta dentro — el refugio no es un parapeto desde el que disparar
gratis. En el codigo es la unica diferencia entre `NearestPlayer` (que jamas mira dentro) y
`PreyOf` (que si, cuando el bicho fue provocado).

Lo que **no** se copio del legado:

- Sorteaba el destino en `20000x12800` a mano teniendo un mapa de `20800x12800`, asi que sus
  bichos nunca visitaban la franja derecha. Aqui los limites salen del mapa.
- Usaba `RenderRange` (2000, fijo en codigo) como radio de aggro. Aqui es
  `npc_catalog.aggro_radius`, un dial por especie en BD.
- Su bucle recorria **todos** los jugadores en rango sin cortar, asi que mandaba el ultimo de
  la lista. Aqui gana el mas cercano.
- `DateTime.Now` disperso; aqui el tiempo es el tick inyectado.

## La bodega y las cajas

La capacidad es **identidad de la nave** (`ship_catalog.cargo_capacity`), no un producto:
las guidelines cerraron la puerta a los extensores de slot. La progresion llega por naves
mayores del roster y por el **AMP-CRG** crafteable (+% de bodega), que es de E4.

La Phoenix arranca con **300** (migracion `.8`). Antes eran 100 y la proporcion estaba rota:
un Vex suelta 30-60, asi que el jugador se llenaba cada dos muertes, y las cajas del Ferox
(hasta 180) y del Skarnox (hasta 240) **ni cabian**. La recogida parcial deja el resto en la
caja, pero la caja **expira a los 150 s**, asi que lejos de la base el sobrante se evaporaba
— y los materiales salen unica y exclusivamente de esas cajas.

Recoger nunca destruye lo que no cabe: se toma lo que quepa, el resto sigue en la caja y la
caja solo desaparece cuando queda vacia (o al expirar).

## Muerte del jugador

Cuando el casco llega a 0: `EntityDestroyed`, y la **bodega volante** se queda en el sitio
dentro de una caja — *transferencia, no destruccion* (guidelines §7). El **almacen de la base
no se toca**: para eso `player_cargo_hold` esta separado de `player_resource_balance` desde el
dia uno. La salida se asienta en el ledger como `CARGO_LOST` con la caja como referencia.

Despues el server manda `RespawnOptions`. En el slice hay una sola opcion (volver a la base,
entera y gratis); el contrato ya transporta coste y disponibilidad para las demas. Mientras
esta muerto, el jugador no vuela ni dispara, y los NPCs que lo tenian fichado lo olvidan.

## Zona radiactiva

Mas alla del limite publicado del mapa (`map_bounds.*`) la nave **sigue volando**: el
clamp del servidor (`World.Session.cs`, `OnMoveIntent`) ya no corta en el limite a
secas, corta en el limite **mas** `Dials.RadiationMargin` (1000 u) **por los cuatro
lados** — el mismo margen que aplica el cliente sobre su propio clamp (`world.gd`,
`RADIACION_MARGEN`), para que cliente y autoridad sigan coincidiendo en el destino
tal como exige el resto del movimiento.

**Por el lado del 0 el margen es negativo, y eso costo una vuelta entera.** La
primera version (1-sep por la mañana) solo funcionaba por la derecha y por abajo:
el reporte en vivo fue «la nave se para en seco en el borde», y el log del server
lo dijo claro — todo llegaba como `pidio (0, y)`. No era el clamp de radiacion:
eran **cinco capas** que daban por hecho coordenadas sin signo, cada una
suficiente sola para dejar el borde en pared: el `Vector2.ZERO` del clamp del
cliente, el `uint` del cable (`MoveIntent`, `EntityMove`, `EntitySpawn`,
`BoxSpawn`), el `Math.Max(0, v)` de `WireMapping.Round`, el `0` de este clamp y
el `INT UNSIGNED` de `player_ship_state.pos_x/pos_y`. Ahora las coordenadas de
entidades y cajas van en `sint` (zigzag) en el protocolo, `PlayerData.PosX/PosY`
son `int`, y la migracion `2026.09.01.1` firma la columna. **Es un cambio de
encoding del mismo tag, no un tag nuevo: game server, cliente y migracion se
despliegan JUNTOS** (en astrion tambien). El mobiliario (`map_station`,
`map_portal`, `EnterMap`) se queda sin signo: una base nunca esta fuera del mapa.

Fuera del limite, cada segundo **continuo** de exposicion cobra un % del casco
**maximo**, directo — a diferencia del laser, el escudo no absorbe nada
(`World.Radiation.cs`, formula pura en `Combat.RadiationDamage`). Empieza en
`RadiationInitialPct` (10 %) y sube `RadiationEscalationPct` (1 punto) cada segundo
que se sigue ahi: 10, 11, 12... El primer golpe pega **en el mismo tick** que se
cruza el limite, no un segundo despues. Volver dentro del limite reinicia el
contador a cero (edge-triggered, igual que el `Storage.IsInRadiationZone` del
prototipo): la escalada es por estancia continua, no una cuenta de por vida.

Si el casco llega a 0 ahi fuera, la muerte es por `DeathCause.Radiation` — no hay
agresor, así que `killer_id` es la propia nave y el nombre que ve el jugador es
"la radiación" (mismo `RespawnOptions`/`EntityDestroyed` que cualquier otra muerte).

**Numeros de diseño, no calibrados contra el juego real.** A diferencia del daño
plano del prototipo (fijo, sin escalar), este es el primer intento de una curva
porcentual; si en juego se siente demasiado suave o demasiado dura, los diales a
tocar son exactamente esos dos, en `Domain/Dials.cs`.

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

## Consecuencia de los mapas bajo demanda: los NPC no repueblan sin nadie delante

Un mundo sin jugadores **no se tickea** —es lo que evita simular 28 mapas vacíos— y eso incluye la cola
de respawns. Un mapa que se deja vacío conserva sus bajas: los temporizadores no corren hasta que
alguien vuelve, y solo entonces empiezan a descontar.

Para el juego puede ser incluso lo deseable (nadie simula lo que nadie ve), pero **tiene dos efectos
que conviene conocer**:

- Quien vacía un mapa y vuelve enseguida se lo encuentra igual de vacío, y la espera empieza **al
  llegar**, no al irse.
- En pruebas repetidas contra un servidor de larga vida, el `1-1` se despuebla: cada pasada del gate
  se lleva un Vex y nada los repone mientras el bot anda por otros sectores. Un gate que falla con
  *"no hay ningún vex en el mapa"* casi siempre es esto, no una regresión — se confirma reiniciando el
  servidor.

**Está sin decidir** si los respawns deberían correr igualmente en mapas vacíos. Hacerlo cuesta poco
(una pasada barata sobre la cola, sin simular nada más) y quita las dos rarezas de arriba.

## Despliegue

El game server vive en `/opt/astrion/gs` y el mundo se alcanza en
`wss://astrion-gs.turname.mx/ws`. nginx termina el TLS; el puerto 5210 es interno y nadie de fuera
lo ve.

```bash
ssh root@74.208.108.67 'bash -s' < deploy/deploy.sh
```

**El guion actualiza `mex-orbit-protocol` antes de publicar**, y no es un detalle de comodidad: el
`.csproj` compila `Messages.g.cs` desde el repo hermano por ruta relativa. Publicar sin refrescarlo
compila contra un wire viejo, y eso no falla al arrancar — falla como mensajes que el cliente no
entiende, que es mucho peor de diagnosticar.

### Lo único que nginx tiene que hacer bien

`deploy/astrion-gs.nginx.conf` lleva tres líneas que un proxy normal no lleva, y sin las tres el
juego no conecta:

| Línea | Sin ella |
|---|---|
| `Upgrade` / `Connection: upgrade` | nginx contesta 200 a un handshake de WebSocket y el cliente espera para siempre |
| `proxy_http_version 1.1` | HTTP/1.0 no sabe de *upgrades* |
| `proxy_read_timeout 3600s` | el mundo puede pasar minutos callado si el jugador está quieto; con los 60 s por defecto nginx corta la conexión y el jugador ve una desconexión que nadie provocó |

Se comprueba con un `curl` que pida el *upgrade*: tiene que responder **`101 Switching Protocols`**.
Ojo con `curl -I`, que manda `HEAD` y siempre da 400 — el handshake exige `GET`.

### El orden de arranque importa

`astrion-gs.service` declara `After=astrion-api.service` porque la API **genera el par Ed25519 la
primera vez que arranca** y este servicio verifica los tickets con la pública. En un servidor recién
instalado, al revés, el juego arranca contra una clave que aún no existe.

### El cliente de consola es la sonda de producción

`tools/console-client` apunta a donde se le diga y sale con 0 solo si recibió `Welcome`, `EnterMap`,
spawns y ecos de movimiento que avanzan. Es la prueba de humo real: hace el recorrido entero
—login HTTP, WSS por nginx, ticket, mundo— con una cuenta cualquiera.

```bash
API=https://astrion.turname.mx/api dotnet run -- usuario clave
```

La URL del game server **no se configura**: usa el `game_host` que devuelve el login, igual que el
cliente de verdad. Es deliberado. Un despliegue puede tener la API perfecta y `GameHost` apuntando
todavía a `127.0.0.1`; con un parámetro aparte, la prueba pasaría sin tocar el campo que falla.

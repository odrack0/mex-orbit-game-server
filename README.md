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

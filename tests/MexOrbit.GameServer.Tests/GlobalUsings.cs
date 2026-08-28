// xunit v2 no trae usings implicitos: se declara una vez y no en cada archivo.
global using Xunit;

// Las pruebas hablan DOS idiomas a proposito, y estos alias dicen cual es cual:
//   · para PEDIR cosas al mundo, el vocabulario del dominio (un comando de chat
//     lleva un `ChatChannel` del juego, no del cable);
//   · para AFIRMAR lo que llego al cliente, el vocabulario del protocolo — que es
//     literalmente lo que veria el cliente de Godot.
// Los nombres que existen en los dos lados se resuelven aqui, una vez.
global using ChatChannel = MexOrbit.GameServer.Domain.ChatChannel;
global using EntityDestroyed = MexOrbit.Protocol.EntityDestroyed;
global using EntityKind = MexOrbit.Protocol.EntityKind;
global using ErrorCode = MexOrbit.Protocol.ErrorCode;
global using DeathCause = MexOrbit.Protocol.DeathCause;
global using BoxDespawnReason = MexOrbit.Protocol.BoxDespawnReason;
global using Weapon = MexOrbit.Protocol.Weapon;

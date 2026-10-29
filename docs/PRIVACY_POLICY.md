# NotiLink — Política de privacidad

Última actualización: 2026-09-03

NotiLink es una aplicación que refleja las notificaciones de tu teléfono Android en una
aplicación complementaria para Windows, **exclusivamente dentro de tu propia red local (WiFi)**.
No existe ningún servidor en la nube, ningún backend, ninguna cuenta de usuario ni ninguna
infraestructura operada por el desarrollador involucrada en el funcionamiento de la app.

## Qué datos toca la app y a dónde van

**Nada sale de tu red local.** Toda la comunicación ocurre directamente entre tu teléfono y tu
PC, sobre tu propia WiFi, mediante una conexión que tú mismo emparejas la primera vez (código de
6 dígitos, confirmado en ambos dispositivos). El desarrollador de NotiLink nunca recibe, almacena
ni tiene acceso a ninguno de estos datos — no hay ningún servidor intermedio.

Concretamente, lo que se transmite (solo entre tu teléfono y tu PC, nunca a terceros):

- El contenido de las notificaciones que tú elijas mirar reflejadas (título, texto, imágenes
  adjuntas) — filtrable por app desde la propia PC.
- Nivel de batería, estado de WiFi/datos móviles, la app que se está reproduciendo actualmente
  (si aplica) y, opcionalmente, tu fondo de pantalla — únicamente si activas esas funciones.
- Un token de emparejamiento, generado localmente y almacenado cifrado (Android Keystore en el
  teléfono, DPAPI de Windows en la PC) — nunca una contraseña ni datos de una cuenta.

## Registro de diagnóstico (DiagLog)

La app mantiene un pequeño registro de diagnóstico en el propio dispositivo, pensado para poder
solucionar problemas de conexión o de acciones que no funcionan. Este registro puede incluir
metadatos limitados de alguna notificación puntual (por ejemplo, el nombre de quien llama en una
notificación de llamada), con el único propósito de depuración técnica.

- Se guarda únicamente en el almacenamiento privado de la app, en tu propio teléfono.
- Solo se envía a tu propia PC ya emparejada, sobre tu red local, y solo cuando tú lo solicitas
  expresamente (botón "Enviar logs") o al reconectar — nunca al desarrollador ni a ningún
  servidor externo.
- Se puede borrar en cualquier momento desinstalando la app.

## Permisos que solicita la app y por qué

- **Acceso a notificaciones** (`NotificationListenerService`): es la función central de la
  app — leer el contenido de las notificaciones para reflejarlas en la PC. Puedes excluir
  cualquier app específica desde el panel de la PC.
- **Accesibilidad** (opcional, por separado): usada exclusivamente para simular el mismo toque
  que harías tú sobre una notificación real, cuando pides "Abrir en el teléfono" desde la PC —
  no se usa para ningún otro fin, y puedes desactivarla en cualquier momento sin perder el resto
  de funciones de la app.
- **Exención de optimización de batería**: para que el servicio en segundo plano que mantiene la
  conexión no sea cerrado agresivamente por el sistema.
- **Wi-Fi / estado de red, ubicación aproximada (para el nombre de la red Wi-Fi)**: solo para
  mostrar en la PC a qué red estás conectado, si lo permites.

## Terceros y publicidad

NotiLink no incluye publicidad, ni SDKs de analítica, ni SDKs de terceros que recolecten datos.
No se vende ni se comparte ningún dato con nadie, bajo ninguna circunstancia.

## Compartir pantalla/audio (función opcional)

Si activas "Compartir pantalla" o "Compartir audio", la app usa depuración USB/inalámbrica
(ADB) para transmitir la imagen o el audio de tu teléfono directamente a tu PC, sobre tu misma
red local — de nuevo, sin pasar por ningún servidor externo.

## Eliminar tus datos

Desinstalar la app en el teléfono, o eliminar el dispositivo emparejado desde la PC, borra el
token de emparejamiento y toda la información asociada de ambos lados. Como no existe ningún
servidor externo, no hay nada más de qué "darte de baja".

## Contacto

Para preguntas sobre esta política, abre un issue en el repositorio del proyecto:
https://github.com/BrandoRubio/NotiLink

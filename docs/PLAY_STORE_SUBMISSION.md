# Publicar NotiLink en Google Play — guía y textos listos para usar

Todo lo que sigue son textos ya redactados para pegar directamente en los formularios de Play
Console, más una checklist de lo que **tú** tienes que hacer manualmente (nada de esto se puede
automatizar desde aquí — requiere tu propia cuenta de Google, pago, y la consola web de Play).

## Antes de empezar

- `compileSdk`/`targetSdk` ya están en 35 (`android/app/build.gradle.kts`) — cumple el mínimo
  actual que exige Play. No hace falta tocar nada ahí.
- No hay SDKs de analítica/publicidad en el proyecto (`build.gradle.kts` solo tiene AndroidX,
  Material, zxing para QR, y security-crypto) — el formulario de "Seguridad de los datos" va a
  ser simple: básicamente "no se recopilan datos".
- La política de privacidad ya está redactada: [`docs/PRIVACY_POLICY.md`](PRIVACY_POLICY.md).
  **Falta publicarla en una URL pública real** (Play exige un enlace, no puede ser un archivo del
  repo) — la forma más simple es activar GitHub Pages sobre este mismo repo (ver más abajo) y
  usar la URL que te dé, algo como
  `https://brandorubio.github.io/NotiLink/PRIVACY_POLICY.html`.

## Lo que tienes que hacer tú (checklist)

1. Crear una cuenta de Google Play Console (USD $25, pago único) en
   https://play.google.com/console — requiere verificación de identidad, puede tardar uno o
   varios días según tu país.
2. Activar GitHub Pages en el repo (Settings → Pages → Source: rama `master`, carpeta `/docs`) y
   confirmar que `PRIVACY_POLICY.md` quede accesible por URL.
3. Generar el AAB firmado (`./gradlew.bat bundleRelease` — necesitarás una keystore de
   *release* separada de la de debug; avísame cuando llegues aquí y te ayudo con ese paso).
4. Completar la ficha de la tienda (título, descripción corta/larga, ícono, capturas de
   pantalla — puedo ayudarte a redactar los textos cuando quieras).
5. Completar el formulario de **Seguridad de los datos** — ver sección de abajo.
6. Completar la **declaración de uso de Accessibility Service** — ver sección de abajo. Este es
   el paso con más riesgo real de rechazo o pedido de aclaración por parte de Google.
7. Completar la declaración de **Acceso a notificaciones** — ver sección de abajo.
8. Enviar a revisión. Google puede tardar de días a un par de semanas, y puede pedir un video
   demostrando el uso real de Accessibility antes de aprobar — si piden eso, una grabación de
   pantalla simple usando "Abrir en el teléfono" desde la PC debería bastar.

## Formulario de Seguridad de los datos (Data safety)

- **¿La app recopila o comparte datos de usuario?** No.
- **¿Los datos se cifran en tránsito?** N/A — no hay tránsito hacia ningún servidor; toda la
  comunicación es directa entre el teléfono y tu propia PC dentro de tu red local.
- **¿Los usuarios pueden solicitar que se borren sus datos?** N/A — no se recopila ni almacena
  nada fuera del propio dispositivo del usuario.

## Declaración de Accessibility Service (el formulario más estricto)

Google pide explicar, en tus propias palabras, para qué usa la app este permiso y por qué no hay
alternativa. Texto sugerido (tradúcelo/ajústalo a tu estilo si quieres, pero mantén los puntos
técnicos):

> NotiLink is a notification-mirroring companion app between an Android phone and a Windows PC
> on the same local network. Its core function is to let the user act on their phone
> notifications from the PC. One specific action — "open this notification on the phone",
> requested from the PC — requires simulating the exact tap the user would perform on the
> notification shade themselves, so the source app's own PendingIntent fires from a privileged
> system context. Directly invoking a notification's PendingIntent from a third-party
> background service is blocked by Android's Background Activity Launch restrictions since
> Android 10, with no other public API available to achieve this. AccessibilityService is used
> here strictly as an automation mechanism to replay a single user-initiated action (open the
> notification shade, locate the matching notification, perform its own default click action) —
> it does not read, log, or transmit any content beyond what's needed to locate that one
> notification, and never operates outside of this explicit, user-triggered request. This
> permission is entirely optional: NotiLink's core notification-mirroring function works fully
> without it: only the "open on phone" button is unavailable if the user declines it.

Puntos que probablemente te van a pedir aclarar si hacen una segunda ronda de preguntas:
- Que es un permiso **opcional**, separado del resto de la app (ya lo es — pantalla de onboarding
  propia, casilla independiente).
- Que no hay lectura continua de la pantalla ni recolección de texto en segundo plano — solo se
  activa al recibir un comando explícito desde la PC.

## Declaración de Acceso a notificaciones

Más sencilla de justificar — es literalmente la función principal de la app:

> NotificationListenerService is NotiLink's core, only-reason-the-app-exists function: mirroring
> the user's own phone notifications to their own Windows PC on the same local network, so they
> can view and act on them without picking up the phone. Users can exclude specific apps from
> being mirrored at any time.

## Alternativa a tener en cuenta

Si en algún punto la revisión de Play se alarga demasiado o rechaza la app por el uso de
Accessibility, la salida de respaldo sigue siendo distribuir el APK directamente (GitHub
Releases, como ya se hace con el instalador de PC) — sin pasar por la revisión de Google en
absoluto. No hace falta decidir esto ahora; podemos intentar Play primero y cambiar de plan si
hace falta.

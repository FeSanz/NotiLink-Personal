NotiLink - Requisitos mínimos
=============================

NotiLink refleja en la PC las notificaciones de tu celular Android por la red local
(Wi-Fi/Ethernet), sin pasar por ningún servidor en internet. Son dos aplicaciones: la de
Windows (este instalador) y la de Android.


PC (Windows)
------------

- Sistema operativo: Windows 10 versión 1809 (compilación 17763) o posterior, 64 bits.
  Recomendado: Windows 11.
- Procesador: x64 (Intel/AMD de 64 bits). No hay versión para ARM ni 32 bits.
- Memoria: 4 GB de RAM como mínimo (la app usa unos 250 MB).
- Espacio en disco: unos 300 MB.
- Microsoft Edge WebView2 Runtime: ya viene en Windows 11. Si falta (algunos Windows 10), el
  instalador lo descarga e instala de Microsoft automáticamente (necesita internet solo en
  ese momento).
- .NET: no hace falta instalar nada; la app trae su propio runtime integrado.
- Red: la PC y el celular deben estar en la misma red local. NotiLink usa el puerto TCP 45212
  (conexión) y UDP 45211 (búsqueda automática del celular). La primera vez que abras la app,
  Windows puede preguntar si permites el acceso en el firewall: acepta "Redes privadas".
- La red debe estar marcada como "Privada" en Windows; en una red "Pública" el firewall
  bloquea la conexión.


Celular (Android)
-----------------

- Android 8.0 (Oreo) o posterior.
- Misma red Wi-Fi que la PC.
- Permisos que pide la app (cada uno es opcional salvo el primero):
  - Acceso a notificaciones (obligatorio): para leer y reflejar las notificaciones.
  - Sin restricciones de batería: para que la conexión no se corte en segundo plano.
  - Accesibilidad: abrir una notificación desde la PC, y contestar/colgar/altavoz/silenciar
    llamadas.
  - Contestar llamadas: contestar y colgar desde la PC.
  - No molestar: activar/desactivar No molestar desde la PC.
  - Modificar ajustes del sistema: controlar la rotación automática desde la PC.
  - Notificaciones y fotos: avisos propios de la app y fondo de pantalla del celular en la PC.


Funciones con requisitos adicionales
------------------------------------

- Compartir pantalla y compartir audio: usan scrcpy (incluido en el instalador). Requieren
  activar en el celular las "Opciones de desarrollador" y la "Depuración USB" o "Depuración
  inalámbrica", y conectar el celular por USB una vez para habilitar la conexión por Wi-Fi.
  Compartir audio requiere Android 11 o posterior.
- El audio de las llamadas no se puede pasar a la PC: Android lo bloquea para cualquier app
  que no sea del sistema. Las llamadas sí se pueden contestar, colgar y controlar desde la PC.


Seguridad y firma
-----------------

- El instalador y la app están firmados con el certificado propio de NotiLink, no con uno de
  una autoridad comercial. En otra PC, Windows SmartScreen puede mostrar "Windows protegió su
  PC": elige "Más información" y luego "Ejecutar de todas formas".
- Si en esa PC está activo Smart App Control (Windows 11), puede bloquear el instalador. En ese
  caso instala primero el certificado NotiLink-Signing.cer que acompaña al instalador
  (doble clic, "Instalar certificado", "Usuario actual", almacén "Entidades de certificación
  raíz de confianza") y vuelve a abrir el instalador. Para evitar estos avisos en cualquier PC
  hace falta un certificado de firma de código emitido por una autoridad reconocida.
- Desinstalar NotiLink no borra tus celulares vinculados ni la configuración
  (%LocalAppData%\NotiLink); así una reinstalación los conserva.

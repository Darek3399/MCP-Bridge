<a id="top"></a>

<div align="right">
  <a href="README.md"><img src="https://img.shields.io/badge/-Русский-15322F?style=for-the-badge" alt="Русский"></a>&nbsp;&nbsp;<a href="README.en.md"><img src="https://img.shields.io/badge/-English-22252A?style=for-the-badge" alt="English"></a>
</div>

<div align="center">
  <img src="src/McpBridge/Resources/MCPBridge.ico" width="80" height="80" alt="MCP Bridge">
  <h1>mcp<b>Bridge</b></h1>
  <p>
    <b>Единый локальный хаб и менеджер MCP-серверов для Windows</b><br>
    Подключите Bridge к своему AI-клиенту один раз и управляйте всеми серверами из одного окна.
  </p>
  <p>
    <a href="../../releases/latest"><img src="https://img.shields.io/badge/⬇-Скачать%20установщик%20(Windows%20x64)-15322F?style=for-the-badge&labelColor=181A1D" alt="Скачать установщик"></a>
  </p>
  <p>
    <a href="#sec-why"><img src="https://img.shields.io/badge/01-Зачем%20нужен-22252A?style=for-the-badge&labelColor=15322F" alt="01 Зачем нужен"></a>&nbsp;
    <a href="#sec-settings"><img src="https://img.shields.io/badge/02-Настройка%20и%20URL-22252A?style=for-the-badge&labelColor=15322F" alt="02 Настройка и URL"></a>&nbsp;
    <a href="#sec-tailscale"><img src="https://img.shields.io/badge/03-Tailscale-22252A?style=for-the-badge&labelColor=15322F" alt="03 Tailscale"></a>&nbsp;
    <a href="#sec-dashboard"><img src="https://img.shields.io/badge/04-Главный%20экран-22252A?style=for-the-badge&labelColor=15322F" alt="04 Главный экран"></a>&nbsp;
    <a href="#sec-servers"><img src="https://img.shields.io/badge/05-Серверы%20и%20Пресеты-22252A?style=for-the-badge&labelColor=15322F" alt="05 Серверы и Пресеты"></a>&nbsp;
    <a href="#sec-add"><img src="https://img.shields.io/badge/06-Добавление%20MCP-22252A?style=for-the-badge&labelColor=15322F" alt="06 Добавление MCP"></a>&nbsp;
    <a href="#sec-security"><img src="https://img.shields.io/badge/07-Безопасность-22252A?style=for-the-badge&labelColor=332915" alt="07 Безопасность"></a>
  </p>
</div>

<br>

<!-- ===================================================================== -->
<!-- 01. ЗАЧЕМ НУЖЕН MCP BRIDGE                                            -->
<!-- ===================================================================== -->

<a id="sec-why"></a>
<p><a href="#sec-why"><img src="docs/images/sec-01-ru.svg" width="100%" alt="01 Зачем нужен MCP Bridge"></a></p>

<table>
  <thead>
    <tr>
      <th width="500" align="left">Чистый контекст ИИ без лишних команд</th>
      <th width="500" align="left">Одно подключение вместо десятков</th>
    </tr>
  </thead>
  <tbody>
    <tr>
      <td width="500" valign="top">
        Если подключить к ИИ сразу десяток серверов (50–100 инструментов), нейросеть начинает путаться в вызовах и тратить токены. Через Bridge вы включаете нужные серверы <b>прямо посреди диалога</b> — без перезапуска чата.
      </td>
      <td width="500" valign="top">
        Вместо ручной правки JSON-конфигов в каждом отдельном AI-клиенте вы один раз подключаете адрес Bridge, а все остальные серверы добавляете и переключаете внутри одного окна.
      </td>
    </tr>
  </tbody>
  <thead>
    <tr>
      <th width="500" align="left">Локальные STDIO-серверы в веб-приложениях ИИ</th>
      <th width="500" align="left">Контроль памяти и всё в комплекте</th>
    </tr>
  </thead>
  <tbody>
    <tr>
      <td width="500" valign="top">
        Большинство MCP-серверов работают только локально через консоль (<code>npx</code>, <code>uvx</code>, <code>python</code>), а веб-сервисы требуют внешний <code>HTTPS</code>. Bridge сам превращает любой локальный сервер в защищённую ссылку.
      </td>
      <td width="500" valign="top">
        Выключенные серверы не висят фоновыми процессами — Bridge полностью гасит их и освобождает ОЗУ. <code>Node.js</code>, <code>uv</code>, <code>Filesystem MCP</code> и <code>Tailscale</code> уже встроены в установщик.
      </td>
    </tr>
  </tbody>
</table>

<br>
<p><a href="https://pay.cloudtips.ru/p/cdb7341e"><img src="docs/images/coffee-ru.svg" width="100%" alt="На кофе разработчику"></a></p>
<br>

<!-- ===================================================================== -->
<!-- 02. ПЕРВОНАЧАЛЬНАЯ НАСТРОЙКА                                          -->
<!-- ===================================================================== -->

<a id="sec-settings"></a>
<p><a href="#sec-settings"><img src="docs/images/sec-02-ru.svg" width="100%" alt="02 Первоначальная настройка и подключение"></a></p>

<p>Откройте вкладку <b>Settings</b> — здесь настраивается доступ к папкам, внешний туннель и находятся готовые ссылки для подключения ИИ:</p>

<p><img src="docs/images/Settings.svg" width="100%" alt="Settings"></p>

<table>
  <thead>
    <tr>
      <th width="333" align="left">1. Allowed Folders</th>
      <th width="333" align="left">2. Online access</th>
      <th width="334" align="left">3. Connection URLs</th>
    </tr>
  </thead>
  <tbody>
    <tr>
      <td width="333" valign="top">
        Папки, к которым разрешён доступ для встроенного <b>Filesystem MCP</b>. Снимите галочку — доступ для ИИ закроется мгновенно без перезапуска шлюза.
      </td>
      <td width="333" valign="top">
        Включение публичного HTTPS-туннеля через <b>Tailscale Funnel</b>. Нужен только для веб-приложений ИИ. Для программ на вашем ПК включать его не нужно.
      </td>
      <td width="334" valign="top">
        Готовые адреса шлюза. Нажмите <b>Copy URL</b> напротив нужного адреса и вставьте его в настройки своего AI-клиента.
      </td>
    </tr>
  </tbody>
</table>

<table>
  <thead>
    <tr>
      <th width="500" align="left">LOCAL URL — <code>http://127.0.0.1:8004/mcp</code></th>
      <th width="500" align="left">PUBLIC URL — <code>https://&lt;device&gt;.&lt;tailnet&gt;.ts.net/mcp</code></th>
    </tr>
  </thead>
  <tbody>
    <tr>
      <td width="500" valign="top">
        Для программ, установленных на вашем ПК (IDE, десктопные клиенты, локальные агенты). Укажите этот адрес как <code>Streamable HTTP</code> в настройках MCP вашей программы.
      </td>
      <td width="500" valign="top">
        Для веб-приложений ИИ с поддержкой MCP. В разных сервисах этот раздел называется по-разному — <i>коннекторы, плагины, интеграции</i> или подключается через расширения: просто вставьте туда этот адрес.
      </td>
    </tr>
  </tbody>
</table>

<br>

<!-- ===================================================================== -->
<!-- 03. АВТОРИЗАЦИЯ TAILSCALE                                             -->
<!-- ===================================================================== -->

<a id="sec-tailscale"></a>
<p><a href="#sec-tailscale"><img src="docs/images/sec-03-ru.svg" width="100%" alt="03 Авторизация Tailscale"></a></p>

<p>При первом нажатии кнопки <b>Connect</b> в блоке <i>Online access</i> нужно пройти три экрана по очереди:</p>

<table>
  <thead>
    <tr>
      <th width="333" align="left">ШАГ 1 · Welcome to Tailscale</th>
      <th width="333" align="left">ШАГ 2 · Авторизация и Connect</th>
      <th width="334" align="left">ШАГ 3 · Start using Funnel</th>
    </tr>
  </thead>
  <tbody>
    <tr>
      <td width="333" valign="top">
        <img src="docs/images/Tailscale_Welcome.svg" width="100%" alt="Tailscale Welcome"><br><br>
        Откроется окно приложения Tailscale. Нажмите кнопку <b>Get Started</b>.
      </td>
      <td width="333" valign="top">
        <img src="docs/images/Tailscale_Device_Connect.svg" width="100%" alt="Tailscale Device Connect"><br><br>
        В браузере откроется форма входа — <b>авторизуйтесь</b>, после чего в окне <i>Connect device</i> нажмите <b>Connect</b>.
      </td>
      <td width="334" valign="top">
        <img src="docs/images/Tailscale_Funnel.svg" width="100%" alt="Tailscale Funnel"><br><br>
        Откроется страница <b>Start using Funnel</b>. Прокрутите вниз, нажмите <b>Enable Funnel</b> и дождитесь подключения.
      </td>
    </tr>
  </tbody>
</table>

<p><img src="docs/images/tailscale-note-ru.svg" width="100%" alt="Важное примечание по авторизации Tailscale"></p>

<br>

<!-- ===================================================================== -->
<!-- 04. ГЛАВНЫЙ ЭКРАН (DASHBOARD)                                         -->
<!-- ===================================================================== -->

<a id="sec-dashboard"></a>
<p><a href="#sec-dashboard"><img src="docs/images/sec-04-ru.svg" width="100%" alt="04 Главный экран (Dashboard)"></a></p>

<p>На вкладке <b>Dashboard</b> отображается сводка по шлюзу и список включённых серверов. Выключенные серверы (<code>DISABLED</code>) в списке <i>MCP Services Status</i> скрыты — они находятся во вкладке <i>MCP Services</i>.</p>

<p><img src="docs/images/Main.svg" width="100%" alt="Dashboard"></p>

<table>
  <thead>
    <tr>
      <th width="250" align="left"><img src="https://img.shields.io/badge/-ACTIVE-15322F?style=flat-square" alt="ACTIVE"></th>
      <th width="250" align="left"><img src="https://img.shields.io/badge/-WAITING-332915?style=flat-square" alt="WAITING"></th>
      <th width="250" align="left"><img src="https://img.shields.io/badge/-QUARANTINE-381B20?style=flat-square" alt="QUARANTINE"></th>
      <th width="250" align="left"><img src="https://img.shields.io/badge/-DISABLED-1D2025?style=flat-square" alt="DISABLED"></th>
    </tr>
  </thead>
  <tbody>
    <tr>
      <td width="250" valign="top">Сервер подключён, проверен и передаёт свои инструменты в диалог.</td>
      <td width="250" valign="top">Ждёт запуска внешней программы (например, Unreal Editor на порту <code>:8000</code>).</td>
      <td width="250" valign="top">Ошибка запуска или протокола MCP. Сервер изолирован и не мешает остальным.</td>
      <td width="250" valign="top">Сервер выключен вручную (отображается только во вкладке <i>MCP Services</i>).</td>
    </tr>
  </tbody>
</table>

<br>

<!-- ===================================================================== -->
<!-- 05. УПРАВЛЕНИЕ СЕРВЕРАМИ И ПРЕСЕТАМИ                                  -->
<!-- ===================================================================== -->

<a id="sec-servers"></a>
<p><a href="#sec-servers"><img src="docs/images/sec-05-ru.svg" width="100%" alt="05 Управление MCP-серверами и пресетами"></a></p>

<p><img src="docs/images/MSP_Servises.svg" width="100%" alt="MCP Services"></p>

<table>
  <thead>
    <tr>
      <th width="1000" align="left">Как работает переключение серверов в реальном времени</th>
    </tr>
  </thead>
  <tbody>
    <tr>
      <td width="1000">
        Вы можете включать и выключать любые серверы прямо во время активного разговора с ИИ. В каждый момент времени в диалоге доступны <b>только инструменты тех серверов, которые включены прямо сейчас</b>. При этом <b>не нужно перезагружать страницу в браузере</b>, перезапускать локальный клиент или вручную обновлять список инструментов в настройках ИИ — шлюз передаёт изменения автоматически.
      </td>
    </tr>
  </tbody>
</table>

<table>
  <thead>
    <tr>
      <th width="333" align="left">Server Presets (Пресеты)</th>
      <th width="333" align="left">Включение (ON) и Tools</th>
      <th width="334" align="left">Редактирование и удаление</th>
    </tr>
  </thead>
  <tbody>
    <tr>
      <td width="333" valign="top">
        Отметьте нужную комбинацию серверов для конкретной задачи и нажмите <b>+ Save Preset</b>. Переключайте готовые профили в 1 клик прямо во время работы.
      </td>
      <td width="333" valign="top">
        Галочка <b>ON</b> мгновенно подключает сервер к диалогу или полностью гасит его процесс в памяти, а плашка <code>Tools: N</code> показывает число доступных инструментов.
      </td>
      <td width="334" valign="top">
        Кнопка <b>✎</b> открывает JSON-конфигурацию выбранного сервера для правки параметров, а <b>✕</b> удаляет сервер из списка.
      </td>
    </tr>
  </tbody>
</table>

<br>

<!-- ===================================================================== -->
<!-- 06. ДОБАВЛЕНИЕ MCP-СЕРВЕРА                                            -->
<!-- ===================================================================== -->

<a id="sec-add"></a>
<p><a href="#sec-add"><img src="docs/images/sec-06-ru.svg" width="100%" alt="06 Добавление MCP-сервера"></a></p>

<p>Нажмите <b>+ Add Server</b> во вкладке <i>MCP Services</i>, чтобы подключить новый локальный или удалённый сервер:</p>

<p><img src="docs/images/Add_MCP.svg" width="100%" alt="Add MCP Server"></p>

<table>
  <thead>
    <tr>
      <th width="333" align="left">1. Вставка JSON</th>
      <th width="333" align="left">2. Проверить и добавить</th>
      <th width="334" align="left">3. Сохранить без проверки</th>
    </tr>
  </thead>
  <tbody>
    <tr>
      <td width="333" valign="top">
        Скопируйте готовый блок <code>mcpServers</code> из документации нужного сервера и нажмите <b>Вставить из буфера</b> (или отредактируйте JSON вручную).
      </td>
      <td width="333" valign="top">
        Нажмите <b>Проверить и добавить</b> — Bridge протестирует запуск, проверит список инструментов и сразу подключит сервер к шлюзу.
      </td>
      <td width="334" valign="top">
        Если сервер зависит от внешней программы, которая сейчас закрыта, нажмите <b>Сохранить без проверки</b> — он добавится в режиме <code>WAITING</code>.
      </td>
    </tr>
  </tbody>
</table>

<br>

<!-- ===================================================================== -->
<!-- 07. БЕЗОПАСНОСТЬ И ОТВЕТСТВЕННОСТЬ ПОЛЬЗОВАТЕЛЯ                       -->
<!-- ===================================================================== -->

<a id="sec-security"></a>
<p><a href="#sec-security"><img src="docs/images/sec-07-ru.svg" width="100%" alt="07 Безопасность и ответственность пользователя"></a></p>

<p><img src="docs/images/disclaimer-ru.svg" width="100%" alt="Отказ от ответственности"></p>

<table>
  <thead>
    <tr>
      <th width="500" align="left">1. Полный доступ к разрешённым папкам</th>
      <th width="500" align="left">2. Запуск сторонних STDIO-процессов</th>
    </tr>
  </thead>
  <tbody>
    <tr>
      <td width="500" valign="top">
        Встроенный <code>Filesystem MCP</code> даёт ИИ право читать, изменять и удалять любые файлы внутри отмеченных папок в <b>Allowed Folders</b>. Никогда не открывайте доступ к системным дискам целиком (<code>C:\</code>), папкам с паролями, криптокошельками или SSH-ключами.
      </td>
      <td width="500" valign="top">
        Любой добавленный вами консольный сервер (<code>npx</code>, <code>uvx</code>, <code>python</code>, <code>node</code>, <code>.exe</code>) выполняется на вашем ПК с правами вашей учётной записи Windows. Добавляйте только те серверы и пакеты, источнику которых вы полностью доверяете.
      </td>
    </tr>
  </tbody>
  <thead>
    <tr>
      <th width="500" align="left">3. Публичная ссылка Tailscale Funnel</th>
      <th width="500" align="left">4. Хранение ключей и токенов</th>
    </tr>
  </thead>
  <tbody>
    <tr>
      <td width="500" valign="top">
        При включённом туннеле ваш <code>PUBLIC URL</code> доступен из интернета любому, кто узнает этот адрес. Не публикуйте эту ссылку в открытом доступе, не светите её на скриншотах и отключайте туннель кнопкой <b>Disconnect</b>, когда он вам не нужен.
      </td>
      <td width="500" valign="top">
        Указанные вами переменные окружения (<code>env</code>) и HTTP-заголовки авторизации (<code>Authorization</code> / API-ключи) сохраняются локально в открытом виде в файле <code>Config/settings.json</code>. Не передавайте этот файл посторонним.
      </td>
    </tr>
  </tbody>
</table>

<br>

<div align="center">
  <p><a href="#top"><img src="https://img.shields.io/badge/-↑%20Вернуться%20наверх-181A1D?style=for-the-badge" alt="Наверх"></a></p>
  <sub>
    <b>Системные требования:</b> Windows 10 x64 (1903+) / Windows 11 x64 · .NET Framework 4.8<br>
    <a href="docs/ARCHITECTURE.md">Архитектура</a> · <a href="SECURITY.md">Безопасность</a> · <a href="THIRD_PARTY_NOTICES.md">Сторонние компоненты</a> · <a href="CONTRIBUTING.md">Участие в разработке</a>
  </sub>
</div>

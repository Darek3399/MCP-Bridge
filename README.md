<a id="top"></a>

<!-- Плавающая кнопка «Наверх» в правом нижнем углу при скролле -->
<div align="right" style="position: fixed; bottom: 24px; right: 28px; z-index: 999;">
  <a href="#top"><img src="https://img.shields.io/badge/-↑%20Наверх-181A1D?style=for-the-badge" alt="Наверх"></a>
</div>

<!-- Шапка: логотип по центру и переключатель языка на одном уровне -->
<table width="100%" style="display: table; width: 100%; table-layout: fixed; border-collapse: collapse; border: none; margin-top: 8px; margin-bottom: 12px;">
  <tr>
    <td width="25%" valign="top" style="border: none; background: transparent; padding: 0;"></td>
    <td width="50%" valign="top" align="center" style="border: none; background: transparent; padding: 0;">
      <img src="src/McpBridge/Resources/Svg/bridge-mark.svg" width="80" height="80" alt="MCP Bridge">
      <h1 style="margin: 10px 0 6px 0; font-size: 36px; border: none; padding: 0;">mcp<b>Bridge</b></h1>
    </td>
    <td width="25%" valign="top" align="right" style="border: none; background: transparent; padding: 4px 0 0 0; white-space: nowrap;">
      <a href="README.md"><img src="https://img.shields.io/badge/-Русский-15322F?style=for-the-badge" alt="Русский"></a>&nbsp;&nbsp;<a href="README.en.md"><img src="https://img.shields.io/badge/-Английский-22252A?style=for-the-badge" alt="Английский"></a>
    </td>
  </tr>
</table>

<div align="center" style="margin-bottom: 36px;">
  <p style="color: #B8C0C8; font-size: 16px; line-height: 1.55; max-width: 640px; margin: 0 auto 22px auto;">
    <b>Единый локальный хаб и менеджер MCP-серверов для Windows</b><br>
    Подключите Bridge к своему AI-клиенту один раз и управляйте всеми серверами из одного окна.
  </p>
  <p style="margin-bottom: 26px;">
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

<!-- ===================================================================== -->
<!-- 01. ЗАЧЕМ НУЖЕН MCP BRIDGE                                            -->
<!-- ===================================================================== -->

<a id="sec-why"></a>
<table width="100%" style="display: table; width: 100%; table-layout: fixed; border-collapse: collapse; margin-top: 48px; margin-bottom: 22px;">
  <thead>
    <tr>
      <th align="left" style="background: linear-gradient(90deg, #1B3B38 0%, #162226 100%); border: 1px solid #2CE5D0; border-left: 6px solid #2CE5D0; border-radius: 4px; padding: 18px 24px; font-size: 21px; color: #FFFFFF; letter-spacing: 0.4px;">
        <span style="color: #2CE5D0;">01 ·</span> ЗАЧЕМ НУЖЕН MCP BRIDGE
      </th>
    </tr>
  </thead>
</table>

<table width="100%" style="display: table; width: 100%; table-layout: fixed; border-collapse: collapse; border: none; margin-bottom: 0;">
  <tr>
    <td width="49%" valign="top" style="background: #141619; border: 1px solid #262A30; border-radius: 4px; padding: 22px 24px;">
      <div style="color: #EAECEF; font-size: 18px; font-weight: 700; margin-bottom: 10px;">Чистый контекст ИИ без лишних команд</div>
      <div style="color: #969FA8; font-size: 15px; line-height: 1.6;">
        Если подключить к ИИ сразу десяток серверов (50–100 инструментов), нейросеть начинает путаться в вызовах и тратить токены. Через Bridge вы включаете нужные серверы <b style="color: #D6DCE2;">прямо посреди диалога</b> — без перезапуска чата.
      </div>
    </td>
    <td width="2%" style="border: none; background: transparent; padding: 0;"></td>
    <td width="49%" valign="top" style="background: #141619; border: 1px solid #262A30; border-radius: 4px; padding: 22px 24px;">
      <div style="color: #EAECEF; font-size: 18px; font-weight: 700; margin-bottom: 10px;">Одно подключение вместо десятков</div>
      <div style="color: #969FA8; font-size: 15px; line-height: 1.6;">
        Вместо ручной правки JSON-конфигов в каждом отдельном AI-клиенте вы один раз подключаете адрес Bridge, а все остальные серверы добавляете и переключаете внутри одного окна.
      </div>
    </td>
  </tr>
  <tr style="height: 16px;">
    <td colspan="3" style="border: none; background: transparent; padding: 0;"></td>
  </tr>
  <tr>
    <td width="49%" valign="top" style="background: #141619; border: 1px solid #262A30; border-radius: 4px; padding: 22px 24px;">
      <div style="color: #EAECEF; font-size: 18px; font-weight: 700; margin-bottom: 10px;">Локальные STDIO-серверы в веб-приложениях ИИ</div>
      <div style="color: #969FA8; font-size: 15px; line-height: 1.6;">
        Большинство MCP-серверов работают только локально через консоль (<code>npx</code>, <code>uvx</code>, <code>python</code>), а веб-сервисы требуют внешний <code>HTTPS</code>. Bridge сам превращает любой локальный сервер в защищённую ссылку.
      </div>
    </td>
    <td width="2%" style="border: none; background: transparent; padding: 0;"></td>
    <td width="49%" valign="top" style="background: #141619; border: 1px solid #262A30; border-radius: 4px; padding: 22px 24px;">
      <div style="color: #EAECEF; font-size: 18px; font-weight: 700; margin-bottom: 10px;">Контроль памяти и всё в комплекте</div>
      <div style="color: #969FA8; font-size: 15px; line-height: 1.6;">
        Выключенные серверы не висят фоновыми процессами — Bridge полностью гасит их и освобождает ОЗУ. <code>Node.js</code>, <code>uv</code>, <code>Filesystem MCP</code> и <code>Tailscale</code> уже встроены в установщик.
      </div>
    </td>
  </tr>
</table>

<!-- ================= ОТДЕЛЬНЫЙ БЛОК ПОДДЕРЖКИ ================= -->

<table width="100%" style="display: table; width: 100%; table-layout: fixed; border-collapse: collapse; background: linear-gradient(90deg, #282115 0%, #1C1A17 100%); border: 1px solid #8C6A32; border-left: 5px solid #F5A623; border-radius: 4px; margin: 64px 0;">
  <tr>
    <td width="70%" valign="middle" align="left" style="padding: 18px 24px; border: none; color: #EAECEF; font-size: 15px; line-height: 1.45;">
      Проект развивается в свободное время. Если <b style="color: #F5A623;">MCP Bridge</b> упрощает вам работу — автора можно угостить кофе.
    </td>
    <td width="30%" valign="middle" align="right" style="padding: 18px 24px; border: none; line-height: 0;">
      <a href="https://pay.cloudtips.ru/p/cdb7341e" style="display: inline-block; line-height: 0; vertical-align: middle; text-decoration: none;"><img src="https://img.shields.io/badge/-На%20кофе%20разработчику-332915?style=for-the-badge&logo=data:image/svg%2bxml;base64,PHN2ZyB4bWxucz0iaHR0cDovL3d3dy53My5vcmcvMjAwMC9zdmciIHZpZXdCb3g9IjAgMCAyNCAyNCIgZmlsbD0ibm9uZSIgc3Ryb2tlPSIjRjVBNjIzIiBzdHJva2Utd2lkdGg9IjIiIHN0cm9rZS1saW5lY2FwPSJyb3VuZCIgc3Ryb2tlLWxpbmVqb2luPSJyb3VuZCI+PHBhdGggZD0iTTE3IDhoMWE0IDQgMCAxIDEgMCA4aC0xIi8+PHBhdGggZD0iTTMgOGgxNHY5YTQgNCAwIDAgMS00IDRIN2E0IDQgMCAwIDEtNC00WiIvPjxsaW5lIHgxPSI2IiB5MT0iMiIgeDI9IjYiIHkyPSI0Ii8+PGxpbmUgeDE9IjEwIiB5MT0iMiIgeDI9IjEwIiB5Mj0iNCIvPjxsaW5lIHgxPSIxNCIgeTE9IjIiIHgyPSIxNCIgeTI9IjQiLz48L3N2Zz4=&logoWidth=18" style="display: block; margin: 0; border: 1px solid #F5A623; border-radius: 3px;" alt="На кофе разработчику"></a>
    </td>
  </tr>
</table>

<!-- ===================================================================== -->
<!-- 02. ПЕРВОНАЧАЛЬНАЯ НАСТРОЙКА                                          -->
<!-- ===================================================================== -->

<a id="sec-settings"></a>
<table width="100%" style="display: table; width: 100%; table-layout: fixed; border-collapse: collapse; margin-top: 64px; margin-bottom: 22px;">
  <thead>
    <tr>
      <th align="left" style="background: linear-gradient(90deg, #1B3B38 0%, #162226 100%); border: 1px solid #2CE5D0; border-left: 6px solid #2CE5D0; border-radius: 4px; padding: 18px 24px; font-size: 21px; color: #FFFFFF; letter-spacing: 0.4px;">
        <span style="color: #2CE5D0;">02 ·</span> ПЕРВОНАЧАЛЬНАЯ НАСТРОЙКА И ПОДКЛЮЧЕНИЕ
      </th>
    </tr>
  </thead>
</table>

<p style="color: #B8C0C8; font-size: 15px; margin-bottom: 16px;">Откройте вкладку <b>Settings</b> — здесь настраивается доступ к папкам, внешний туннель и находятся готовые ссылки для подключения ИИ:</p>

<div align="center" style="margin-bottom: 18px;">
  <img src="docs/images/Settings.svg" width="100%" style="display: block; width: 100%; border: 1px solid #262A30; border-radius: 4px; box-sizing: border-box;" alt="Settings">
</div>

<table width="100%" style="display: table; width: 100%; table-layout: fixed; border-collapse: collapse; border: none; margin-bottom: 16px;">
  <tr>
    <td width="32%" valign="top" style="background: #141619; border: 1px solid #262A30; border-radius: 4px; padding: 20px 22px;">
      <div style="color: #EAECEF; font-size: 16px; font-weight: 700; margin-bottom: 10px;">1. Allowed Folders</div>
      <div style="color: #969FA8; font-size: 14.5px; line-height: 1.55;">
        Папки, к которым разрешён доступ для встроенного <b style="color: #D6DCE2;">Filesystem MCP</b>. Снимите галочку — доступ для ИИ закроется мгновенно без перезапуска шлюза.
      </div>
    </td>
    <td width="2%" style="border: none; background: transparent; padding: 0;"></td>
    <td width="32%" valign="top" style="background: #141619; border: 1px solid #262A30; border-radius: 4px; padding: 20px 22px;">
      <div style="color: #EAECEF; font-size: 16px; font-weight: 700; margin-bottom: 10px;">2. Online access</div>
      <div style="color: #969FA8; font-size: 14.5px; line-height: 1.55;">
        Включение публичного HTTPS-туннеля через <b style="color: #D6DCE2;">Tailscale Funnel</b>. Нужен только для веб-приложений ИИ. Для программ на вашем ПК включать его не нужно.
      </div>
    </td>
    <td width="2%" style="border: none; background: transparent; padding: 0;"></td>
    <td width="32%" valign="top" style="background: #141619; border: 1px solid #262A30; border-radius: 4px; padding: 20px 22px;">
      <div style="color: #EAECEF; font-size: 16px; font-weight: 700; margin-bottom: 10px;">3. Connection URLs</div>
      <div style="color: #969FA8; font-size: 14.5px; line-height: 1.55;">
        Готовые адреса шлюза. Нажмите <b style="color: #D6DCE2;">Copy URL</b> напротив нужного адреса и вставьте его в настройки своего AI-клиента.
      </div>
    </td>
  </tr>
</table>

<table width="100%" style="display: table; width: 100%; table-layout: fixed; border-collapse: collapse; border: none;">
  <tr>
    <td width="49%" valign="top" style="background: #141619; border: 1px solid #262A30; border-radius: 4px; padding: 20px 22px;">
      <div style="color: #EAECEF; font-size: 16px; font-weight: 700; margin-bottom: 8px;">LOCAL URL &nbsp;<code>http://127.0.0.1:8004/mcp</code></div>
      <div style="color: #969FA8; font-size: 14.5px; line-height: 1.55;">
        Для программ, установленных на вашем ПК (IDE, десктопные клиенты, локальные агенты). Укажите этот адрес как <code>Streamable HTTP</code> в настройках MCP вашей программы.
      </div>
    </td>
    <td width="2%" style="border: none; background: transparent; padding: 0;"></td>
    <td width="49%" valign="top" style="background: #141619; border: 1px solid #262A30; border-radius: 4px; padding: 20px 22px;">
      <div style="color: #EAECEF; font-size: 16px; font-weight: 700; margin-bottom: 8px;">PUBLIC URL &nbsp;<code>https://&lt;device&gt;.&lt;tailnet&gt;.ts.net/mcp</code></div>
      <div style="color: #969FA8; font-size: 14.5px; line-height: 1.55;">
        Для веб-приложений ИИ с поддержкой MCP. В разных сервисах этот раздел называется по-разному — <i>коннекторы, плагины, интеграции</i> или подключается через расширения: просто вставьте туда этот адрес.
      </div>
    </td>
  </tr>
</table>

<!-- ===================================================================== -->
<!-- 03. АВТОРИЗАЦИЯ TAILSCALE                                             -->
<!-- ===================================================================== -->

<a id="sec-tailscale"></a>
<table width="100%" style="display: table; width: 100%; table-layout: fixed; border-collapse: collapse; margin-top: 88px; margin-bottom: 22px;">
  <thead>
    <tr>
      <th align="left" style="background: linear-gradient(90deg, #1B3B38 0%, #162226 100%); border: 1px solid #2CE5D0; border-left: 6px solid #2CE5D0; border-radius: 4px; padding: 18px 24px; font-size: 21px; color: #FFFFFF; letter-spacing: 0.4px;">
        <span style="color: #2CE5D0;">03 ·</span> АВТОРИЗАЦИЯ TAILSCALE (ДЛЯ ВЕБ-ПРИЛОЖЕНИЙ)
      </th>
    </tr>
  </thead>
</table>

<p style="color: #B8C0C8; font-size: 15px; margin-bottom: 16px;">При первом нажатии кнопки <b>Connect</b> в блоке <i>Online access</i> нужно пройти три экрана по очереди:</p>

<table width="100%" style="display: table; width: 100%; table-layout: fixed; border-collapse: collapse; border: none; margin-bottom: 16px;">
  <tr>
    <td width="32%" valign="top" style="background: #141619; border: 1px solid #262A30; border-radius: 4px; padding: 18px;">
      <div style="color: #EAECEF; font-size: 15.5px; font-weight: 700; margin-bottom: 12px;">ШАГ 1 · Welcome to Tailscale</div>
      <img src="docs/images/Tailscale_Welcome.svg" width="100%" style="display: block; width: 100%; border-radius: 3px; margin-bottom: 14px;" alt="Tailscale Welcome">
      <div style="color: #969FA8; font-size: 14px; line-height: 1.55;">
        Откроется окно приложения Tailscale. Нажмите кнопку <b style="color: #D6DCE2;">Get Started</b>.
      </div>
    </td>
    <td width="2%" style="border: none; background: transparent; padding: 0;"></td>
    <td width="32%" valign="top" style="background: #141619; border: 1px solid #262A30; border-radius: 4px; padding: 18px;">
      <div style="color: #EAECEF; font-size: 15.5px; font-weight: 700; margin-bottom: 12px;">ШАГ 2 · Авторизация и Connect</div>
      <img src="docs/images/Tailscale_Device_Connect.svg" width="100%" style="display: block; width: 100%; border-radius: 3px; margin-bottom: 14px;" alt="Tailscale Device Connect">
      <div style="color: #969FA8; font-size: 14px; line-height: 1.55;">
        В браузере откроется форма входа — <b style="color: #D6DCE2;">авторизуйтесь</b>, после чего в окне <i>Connect device</i> нажмите <b style="color: #D6DCE2;">Connect</b>.
      </div>
    </td>
    <td width="2%" style="border: none; background: transparent; padding: 0;"></td>
    <td width="32%" valign="top" style="background: #141619; border: 1px solid #262A30; border-radius: 4px; padding: 18px;">
      <div style="color: #EAECEF; font-size: 15.5px; font-weight: 700; margin-bottom: 12px;">ШАГ 3 · Start using Funnel</div>
      <img src="docs/images/Tailscale_Funnel.svg" width="100%" style="display: block; width: 100%; border-radius: 3px; margin-bottom: 14px;" alt="Tailscale Funnel">
      <div style="color: #969FA8; font-size: 14px; line-height: 1.55;">
        Откроется страница <b style="color: #D6DCE2;">Start using Funnel</b>. Прокрутите вниз, нажмите <b style="color: #D6DCE2;">Enable Funnel</b> и дождитесь подключения.
      </div>
    </td>
  </tr>
</table>

<table width="100%" style="display: table; width: 100%; table-layout: fixed; border-collapse: collapse; background: #141619; border: 1px solid #262A30; border-left: 3px solid #F5A623; border-radius: 4px;">
  <tr>
    <td style="padding: 18px 22px; border: none; color: #969FA8; font-size: 14.5px; line-height: 1.55;">
      <div style="color: #F5A623; font-size: 15.5px; font-weight: 700; margin-bottom: 6px;">Если на Шаге 2 окно Connect device не открылось с первого раза:</div>
      Вам нужно пройти именно эти три окна по порядку. Если после входа в аккаунт вы видите что-то другое или страница не открылась, вернитесь в окно Tailscale и <b style="color: #D6DCE2;">нажмите Get Started ещё раз</b>.<br><br>
      Когда кнопка <i>Get Started</i> в окне Tailscale сменится на три кнопки (информация об устройстве и кнопка <b style="color: #D6DCE2;">Close</b>), закройте это окно кнопкой <b style="color: #D6DCE2;">Close</b> и дождитесь открытия страницы <b style="color: #D6DCE2;">Start using Funnel</b>.
    </td>
  </tr>
</table>

<!-- ===================================================================== -->
<!-- 04. ГЛАВНЫЙ ЭКРАН (DASHBOARD)                                         -->
<!-- ===================================================================== -->

<a id="sec-dashboard"></a>
<table width="100%" style="display: table; width: 100%; table-layout: fixed; border-collapse: collapse; margin-top: 88px; margin-bottom: 22px;">
  <thead>
    <tr>
      <th align="left" style="background: linear-gradient(90deg, #1B3B38 0%, #162226 100%); border: 1px solid #2CE5D0; border-left: 6px solid #2CE5D0; border-radius: 4px; padding: 18px 24px; font-size: 21px; color: #FFFFFF; letter-spacing: 0.4px;">
        <span style="color: #2CE5D0;">04 ·</span> ГЛАВНЫЙ ЭКРАН (DASHBOARD)
      </th>
    </tr>
  </thead>
</table>

<p style="color: #B8C0C8; font-size: 15px; margin-bottom: 16px;">На вкладке <b>Dashboard</b> отображается сводка по шлюзу и список включённых серверов. Выключенные серверы (<code>DISABLED</code>) в списке <i>MCP Services Status</i> скрыты — они находятся во вкладке <i>MCP Services</i>.</p>

<div align="center" style="margin-bottom: 18px;">
  <img src="docs/images/Main.svg" width="100%" style="display: block; width: 100%; border: 1px solid #262A30; border-radius: 4px; box-sizing: border-box;" alt="Dashboard">
</div>

<table width="100%" style="display: table; width: 100%; table-layout: fixed; border-collapse: collapse; border: none;">
  <tr>
    <td width="23.5%" valign="top" style="background: #141619; border: 1px solid #262A30; border-radius: 4px; padding: 16px 18px;">
      <div style="color: #2CE5D0; font-size: 14px; font-weight: 700; margin-bottom: 8px;">ACTIVE</div>
      <div style="color: #969FA8; font-size: 13.5px; line-height: 1.5;">Сервер подключён, проверен и передаёт свои инструменты в диалог.</div>
    </td>
    <td width="2%" style="border: none; background: transparent; padding: 0;"></td>
    <td width="23.5%" valign="top" style="background: #141619; border: 1px solid #262A30; border-radius: 4px; padding: 16px 18px;">
      <div style="color: #F5A623; font-size: 14px; font-weight: 700; margin-bottom: 8px;">WAITING</div>
      <div style="color: #969FA8; font-size: 13.5px; line-height: 1.5;">Ждёт запуска внешней программы (например, Unreal Editor на порту <code>:8000</code>).</div>
    </td>
    <td width="2%" style="border: none; background: transparent; padding: 0;"></td>
    <td width="23.5%" valign="top" style="background: #141619; border: 1px solid #262A30; border-radius: 4px; padding: 16px 18px;">
      <div style="color: #FF6B78; font-size: 14px; font-weight: 700; margin-bottom: 8px;">QUARANTINE</div>
      <div style="color: #969FA8; font-size: 13.5px; line-height: 1.5;">Ошибка запуска или протокола MCP. Сервер изолирован и не мешает остальным.</div>
    </td>
    <td width="2%" style="border: none; background: transparent; padding: 0;"></td>
    <td width="23.5%" valign="top" style="background: #141619; border: 1px solid #262A30; border-radius: 4px; padding: 16px 18px;">
      <div style="color: #7F8790; font-size: 14px; font-weight: 700; margin-bottom: 8px;">DISABLED</div>
      <div style="color: #7F8790; font-size: 13.5px; line-height: 1.5;">Сервер выключен вручную (отображается только во вкладке <i>MCP Services</i>).</div>
    </td>
  </tr>
</table>

<!-- ===================================================================== -->
<!-- 05. УПРАВЛЕНИЕ СЕРВЕРАМИ И ПРЕСЕТАМИ                                  -->
<!-- ===================================================================== -->

<a id="sec-servers"></a>
<table width="100%" style="display: table; width: 100%; table-layout: fixed; border-collapse: collapse; margin-top: 88px; margin-bottom: 22px;">
  <thead>
    <tr>
      <th align="left" style="background: linear-gradient(90deg, #1B3B38 0%, #162226 100%); border: 1px solid #2CE5D0; border-left: 6px solid #2CE5D0; border-radius: 4px; padding: 18px 24px; font-size: 21px; color: #FFFFFF; letter-spacing: 0.4px;">
        <span style="color: #2CE5D0;">05 ·</span> УПРАВЛЕНИЕ MCP-СЕРВЕРАМИ И ПРЕСЕТАМИ
      </th>
    </tr>
  </thead>
</table>

<div align="center" style="margin-bottom: 18px;">
  <img src="docs/images/MSP_Servises.svg" width="100%" style="display: block; width: 100%; border: 1px solid #262A30; border-radius: 4px; box-sizing: border-box;" alt="MCP Services">
</div>

<table width="100%" style="display: table; width: 100%; table-layout: fixed; border-collapse: collapse; background: #141619; border: 1px solid #262A30; border-radius: 4px; margin-bottom: 16px;">
  <tr>
    <td style="padding: 20px 24px; border: none; color: #969FA8; font-size: 15px; line-height: 1.6;">
      <div style="color: #EAECEF; font-size: 17px; font-weight: 700; margin-bottom: 8px;">Как работает переключение серверов в реальном времени</div>
      Вы можете включать и выключать любые серверы прямо во время активного разговора с ИИ. В каждый момент времени в диалоге доступны <b style="color: #D6DCE2;">только инструменты тех серверов, которые включены прямо сейчас</b>.<br><br>
      При этом <b style="color: #D6DCE2;">не нужно перезагружать страницу в браузере</b>, перезапускать локальный клиент или вручную обновлять список инструментов в настройках ИИ — шлюз передаёт изменения автоматически.
    </td>
  </tr>
</table>

<table width="100%" style="display: table; width: 100%; table-layout: fixed; border-collapse: collapse; border: none;">
  <tr>
    <td width="32%" valign="top" style="background: #141619; border: 1px solid #262A30; border-radius: 4px; padding: 20px 22px;">
      <div style="color: #EAECEF; font-size: 16px; font-weight: 700; margin-bottom: 10px;">Server Presets (Пресеты)</div>
      <div style="color: #969FA8; font-size: 14.5px; line-height: 1.55;">
        Отметьте нужную комбинацию серверов для конкретной задачи и нажмите <b style="color: #D6DCE2;">+ Save Preset</b>. Переключайте готовые профили в 1 клик прямо во время работы.
      </div>
    </td>
    <td width="2%" style="border: none; background: transparent; padding: 0;"></td>
    <td width="32%" valign="top" style="background: #141619; border: 1px solid #262A30; border-radius: 4px; padding: 20px 22px;">
      <div style="color: #EAECEF; font-size: 16px; font-weight: 700; margin-bottom: 10px;">Включение (ON) и Tools</div>
      <div style="color: #969FA8; font-size: 14.5px; line-height: 1.55;">
        Галочка <b style="color: #D6DCE2;">ON</b> мгновенно подключает сервер к диалогу или полностью гасит его процесс в памяти, а плашка <code>Tools: N</code> показывает число доступных инструментов.
      </div>
    </td>
    <td width="2%" style="border: none; background: transparent; padding: 0;"></td>
    <td width="32%" valign="top" style="background: #141619; border: 1px solid #262A30; border-radius: 4px; padding: 20px 22px;">
      <div style="color: #EAECEF; font-size: 16px; font-weight: 700; margin-bottom: 10px;">Редактирование и удаление</div>
      <div style="color: #969FA8; font-size: 14.5px; line-height: 1.55;">
        Кнопка <b style="color: #D6DCE2;">✎</b> открывает JSON-конфигурацию выбранного сервера для правки параметров, а <b style="color: #D6DCE2;">✕</b> удаляет сервер из списка.
      </div>
    </td>
  </tr>
</table>

<!-- ===================================================================== -->
<!-- 06. ДОБАВЛЕНИЕ MCP-СЕРВЕРА                                            -->
<!-- ===================================================================== -->

<a id="sec-add"></a>
<table width="100%" style="display: table; width: 100%; table-layout: fixed; border-collapse: collapse; margin-top: 88px; margin-bottom: 22px;">
  <thead>
    <tr>
      <th align="left" style="background: linear-gradient(90deg, #1B3B38 0%, #162226 100%); border: 1px solid #2CE5D0; border-left: 6px solid #2CE5D0; border-radius: 4px; padding: 18px 24px; font-size: 21px; color: #FFFFFF; letter-spacing: 0.4px;">
        <span style="color: #2CE5D0;">06 ·</span> ДОБАВЛЕНИЕ MCP-СЕРВЕРА
      </th>
    </tr>
  </thead>
</table>

<p style="color: #B8C0C8; font-size: 15px; margin-bottom: 16px;">Нажмите <b>+ Add Server</b> во вкладке <i>MCP Services</i>, чтобы подключить новый локальный или удалённый сервер:</p>

<div align="center" style="margin-bottom: 18px;">
  <img src="docs/images/Add_MCP.svg" width="100%" style="display: block; width: 100%; border: 1px solid #262A30; border-radius: 4px; box-sizing: border-box;" alt="Add MCP Server">
</div>

<table width="100%" style="display: table; width: 100%; table-layout: fixed; border-collapse: collapse; border: none;">
  <tr>
    <td width="32%" valign="top" style="background: #141619; border: 1px solid #262A30; border-radius: 4px; padding: 20px 22px;">
      <div style="color: #EAECEF; font-size: 16px; font-weight: 700; margin-bottom: 10px;">1. Вставка JSON</div>
      <div style="color: #969FA8; font-size: 14.5px; line-height: 1.55;">
        Скопируйте готовый блок <code>mcpServers</code> из документации нужного сервера и нажмите <b style="color: #D6DCE2;">Вставить из буфера</b> (или отредактируйте JSON вручную).
      </div>
    </td>
    <td width="2%" style="border: none; background: transparent; padding: 0;"></td>
    <td width="32%" valign="top" style="background: #141619; border: 1px solid #262A30; border-radius: 4px; padding: 20px 22px;">
      <div style="color: #EAECEF; font-size: 16px; font-weight: 700; margin-bottom: 10px;">2. Проверить и добавить</div>
      <div style="color: #969FA8; font-size: 14.5px; line-height: 1.55;">
        Нажмите <b style="color: #D6DCE2;">Проверить и добавить</b> — Bridge протестирует запуск, проверит список инструментов и сразу подключит сервер к шлюзу.
      </div>
    </td>
    <td width="2%" style="border: none; background: transparent; padding: 0;"></td>
    <td width="32%" valign="top" style="background: #141619; border: 1px solid #262A30; border-radius: 4px; padding: 20px 22px;">
      <div style="color: #EAECEF; font-size: 16px; font-weight: 700; margin-bottom: 10px;">3. Сохранить без проверки</div>
      <div style="color: #969FA8; font-size: 14.5px; line-height: 1.55;">
        Если сервер зависит от внешней программы, которая сейчас закрыта, нажмите <b style="color: #D6DCE2;">Сохранить без проверки</b> — он добавится в режиме <code>WAITING</code>.
      </div>
    </td>
  </tr>
</table>

<!-- ===================================================================== -->
<!-- 07. БЕЗОПАСНОСТЬ И ОТВЕТСТВЕННОСТЬ ПОЛЬЗОВАТЕЛЯ                       -->
<!-- ===================================================================== -->

<a id="sec-security"></a>
<table width="100%" style="display: table; width: 100%; table-layout: fixed; border-collapse: collapse; margin-top: 88px; margin-bottom: 22px;">
  <thead>
    <tr>
      <th align="left" style="background: linear-gradient(90deg, #332915 0%, #1F1B16 100%); border: 1px solid #F5A623; border-left: 6px solid #F5A623; border-radius: 4px; padding: 18px 24px; font-size: 21px; color: #FFFFFF; letter-spacing: 0.4px;">
        <span style="color: #F5A623;">07 ·</span> БЕЗОПАСНОСТЬ И ОТВЕТСТВЕННОСТЬ ПОЛЬЗОВАТЕЛЯ
      </th>
    </tr>
  </thead>
</table>

<table width="100%" style="display: table; width: 100%; table-layout: fixed; border-collapse: collapse; background: #262015; border: 1px solid #8C6A32; border-left: 5px solid #F5A623; border-radius: 4px; margin-bottom: 18px;">
  <tr>
    <td style="padding: 20px 24px; border: none; color: #EAECEF; font-size: 15px; line-height: 1.6;">
      <div style="color: #F5A623; font-size: 17px; font-weight: 700; margin-bottom: 8px;">Отказ от ответственности (Использование на свой страх и риск)</div>
      Приложение <b>MCP Bridge</b> предоставляется по принципу «как есть» (<i>AS IS</i>). Разработчик не несёт никакой ответственности за потерю или повреждение данных, удаление файлов, утечку токенов, сбои в работе системы, а также за любые действия подключённых нейросетей и сторонних MCP-серверов.<br><br>
      <b>Вся ответственность за выбор подключаемых серверов, открытые папки и безопасность публичных ссылок полностью лежит на самом пользователе.</b>
    </td>
  </tr>
</table>

<table width="100%" style="display: table; width: 100%; table-layout: fixed; border-collapse: collapse; border: none;">
  <tr>
    <td width="49%" valign="top" style="background: #141619; border: 1px solid #8C6A32; border-radius: 4px; padding: 20px 22px;">
      <div style="color: #F5A623; font-size: 16px; font-weight: 700; margin-bottom: 10px;">1. Полный доступ к разрешённым папкам</div>
      <div style="color: #969FA8; font-size: 14.5px; line-height: 1.55;">
        Встроенный <code>Filesystem MCP</code> даёт ИИ право читать, изменять и удалять любые файлы внутри отмеченных папок в <b>Allowed Folders</b>. Никогда не открывайте доступ к системным дискам целиком (<code>C:\</code>), папкам с паролями, криптокошельками или SSH-ключами.
      </div>
    </td>
    <td width="2%" style="border: none; background: transparent; padding: 0;"></td>
    <td width="49%" valign="top" style="background: #141619; border: 1px solid #8C6A32; border-radius: 4px; padding: 20px 22px;">
      <div style="color: #F5A623; font-size: 16px; font-weight: 700; margin-bottom: 10px;">2. Запуск сторонних STDIO-процессов</div>
      <div style="color: #969FA8; font-size: 14.5px; line-height: 1.55;">
        Любой добавленный вами консольный сервер (<code>npx</code>, <code>uvx</code>, <code>python</code>, <code>node</code>, <code>.exe</code>) выполняется на вашем ПК с правами вашей учётной записи Windows. Добавляйте только те серверы и пакеты, источнику которых вы полностью доверяете.
      </div>
    </td>
  </tr>
  <tr style="height: 16px;">
    <td colspan="3" style="border: none; background: transparent; padding: 0;"></td>
  </tr>
  <tr>
    <td width="49%" valign="top" style="background: #141619; border: 1px solid #8C6A32; border-radius: 4px; padding: 20px 22px;">
      <div style="color: #F5A623; font-size: 16px; font-weight: 700; margin-bottom: 10px;">3. Публичная ссылка Tailscale Funnel</div>
      <div style="color: #969FA8; font-size: 14.5px; line-height: 1.55;">
        При включённом туннеле ваш <code>PUBLIC URL</code> доступен из интернета любому, кто узнает этот адрес. Не публикуйте эту ссылку в открытом доступе, не светите её на скриншотах и отключайте туннель кнопкой <b>Disconnect</b>, когда он вам не нужен.
      </div>
    </td>
    <td width="2%" style="border: none; background: transparent; padding: 0;"></td>
    <td width="49%" valign="top" style="background: #141619; border: 1px solid #8C6A32; border-radius: 4px; padding: 20px 22px;">
      <div style="color: #F5A623; font-size: 16px; font-weight: 700; margin-bottom: 10px;">4. Хранение ключей и токенов</div>
      <div style="color: #969FA8; font-size: 14.5px; line-height: 1.55;">
        Указанные вами переменные окружения (<code>env</code>) и HTTP-заголовки авторизации (<code>Authorization</code> / API-ключи) сохраняются локально в открытом виде в файле <code>Config/settings.json</code>. Не передавайте этот файл посторонним.
      </div>
    </td>
  </tr>
</table>

<hr style="border: none; border-top: 1px solid #262A30; margin: 64px 0 20px 0;">

<div align="center">
  <sub>
    <b>Системные требования:</b> Windows 10 x64 (1903+) / Windows 11 x64 · .NET Framework 4.8<br>
    <a href="docs/ARCHITECTURE.md">Архитектура</a> · <a href="SECURITY.md">Безопасность</a> · <a href="THIRD_PARTY_NOTICES.md">Сторонние компоненты</a> · <a href="CONTRIBUTING.md">Участие в разработке</a>
  </sub>
</div>

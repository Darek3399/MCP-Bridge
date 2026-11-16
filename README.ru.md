<div style="max-width: 1000px; margin: 0 auto;">

<div align="right" style="display: flex; justify-content: space-between; align-items: center; padding-top: 8px;">
  <span><a id="top"></a><a href="docs/donate.md" title="Поддержать проект"><img align="left" width="24" height="24" style="float: none; display: block;" src="docs/images/coffee-button.svg" alt="Поддержать проект"></a></span>
  <span><a href="README.md"><img style="display: inline-block; vertical-align: middle;" src="https://img.shields.io/badge/-English-22252A?style=for-the-badge" height="24" vspace="7" alt="English"></a>&nbsp;<a href="README.ru.md"><img style="display: inline-block; vertical-align: middle;" src="https://img.shields.io/badge/-Русский-15322F?style=for-the-badge" height="24" vspace="7" alt="Русский"></a></span>
</div>

<div align="center">
  <br clear="all">
  <img style="display: block; margin: 18px auto -16px;" src="src/McpHotplug/Resources/MCPHotplug.ico" width="72" height="72" alt="MCP Hotplug">
  <h1 style="margin-top: 0;">mcp<b>Hotplug</b></h1>

  <h3>Один адрес для всех MCP-серверов. Переключайте инструменты, не перезапуская чат.</h3>

  <p>
    MCP Hotplug объединяет локальные и удалённые MCP-серверы в одном приложении для Windows.<br>
    Подключите AI-клиент один раз, а затем включайте только нужные серверы.
  </p>

  <p>
    <a href="LICENSE"><img src="https://img.shields.io/badge/License-MIT-2ea44f?logo=opensourceinitiative&logoColor=white" alt="MIT License"></a>
    <a href="https://mcp.so/server/MCP-Hotplug/Darek3399"><img src="https://img.shields.io/badge/MCP.so-MCP_Hotplug-f97316?logo=modelcontextprotocol&logoColor=white" alt="MCP Hotplug on MCP.so"></a>
    <a href="https://glama.ai/mcp/servers/Darek3399/MCP-Hotplug"><img src="https://glama.ai/mcp/servers/Darek3399/MCP-Hotplug/badges/score.svg?v=2" alt="MCP Hotplug score on Glama"></a>
  </p>


  <p><sub>Windows 10/11 · Для локальных десктопных клиентов публичный туннель не нужен</sub></p>
</div>

<p align="center">
  <img src="docs/images/Main_RU.svg" width="900" alt="Главный экран MCP Hotplug">
</p>

## Что решает MCP Hotplug

- **Один адрес подключения:** объедините несколько локальных и удалённых MCP-серверов.
- **Переключение на лету:** включайте и выключайте серверы во время работы.
- **Пресеты для задач:** сохраняйте наборы серверов и быстро переключайтесь между ними.
- **Локальные серверы в веб-ИИ:** при необходимости подключайте локальные STDIO-серверы через Tailscale Funnel.

> **Для локальных десктопных клиентов туннель не нужен.** Tailscale Funnel — дополнительная настройка для совместимых веб-клиентов, которым нужен удалённый доступ.

## Быстрый старт

1. Скачайте установщик со [страницы последнего релиза](https://github.com/Darek3399/MCP-Hotplug/releases/latest).
2. Установите и запустите MCP Hotplug.
3. Откройте вкладку **MCP Services** и добавьте MCP-сервер. Можно вставить конфигурацию из документации сервера.
4. Нажмите **Verify & Add**. Если сервер зависит от закрытого приложения, выберите **Save Without Check**.
5. В настройках MCP-клиента добавьте подключение **Streamable HTTP** и укажите локальный адрес:

   `http://127.0.0.1:8004/mcp`

Если ваш клиент использует JSON-файл конфигурации, добавьте:

```json
{
  "mcpServers": {
    "mcp-hotplug": {
      "type": "http",
      "url": "http://127.0.0.1:8004/mcp"
    }
  }
}
```

<p>
  <img src="docs/images/Settings_RU.svg" width="900" alt="Настройки MCP Hotplug и адреса подключения">
</p>

## Удалённый доступ через Tailscale Funnel

При первоначальной настройке авторизуйте устройство в Tailscale и включите Funnel. После этого используется стабильное имя устройства и адрес Funnel: обычно не нужно снова входить в аккаунт или получать новый URL при каждом запуске Hotplug. Адрес доступен, пока устройство авторизовано и работает, а Funnel включён.

В отличие от временных туннелей, этот адрес не нужно получать заново при каждом запуске. Условия других сервисов туннелирования отличаются; постоянный адрес у некоторых из них может быть доступен только на платном тарифе.

<table>
  <tr>
    <td width="33%" align="center" valign="top">
      <img src="docs/images/Tailscale_Welcome.png" width="100%" alt="Шаг 1: запуск Tailscale">
      <br><b>1. Запустите Tailscale</b>
    </td>
    <td width="33%" align="center" valign="top">
      <img src="docs/images/Tailscale_Device_Connect.png" width="100%" alt="Шаг 2: вход и подключение устройства">
      <br><b>2. Авторизуйте устройство</b>
    </td>
    <td width="33%" align="center" valign="top">
      <img src="docs/images/Tailscale_Funnel.png" width="100%" alt="Шаг 3: включение Tailscale Funnel">
      <br><b>3. Включите Funnel</b>
    </td>
  </tr>
</table>

> **Безопасность:** публичный URL может быть доступен из интернета. Не публикуйте его и включайте Funnel только тогда, когда нужен удалённый доступ.

## Возможности

### Переключайте серверы прямо во время чата

Включайте и выключайте MCP-серверы во время активного диалога, не переподключая клиент вручную.

<p>
  <img src="docs/images/MSP_Servises_RU.svg" width="900" alt="Управление MCP-серверами">
</p>

### Используйте пресеты для разных задач

Сохраните комбинацию серверов как пресет и переключайтесь между наборами для разных сценариев — например, разработки или работы с файлами.

### Подключайте локальные STDIO-серверы

Hotplug объединяет локальные консольные MCP-серверы, запускаемые через `npx`, `uvx`, `python` и другие инструменты, с подключением к общему шлюзу.

### Ограничивайте доступ к папкам

Для встроенного Filesystem MCP выберите папки, доступные серверу. Выдавайте доступ только к тем каталогам, которые действительно нужны для работы.

## Как это устроено

<p>
  <img src="docs/images/architecture-ru.svg" width="900" alt="Архитектура MCP Hotplug">
</p>

1. AI-клиент подключается к endpoint MCP Hotplug.
2. Hotplug направляет запросы к включённым MCP-серверам.
3. Локальные серверы могут использовать STDIO, удалённые — HTTP.
4. Удалённый доступ для веб-клиента при необходимости включается отдельно через Tailscale Funnel.

## Инструменты

Hotplug предоставляет два meta-tools — `all_tools` и `call_tool` — для обнаружения и вызова инструментов подключённых серверов. Это позволяет не передавать клиенту описания всех инструментов сразу. Фактический эффект зависит от клиента и модели.

- **`all_tools`** — возвращает актуальный кэшированный каталог инструментов включённых MCP-серверов, включая `tool_id`, `inputSchema`, статус сервера и `catalog_revision`.
- **`call_tool`** — проверяет аргументы по кэшированной `inputSchema` и направляет вызов целевому серверу по параметрам `tool_id`, `catalog_revision` и `arguments`.

## Безопасность и ограничения

- **Сторонние MCP-серверы** запускаются на вашем компьютере от имени вашего пользователя. Добавляйте только те команды и серверы, которым доверяете.
- **Filesystem MCP** может читать, изменять и удалять файлы в разрешённых папках. Не выдавайте доступ ко всему диску без необходимости.
- **Tailscale Funnel** открывает публичный URL. Не размещайте его в открытом доступе и выключайте Funnel, когда он не нужен.
- **Секреты серверов** — переменные окружения и заголовки авторизации — хранятся локально. Согласно текущей документации проекта, они сохраняются в открытом виде; учитывайте это при работе с ключами и токенами.

Подробнее: [SECURITY.md](SECURITY.md).

## Системные требования

- Windows 10 x64 (1903+) или Windows 11.
- .NET Framework 4.8.
- Установщик для Windows x64 доступен на [странице релизов](https://github.com/Darek3399/MCP-Hotplug/releases/latest).

## Технические сведения

- **Платформа:** Windows.
- **Язык интерфейса:** русский и английский.
- **MCP-транспорты:** STDIO и HTTP.
- **Локальный endpoint:** `http://127.0.0.1:8004/mcp`.
- **Meta-tools:** `all_tools` и `call_tool`.
- **Опциональный внешний доступ:** Tailscale Funnel.
- **Управление дочерними процессами:** Windows Job Objects.

## Участие в проекте

Нашли ошибку или хотите предложить улучшение?

- [Создать issue](https://github.com/Darek3399/MCP-Hotplug/issues).
- [Посмотреть исходный код](https://github.com/Darek3399/MCP-Hotplug).

## Лицензия

Проект распространяется под лицензией [MIT](LICENSE).

## Поддержать проект

Если MCP Hotplug оказался вам полезен, можно поддержать разработку:

<p>
  <a href="docs/donate.md">
    <img src="docs/images/coffee-ru.svg" width="500" alt="Поддержать разработку MCP Hotplug">
  </a>
</p>

---

<p align="center">
  <a href="README.md">English</a> ·
  <a href="SECURITY.md">Безопасность</a> ·
  <a href="THIRD_PARTY_NOTICES.md">Сторонние компоненты</a> ·
  <a href="docs/donate.md">Поддержать проект</a>
</p>

</div>

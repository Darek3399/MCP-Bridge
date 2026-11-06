<a id="top"></a>

<!-- Floating Back-to-Top button in the bottom-right corner -->
<div align="right" style="position: fixed; bottom: 24px; right: 28px; z-index: 999;">
  <a href="#top"><img src="https://img.shields.io/badge/-↑%20Top-181A1D?style=for-the-badge" alt="Top"></a>
</div>

<!-- Header: centered logo and language switcher on the same level -->
<table width="100%" style="display: table; width: 100%; table-layout: fixed; border-collapse: collapse; border: none; margin-top: 8px; margin-bottom: 12px;">
  <tr>
    <td width="25%" valign="top" style="border: none; background: transparent; padding: 0;"></td>
    <td width="50%" valign="top" align="center" style="border: none; background: transparent; padding: 0;">
      <img src="src/McpBridge/Resources/Svg/bridge-mark.svg" width="80" height="80" alt="MCP Bridge">
      <h1 style="margin: 10px 0 6px 0; font-size: 36px; border: none; padding: 0;">mcp<b>Bridge</b></h1>
    </td>
    <td width="25%" valign="top" align="right" style="border: none; background: transparent; padding: 4px 0 0 0; white-space: nowrap;">
      <a href="README.md"><img src="https://img.shields.io/badge/-Русский-22252A?style=for-the-badge" alt="Русский"></a>&nbsp;&nbsp;<a href="README.en.md"><img src="https://img.shields.io/badge/-English-15322F?style=for-the-badge" alt="English"></a>
    </td>
  </tr>
</table>

<div align="center" style="margin-bottom: 36px;">
  <p style="color: #B8C0C8; font-size: 16px; line-height: 1.55; max-width: 640px; margin: 0 auto 22px auto;">
    <b>Unified Local Hub & MCP Server Manager for Windows</b><br>
    Connect Bridge to your AI client once and control all your servers and tools from a single window.
  </p>
  <p style="margin-bottom: 26px;">
    <a href="../../releases/latest"><img src="https://img.shields.io/badge/⬇-Download%20Installer%20(Windows%20x64)-15322F?style=for-the-badge&labelColor=181A1D" alt="Download Installer"></a>
  </p>
  <p>
    <a href="#sec-why"><img src="https://img.shields.io/badge/01-Why%20MCP%20Bridge-22252A?style=for-the-badge&labelColor=15322F" alt="01 Why MCP Bridge"></a>&nbsp;
    <a href="#sec-settings"><img src="https://img.shields.io/badge/02-Setup%20%26%20URLs-22252A?style=for-the-badge&labelColor=15322F" alt="02 Setup & URLs"></a>&nbsp;
    <a href="#sec-tailscale"><img src="https://img.shields.io/badge/03-Tailscale-22252A?style=for-the-badge&labelColor=15322F" alt="03 Tailscale"></a>&nbsp;
    <a href="#sec-dashboard"><img src="https://img.shields.io/badge/04-Dashboard-22252A?style=for-the-badge&labelColor=15322F" alt="04 Dashboard"></a>&nbsp;
    <a href="#sec-servers"><img src="https://img.shields.io/badge/05-Servers%20%26%20Presets-22252A?style=for-the-badge&labelColor=15322F" alt="05 Servers & Presets"></a>&nbsp;
    <a href="#sec-add"><img src="https://img.shields.io/badge/06-Adding%20MCP-22252A?style=for-the-badge&labelColor=15322F" alt="06 Adding MCP"></a>&nbsp;
    <a href="#sec-security"><img src="https://img.shields.io/badge/07-Security-22252A?style=for-the-badge&labelColor=332915" alt="07 Security"></a>
  </p>
</div>

<!-- ===================================================================== -->
<!-- 01. WHY MCP BRIDGE                                                    -->
<!-- ===================================================================== -->

<a id="sec-why"></a>
<table width="100%" style="display: table; width: 100%; table-layout: fixed; border-collapse: collapse; margin-top: 48px; margin-bottom: 22px;">
  <thead>
    <tr>
      <th align="left" style="background: linear-gradient(90deg, #1B3B38 0%, #162226 100%); border: 1px solid #2CE5D0; border-left: 6px solid #2CE5D0; border-radius: 4px; padding: 18px 24px; font-size: 21px; color: #FFFFFF; letter-spacing: 0.4px;">
        <span style="color: #2CE5D0;">01 ·</span> WHY MCP BRIDGE
      </th>
    </tr>
  </thead>
</table>

<table width="100%" style="display: table; width: 100%; table-layout: fixed; border-collapse: collapse; border: none; margin-bottom: 0;">
  <tr>
    <td width="49%" valign="top" style="background: #141619; border: 1px solid #262A30; border-radius: 4px; padding: 22px 24px;">
      <div style="color: #EAECEF; font-size: 18px; font-weight: 700; margin-bottom: 10px;">Keep AI Context Clean</div>
      <div style="color: #969FA8; font-size: 15px; line-height: 1.6;">
        Connecting a dozen servers (50–100 tools) at once confuses the model and wastes tokens. With Bridge, you enable only the servers you need <b style="color: #D6DCE2;">right in the middle of a conversation</b> — without restarting the chat.
      </div>
    </td>
    <td width="2%" style="border: none; background: transparent; padding: 0;"></td>
    <td width="49%" valign="top" style="background: #141619; border: 1px solid #262A30; border-radius: 4px; padding: 22px 24px;">
      <div style="color: #EAECEF; font-size: 18px; font-weight: 700; margin-bottom: 10px;">One Connection Instead of Dozens</div>
      <div style="color: #969FA8; font-size: 15px; line-height: 1.6;">
        Instead of manually editing JSON configs in every separate AI client, you connect the Bridge endpoint once and add, configure, or toggle all your servers inside a single window.
      </div>
    </td>
  </tr>
  <tr style="height: 16px;">
    <td colspan="3" style="border: none; background: transparent; padding: 0;"></td>
  </tr>
  <tr>
    <td width="49%" valign="top" style="background: #141619; border: 1px solid #262A30; border-radius: 4px; padding: 22px 24px;">
      <div style="color: #EAECEF; font-size: 18px; font-weight: 700; margin-bottom: 10px;">Local STDIO Servers in Web AI Apps</div>
      <div style="color: #969FA8; font-size: 15px; line-height: 1.6;">
        Most MCP servers only run locally via CLI (<code>npx</code>, <code>uvx</code>, <code>python</code>), while web AI services require a public <code>HTTPS</code> URL. Bridge turns any local server into a secure HTTPS link in one click.
      </div>
    </td>
    <td width="2%" style="border: none; background: transparent; padding: 0;"></td>
    <td width="49%" valign="top" style="background: #141619; border: 1px solid #262A30; border-radius: 4px; padding: 22px 24px;">
      <div style="color: #EAECEF; font-size: 18px; font-weight: 700; margin-bottom: 10px;">RAM Control & Everything Included</div>
      <div style="color: #969FA8; font-size: 15px; line-height: 1.6;">
        Disabled servers never linger as background processes — Bridge completely terminates them to free RAM. <code>Node.js</code>, <code>uv</code>, <code>Filesystem MCP</code>, and <code>Tailscale</code> are already bundled in the installer.
      </div>
    </td>
  </tr>
</table>

<!-- ================= STANDALONE SUPPORT BLOCK ================= -->

<table width="100%" style="display: table; width: 100%; table-layout: fixed; border-collapse: collapse; background: linear-gradient(90deg, #282115 0%, #1C1A17 100%); border: 1px solid #8C6A32; border-left: 5px solid #F5A623; border-radius: 4px; margin: 64px 0;">
  <tr>
    <td width="70%" valign="middle" align="left" style="padding: 18px 24px; border: none; color: #EAECEF; font-size: 15px; line-height: 1.45;">
      This project is developed in spare time. If <b style="color: #F5A623;">MCP Bridge</b> makes your workflow easier — you can buy the author a coffee.
    </td>
    <td width="30%" valign="middle" align="right" style="padding: 18px 24px; border: none; line-height: 0;">
      <a href="https://pay.cloudtips.ru/p/cdb7341e" style="display: inline-block; line-height: 0; vertical-align: middle; text-decoration: none;"><img src="https://img.shields.io/badge/-Buy%20the%20developer%20a%20coffee-332915?style=for-the-badge&logo=data:image/svg%2bxml;base64,PHN2ZyB4bWxucz0iaHR0cDovL3d3dy53My5vcmcvMjAwMC9zdmciIHZpZXdCb3g9IjAgMCAyNCAyNCIgZmlsbD0ibm9uZSIgc3Ryb2tlPSIjRjVBNjIzIiBzdHJva2Utd2lkdGg9IjIiIHN0cm9rZS1saW5lY2FwPSJyb3VuZCIgc3Ryb2tlLWxpbmVqb2luPSJyb3VuZCI+PHBhdGggZD0iTTE3IDhoMWE0IDQgMCAxIDEgMCA4aC0xIi8+PHBhdGggZD0iTTMgOGgxNHY5YTQgNCAwIDAgMS00IDRIN2E0IDQgMCAwIDEtNC00WiIvPjxsaW5lIHgxPSI2IiB5MT0iMiIgeDI9IjYiIHkyPSI0Ii8+PGxpbmUgeDE9IjEwIiB5MT0iMiIgeDI9IjEwIiB5Mj0iNCIvPjxsaW5lIHgxPSIxNCIgeTE9IjIiIHgyPSIxNCIgeTI9IjQiLz48L3N2Zz4=&logoWidth=18" style="display: block; margin: 0; border: 1px solid #F5A623; border-radius: 3px;" alt="Buy the developer a coffee"></a>
    </td>
  </tr>
</table>

<!-- ===================================================================== -->
<!-- 02. INITIAL SETUP & CONNECTION                                        -->
<!-- ===================================================================== -->

<a id="sec-settings"></a>
<table width="100%" style="display: table; width: 100%; table-layout: fixed; border-collapse: collapse; margin-top: 64px; margin-bottom: 22px;">
  <thead>
    <tr>
      <th align="left" style="background: linear-gradient(90deg, #1B3B38 0%, #162226 100%); border: 1px solid #2CE5D0; border-left: 6px solid #2CE5D0; border-radius: 4px; padding: 18px 24px; font-size: 21px; color: #FFFFFF; letter-spacing: 0.4px;">
        <span style="color: #2CE5D0;">02 ·</span> INITIAL SETUP & CONNECTION
      </th>
    </tr>
  </thead>
</table>

<p style="color: #B8C0C8; font-size: 15px; margin-bottom: 16px;">Open the <b>Settings</b> tab — here you configure folder access, the public tunnel, and copy the connection URLs for your AI client:</p>

<div align="center" style="margin-bottom: 18px;">
  <img src="docs/images/Settings.svg" width="100%" style="display: block; width: 100%; border: 1px solid #262A30; border-radius: 4px; box-sizing: border-box;" alt="Settings">
</div>

<table width="100%" style="display: table; width: 100%; table-layout: fixed; border-collapse: collapse; border: none; margin-bottom: 16px;">
  <tr>
    <td width="32%" valign="top" style="background: #141619; border: 1px solid #262A30; border-radius: 4px; padding: 20px 22px;">
      <div style="color: #EAECEF; font-size: 16px; font-weight: 700; margin-bottom: 10px;">1. Allowed Folders</div>
      <div style="color: #969FA8; font-size: 14.5px; line-height: 1.55;">
        Directories accessible to the built-in <b style="color: #D6DCE2;">Filesystem MCP</b>. Uncheck any folder to revoke AI access immediately without restarting the gateway.
      </div>
    </td>
    <td width="2%" style="border: none; background: transparent; padding: 0;"></td>
    <td width="32%" valign="top" style="background: #141619; border: 1px solid #262A30; border-radius: 4px; padding: 20px 22px;">
      <div style="color: #EAECEF; font-size: 16px; font-weight: 700; margin-bottom: 10px;">2. Online access</div>
      <div style="color: #969FA8; font-size: 14.5px; line-height: 1.55;">
        Enables the public HTTPS tunnel via <b style="color: #D6DCE2;">Tailscale Funnel</b>. Required only for web-based AI apps. Local desktop clients on your PC work without it.
      </div>
    </td>
    <td width="2%" style="border: none; background: transparent; padding: 0;"></td>
    <td width="32%" valign="top" style="background: #141619; border: 1px solid #262A30; border-radius: 4px; padding: 20px 22px;">
      <div style="color: #EAECEF; font-size: 16px; font-weight: 700; margin-bottom: 10px;">3. Connection URLs</div>
      <div style="color: #969FA8; font-size: 14.5px; line-height: 1.55;">
        Ready-to-use gateway endpoints. Click <b style="color: #D6DCE2;">Copy URL</b> next to the address you need and paste it into your AI client settings.
      </div>
    </td>
  </tr>
</table>

<table width="100%" style="display: table; width: 100%; table-layout: fixed; border-collapse: collapse; border: none;">
  <tr>
    <td width="49%" valign="top" style="background: #141619; border: 1px solid #262A30; border-radius: 4px; padding: 20px 22px;">
      <div style="color: #EAECEF; font-size: 16px; font-weight: 700; margin-bottom: 8px;">LOCAL URL &nbsp;<code>http://127.0.0.1:8004/mcp</code></div>
      <div style="color: #969FA8; font-size: 14.5px; line-height: 1.55;">
        For applications installed on your PC (IDEs, desktop AI clients, local agents). Add a <code>Streamable HTTP</code> connection in your app's MCP settings and paste this URL.
      </div>
    </td>
    <td width="2%" style="border: none; background: transparent; padding: 0;"></td>
    <td width="49%" valign="top" style="background: #141619; border: 1px solid #262A30; border-radius: 4px; padding: 20px 22px;">
      <div style="color: #EAECEF; font-size: 16px; font-weight: 700; margin-bottom: 8px;">PUBLIC URL &nbsp;<code>https://&lt;device&gt;.&lt;tailnet&gt;.ts.net/mcp</code></div>
      <div style="color: #969FA8; font-size: 14.5px; line-height: 1.55;">
        For web AI applications that support MCP. Depending on the service, this section may be called <i>connectors, plugins, integrations</i>, or connected via third-party extensions — simply paste the copied URL there.
      </div>
    </td>
  </tr>
</table>

<!-- ===================================================================== -->
<!-- 03. TAILSCALE AUTHORIZATION                                           -->
<!-- ===================================================================== -->

<a id="sec-tailscale"></a>
<table width="100%" style="display: table; width: 100%; table-layout: fixed; border-collapse: collapse; margin-top: 88px; margin-bottom: 22px;">
  <thead>
    <tr>
      <th align="left" style="background: linear-gradient(90deg, #1B3B38 0%, #162226 100%); border: 1px solid #2CE5D0; border-left: 6px solid #2CE5D0; border-radius: 4px; padding: 18px 24px; font-size: 21px; color: #FFFFFF; letter-spacing: 0.4px;">
        <span style="color: #2CE5D0;">03 ·</span> TAILSCALE AUTHORIZATION (FOR WEB APPS)
      </th>
    </tr>
  </thead>
</table>

<p style="color: #B8C0C8; font-size: 15px; margin-bottom: 16px;">When clicking <b>Connect</b> under <i>Online access</i> for the first time, complete these three screens in order:</p>

<table width="100%" style="display: table; width: 100%; table-layout: fixed; border-collapse: collapse; border: none; margin-bottom: 16px;">
  <tr>
    <td width="32%" valign="top" style="background: #141619; border: 1px solid #262A30; border-radius: 4px; padding: 18px;">
      <div style="color: #EAECEF; font-size: 15.5px; font-weight: 700; margin-bottom: 12px;">STEP 1 · Welcome to Tailscale</div>
      <img src="docs/images/Tailscale_Welcome.svg" width="100%" style="display: block; width: 100%; border-radius: 3px; margin-bottom: 14px;" alt="Tailscale Welcome">
      <div style="color: #969FA8; font-size: 14px; line-height: 1.55;">
        The Tailscale application window will open. Click <b style="color: #D6DCE2;">Get Started</b>.
      </div>
    </td>
    <td width="2%" style="border: none; background: transparent; padding: 0;"></td>
    <td width="32%" valign="top" style="background: #141619; border: 1px solid #262A30; border-radius: 4px; padding: 18px;">
      <div style="color: #EAECEF; font-size: 15.5px; font-weight: 700; margin-bottom: 12px;">STEP 2 · Sign In & Connect</div>
      <img src="docs/images/Tailscale_Device_Connect.svg" width="100%" style="display: block; width: 100%; border-radius: 3px; margin-bottom: 14px;" alt="Tailscale Device Connect">
      <div style="color: #969FA8; font-size: 14px; line-height: 1.55;">
        A login page will open in your browser — <b style="color: #D6DCE2;">sign in to your account</b>, then click <b style="color: #D6DCE2;">Connect</b> on the <i>Connect device</i> screen.
      </div>
    </td>
    <td width="2%" style="border: none; background: transparent; padding: 0;"></td>
    <td width="32%" valign="top" style="background: #141619; border: 1px solid #262A30; border-radius: 4px; padding: 18px;">
      <div style="color: #EAECEF; font-size: 15.5px; font-weight: 700; margin-bottom: 12px;">STEP 3 · Start using Funnel</div>
      <img src="docs/images/Tailscale_Funnel.svg" width="100%" style="display: block; width: 100%; border-radius: 3px; margin-bottom: 14px;" alt="Tailscale Funnel">
      <div style="color: #969FA8; font-size: 14px; line-height: 1.55;">
        The <b style="color: #D6DCE2;">Start using Funnel</b> page will open automatically. Scroll down, click <b style="color: #D6DCE2;">Enable Funnel</b>, and wait for the connection to finish.
      </div>
    </td>
  </tr>
</table>

<table width="100%" style="display: table; width: 100%; table-layout: fixed; border-collapse: collapse; background: #141619; border: 1px solid #262A30; border-left: 3px solid #F5A623; border-radius: 4px;">
  <tr>
    <td style="padding: 18px 22px; border: none; color: #969FA8; font-size: 14.5px; line-height: 1.55;">
      <div style="color: #F5A623; font-size: 15.5px; font-weight: 700; margin-bottom: 6px;">If the Connect device page does not open on the first try:</div>
      You need to complete these exact three screens in order. If you see something else after signing in or the page does not load, return to the Tailscale window and <b style="color: #D6DCE2;">click Get Started again</b>.<br><br>
      Once the <i>Get Started</i> button in the Tailscale window changes into three buttons (including device details and <b style="color: #D6DCE2;">Close</b>), close that window by clicking <b style="color: #D6DCE2;">Close</b> and wait for the <b style="color: #D6DCE2;">Start using Funnel</b> page to open.
    </td>
  </tr>
</table>

<!-- ===================================================================== -->
<!-- 04. MAIN SCREEN (DASHBOARD)                                           -->
<!-- ===================================================================== -->

<a id="sec-dashboard"></a>
<table width="100%" style="display: table; width: 100%; table-layout: fixed; border-collapse: collapse; margin-top: 88px; margin-bottom: 22px;">
  <thead>
    <tr>
      <th align="left" style="background: linear-gradient(90deg, #1B3B38 0%, #162226 100%); border: 1px solid #2CE5D0; border-left: 6px solid #2CE5D0; border-radius: 4px; padding: 18px 24px; font-size: 21px; color: #FFFFFF; letter-spacing: 0.4px;">
        <span style="color: #2CE5D0;">04 ·</span> MAIN SCREEN (DASHBOARD)
      </th>
    </tr>
  </thead>
</table>

<p style="color: #B8C0C8; font-size: 15px; margin-bottom: 16px;">The <b>Dashboard</b> tab shows the gateway overview and currently enabled servers. Disabled servers (<code>DISABLED</code>) are hidden from the <i>MCP Services Status</i> table to keep it clean — they are visible in the <i>MCP Services</i> tab.</p>

<div align="center" style="margin-bottom: 18px;">
  <img src="docs/images/Main.svg" width="100%" style="display: block; width: 100%; border: 1px solid #262A30; border-radius: 4px; box-sizing: border-box;" alt="Dashboard">
</div>

<table width="100%" style="display: table; width: 100%; table-layout: fixed; border-collapse: collapse; border: none;">
  <tr>
    <td width="23.5%" valign="top" style="background: #141619; border: 1px solid #262A30; border-radius: 4px; padding: 16px 18px;">
      <div style="color: #2CE5D0; font-size: 14px; font-weight: 700; margin-bottom: 8px;">ACTIVE</div>
      <div style="color: #969FA8; font-size: 13.5px; line-height: 1.5;">The server is connected, verified, and exposing its tools to the chat.</div>
    </td>
    <td width="2%" style="border: none; background: transparent; padding: 0;"></td>
    <td width="23.5%" valign="top" style="background: #141619; border: 1px solid #262A30; border-radius: 4px; padding: 16px 18px;">
      <div style="color: #F5A623; font-size: 14px; font-weight: 700; margin-bottom: 8px;">WAITING</div>
      <div style="color: #969FA8; font-size: 13.5px; line-height: 1.5;">Waiting for an external application to start (e.g., Unreal Editor on port <code>:8000</code>).</div>
    </td>
    <td width="2%" style="border: none; background: transparent; padding: 0;"></td>
    <td width="23.5%" valign="top" style="background: #141619; border: 1px solid #262A30; border-radius: 4px; padding: 16px 18px;">
      <div style="color: #FF6B78; font-size: 14px; font-weight: 700; margin-bottom: 8px;">QUARANTINE</div>
      <div style="color: #969FA8; font-size: 13.5px; line-height: 1.5;">Startup or MCP protocol error. The server is isolated so other services keep working.</div>
    </td>
    <td width="2%" style="border: none; background: transparent; padding: 0;"></td>
    <td width="23.5%" valign="top" style="background: #141619; border: 1px solid #262A30; border-radius: 4px; padding: 16px 18px;">
      <div style="color: #7F8790; font-size: 14px; font-weight: 700; margin-bottom: 8px;">DISABLED</div>
      <div style="color: #7F8790; font-size: 13.5px; line-height: 1.5;">Manually disabled by the user (shown only in the <i>MCP Services</i> tab).</div>
    </td>
  </tr>
</table>

<!-- ===================================================================== -->
<!-- 05. MANAGING MCP SERVERS & PRESETS                                    -->
<!-- ===================================================================== -->

<a id="sec-servers"></a>
<table width="100%" style="display: table; width: 100%; table-layout: fixed; border-collapse: collapse; margin-top: 88px; margin-bottom: 22px;">
  <thead>
    <tr>
      <th align="left" style="background: linear-gradient(90deg, #1B3B38 0%, #162226 100%); border: 1px solid #2CE5D0; border-left: 6px solid #2CE5D0; border-radius: 4px; padding: 18px 24px; font-size: 21px; color: #FFFFFF; letter-spacing: 0.4px;">
        <span style="color: #2CE5D0;">05 ·</span> MANAGING MCP SERVERS & PRESETS
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
      <div style="color: #EAECEF; font-size: 17px; font-weight: 700; margin-bottom: 8px;">How Real-Time Server Switching Works</div>
      You can enable or disable any servers right during an active conversation with the AI. At any given moment, <b style="color: #D6DCE2;">only the tools belonging to currently enabled servers</b> are available in the dialogue.<br><br>
      You <b style="color: #D6DCE2;">do not need to reload the browser page</b>, restart your local client, or manually refresh the tool list in your AI settings — the gateway updates the available tools automatically.
    </td>
  </tr>
</table>

<table width="100%" style="display: table; width: 100%; table-layout: fixed; border-collapse: collapse; border: none;">
  <tr>
    <td width="32%" valign="top" style="background: #141619; border: 1px solid #262A30; border-radius: 4px; padding: 20px 22px;">
      <div style="color: #EAECEF; font-size: 16px; font-weight: 700; margin-bottom: 10px;">Server Presets</div>
      <div style="color: #969FA8; font-size: 14.5px; line-height: 1.55;">
        Check the combination of servers you need for a specific task and click <b style="color: #D6DCE2;">+ Save Preset</b>. Switch between saved profiles in 1 click without interrupting your session.
      </div>
    </td>
    <td width="2%" style="border: none; background: transparent; padding: 0;"></td>
    <td width="32%" valign="top" style="background: #141619; border: 1px solid #262A30; border-radius: 4px; padding: 20px 22px;">
      <div style="color: #EAECEF; font-size: 16px; font-weight: 700; margin-bottom: 10px;">Enable (ON) & Tools</div>
      <div style="color: #969FA8; font-size: 14.5px; line-height: 1.55;">
        The <b style="color: #D6DCE2;">ON</b> checkbox attaches a server to the chat on the fly or completely stops its process to free RAM, while <code>Tools: N</code> shows its active tool count.
      </div>
    </td>
    <td width="2%" style="border: none; background: transparent; padding: 0;"></td>
    <td width="32%" valign="top" style="background: #141619; border: 1px solid #262A30; border-radius: 4px; padding: 20px 22px;">
      <div style="color: #EAECEF; font-size: 16px; font-weight: 700; margin-bottom: 10px;">Edit & Remove</div>
      <div style="color: #969FA8; font-size: 14.5px; line-height: 1.55;">
        The <b style="color: #D6DCE2;">✎</b> button opens the JSON configuration editor for the selected server, and <b style="color: #D6DCE2;">✕</b> removes the server from the list.
      </div>
    </td>
  </tr>
</table>

<!-- ===================================================================== -->
<!-- 06. ADDING AN MCP SERVER                                              -->
<!-- ===================================================================== -->

<a id="sec-add"></a>
<table width="100%" style="display: table; width: 100%; table-layout: fixed; border-collapse: collapse; margin-top: 88px; margin-bottom: 22px;">
  <thead>
    <tr>
      <th align="left" style="background: linear-gradient(90deg, #1B3B38 0%, #162226 100%); border: 1px solid #2CE5D0; border-left: 6px solid #2CE5D0; border-radius: 4px; padding: 18px 24px; font-size: 21px; color: #FFFFFF; letter-spacing: 0.4px;">
        <span style="color: #2CE5D0;">06 ·</span> ADDING AN MCP SERVER
      </th>
    </tr>
  </thead>
</table>

<p style="color: #B8C0C8; font-size: 15px; margin-bottom: 16px;">Click <b>+ Add Server</b> in the <i>MCP Services</i> tab to add a new local or remote server:</p>

<div align="center" style="margin-bottom: 18px;">
  <img src="docs/images/Add_MCP.svg" width="100%" style="display: block; width: 100%; border: 1px solid #262A30; border-radius: 4px; box-sizing: border-box;" alt="Add MCP Server">
</div>

<table width="100%" style="display: table; width: 100%; table-layout: fixed; border-collapse: collapse; border: none;">
  <tr>
    <td width="32%" valign="top" style="background: #141619; border: 1px solid #262A30; border-radius: 4px; padding: 20px 22px;">
      <div style="color: #EAECEF; font-size: 16px; font-weight: 700; margin-bottom: 10px;">1. Paste JSON</div>
      <div style="color: #969FA8; font-size: 14.5px; line-height: 1.55;">
        Copy a standard <code>mcpServers</code> block from the server's documentation and click <b style="color: #D6DCE2;">Paste from Clipboard</b> (or edit the JSON manually).
      </div>
    </td>
    <td width="2%" style="border: none; background: transparent; padding: 0;"></td>
    <td width="32%" valign="top" style="background: #141619; border: 1px solid #262A30; border-radius: 4px; padding: 20px 22px;">
      <div style="color: #EAECEF; font-size: 16px; font-weight: 700; margin-bottom: 10px;">2. Verify & Add</div>
      <div style="color: #969FA8; font-size: 14.5px; line-height: 1.55;">
        Click <b style="color: #D6DCE2;">Verify & Add</b> — Bridge will test the startup, discover its tools, and immediately attach the server to the gateway.
      </div>
    </td>
    <td width="2%" style="border: none; background: transparent; padding: 0;"></td>
    <td width="32%" valign="top" style="background: #141619; border: 1px solid #262A30; border-radius: 4px; padding: 20px 22px;">
      <div style="color: #EAECEF; font-size: 16px; font-weight: 700; margin-bottom: 10px;">3. Save Without Check</div>
      <div style="color: #969FA8; font-size: 14.5px; line-height: 1.55;">
        If the server depends on an external app that is currently closed, click <b style="color: #D6DCE2;">Save Without Check</b> — it will be added in <code>WAITING</code> mode.
      </div>
    </td>
  </tr>
</table>

<!-- ===================================================================== -->
<!-- 07. SECURITY & USER RESPONSIBILITY                                    -->
<!-- ===================================================================== -->

<a id="sec-security"></a>
<table width="100%" style="display: table; width: 100%; table-layout: fixed; border-collapse: collapse; margin-top: 88px; margin-bottom: 22px;">
  <thead>
    <tr>
      <th align="left" style="background: linear-gradient(90deg, #332915 0%, #1F1B16 100%); border: 1px solid #F5A623; border-left: 6px solid #F5A623; border-radius: 4px; padding: 18px 24px; font-size: 21px; color: #FFFFFF; letter-spacing: 0.4px;">
        <span style="color: #F5A623;">07 ·</span> SECURITY & USER RESPONSIBILITY
      </th>
    </tr>
  </thead>
</table>

<table width="100%" style="display: table; width: 100%; table-layout: fixed; border-collapse: collapse; background: #262015; border: 1px solid #8C6A32; border-left: 5px solid #F5A623; border-radius: 4px; margin-bottom: 18px;">
  <tr>
    <td style="padding: 20px 24px; border: none; color: #EAECEF; font-size: 15px; line-height: 1.6;">
      <div style="color: #F5A623; font-size: 17px; font-weight: 700; margin-bottom: 8px;">Disclaimer (Use at Your Own Risk)</div>
      <b>MCP Bridge</b> is provided "AS IS", without warranty of any kind. The developer assumes no responsibility or liability for data loss or corruption, deleted files, leaked tokens, system malfunctions, or any actions performed by connected AI models and third-party MCP servers.<br><br>
      <b>Full responsibility for choosing which servers to run, which folders to expose, and keeping public URLs secure rests entirely with the user.</b>
    </td>
  </tr>
</table>

<table width="100%" style="display: table; width: 100%; table-layout: fixed; border-collapse: collapse; border: none;">
  <tr>
    <td width="49%" valign="top" style="background: #141619; border: 1px solid #8C6A32; border-radius: 4px; padding: 20px 22px;">
      <div style="color: #F5A623; font-size: 16px; font-weight: 700; margin-bottom: 10px;">1. Full Access to Allowed Folders</div>
      <div style="color: #969FA8; font-size: 14.5px; line-height: 1.55;">
        The built-in <code>Filesystem MCP</code> grants the AI permission to read, modify, and delete files inside checked directories in <b>Allowed Folders</b>. Never expose entire system drives (<code>C:\</code>) or folders containing passwords, wallets, or SSH keys.
      </div>
    </td>
    <td width="2%" style="border: none; background: transparent; padding: 0;"></td>
    <td width="49%" valign="top" style="background: #141619; border: 1px solid #8C6A32; border-radius: 4px; padding: 20px 22px;">
      <div style="color: #F5A623; font-size: 16px; font-weight: 700; margin-bottom: 10px;">2. Running Third-Party STDIO Processes</div>
      <div style="color: #969FA8; font-size: 14.5px; line-height: 1.55;">
        Any CLI server you add (<code>npx</code>, <code>uvx</code>, <code>python</code>, <code>node</code>, <code>.exe</code>) runs on your PC under your Windows user account. Only add servers and packages from sources you completely trust.
      </div>
    </td>
  </tr>
  <tr style="height: 16px;">
    <td colspan="3" style="border: none; background: transparent; padding: 0;"></td>
  </tr>
  <tr>
    <td width="49%" valign="top" style="background: #141619; border: 1px solid #8C6A32; border-radius: 4px; padding: 20px 22px;">
      <div style="color: #F5A623; font-size: 16px; font-weight: 700; margin-bottom: 10px;">3. Public Tailscale Funnel URL</div>
      <div style="color: #969FA8; font-size: 14.5px; line-height: 1.55;">
        When the tunnel is enabled, your <code>PUBLIC URL</code> is reachable from the internet by anyone who knows the address. Never share this URL publicly or expose it in screenshots, and click <b>Disconnect</b> when you no longer need remote access.
      </div>
    </td>
    <td width="2%" style="border: none; background: transparent; padding: 0;"></td>
    <td width="49%" valign="top" style="background: #141619; border: 1px solid #8C6A32; border-radius: 4px; padding: 20px 22px;">
      <div style="color: #F5A623; font-size: 16px; font-weight: 700; margin-bottom: 10px;">4. Local Storage of Keys & Tokens</div>
      <div style="color: #969FA8; font-size: 14.5px; line-height: 1.55;">
        Configured environment variables (<code>env</code>) and HTTP authorization headers (<code>Authorization</code> / API keys) are stored locally in plain text inside <code>Config/settings.json</code>. Do not share this file with others.
      </div>
    </td>
  </tr>
</table>

<hr style="border: none; border-top: 1px solid #262A30; margin: 64px 0 20px 0;">

<div align="center">
  <sub>
    <b>System Requirements:</b> Windows 10 x64 (1903+) / Windows 11 x64 · .NET Framework 4.8<br>
    <a href="docs/ARCHITECTURE.md">Architecture</a> · <a href="SECURITY.md">Security</a> · <a href="THIRD_PARTY_NOTICES.md">Third-Party Notices</a> · <a href="CONTRIBUTING.md">Contributing</a>
  </sub>
</div>~~~~~~~~

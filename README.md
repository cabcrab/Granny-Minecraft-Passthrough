# Granny Minecraft Passthrough

**Minecraft inside Granny Legacy.** The real Granny Legacy and the real Minecraft Java Edition run at the same time:
Minecraft's picture is blended into Granny's house, so you play Granny with Steve's hands, sword, bow, blocks and TNT.

> [!WARNING]
> **Very buggy.** This is an experimental fan project: expect glitches, wrong lighting, blocks that misbehave, crashes
> and things that just don't work. Have fun, but don't expect a polished mod.

![Granny meets a Minecraft crossbow](docs/screenshot-granny.png)

| | |
|---|---|
| ![Breaking Granny's bathroom wall with a pickaxe](docs/screenshot-breaking.png) | ![Minecraft HUD in Granny's house](docs/screenshot-house.png) |

## What it does

- Minecraft's world, your hand, held item and HUD are drawn **into Granny's frame**, hidden correctly behind Granny's
  walls and furniture.
- Granny's camera drives Minecraft's camera; **F5** gives third person with Steve visible.
- Granny's house is invisible **collision in Minecraft**: arrows stick in the walls, blocks can be placed on the floor
  and against walls, and Granny walks around the blocks you place.
- **Fight back:** swords, arrows, crossbows and TNT hurt Granny, Grandpa and the other creatures. They stagger, get
  knocked down and, after 3 knockdowns, stay down.
- **Minecraft movement** (walk, sprint, jump), while you can still crouch with **C** and hide under tables and beds.
- Granny's items go into Minecraft's hotbar (pick up with **E**, drop with **Q**).
- **Experimental:** break Granny's house like Minecraft blocks with your hand or tools (not TNT). Broken parts grow back
  after 30 seconds.

## Requirements

- Windows 10/11, 64-bit, and a GPU that runs both games at once
- **Granny Legacy** on Steam (Steam must be running while you play)
- **Minecraft: Java Edition**, owned on a Microsoft account
- About 3 GB of free disk space and an internet connection for the setup

## Install

1. Download **`Granny-Minecraft-Passthrough-0.1.0.zip`** from [Releases](../../releases) and extract it anywhere (for example to your
   Desktop).
2. Double-click **`Setup.bat`**. It finds Granny Legacy in your Steam libraries (or asks you for its folder) and
   prepares everything.
3. Double-click **`Play.bat`**. The first time, Prism Launcher opens: click through its quick setup, **sign in with
   your Microsoft account**, and Minecraft is downloaded (a few minutes). Minecraft starts first, then Granny Legacy.
   Press Play in Granny.

**Everything stays inside the extracted folder.** Nothing outside it is installed or changed:

| Folder | What it is |
|---|---|
| `game\Granny Legacy` | a **copy** of your Granny Legacy with BepInEx and the GrannyCraft plugin. Your Steam install is only read, never changed. |
| `launcher` | Prism Launcher in portable mode: your login, settings and the "GrannyCraft" Minecraft instance (26.3 + Fabric) |
| `java` | Java 25 for that Minecraft |
| `downloads` | the downloaded files. You can delete it after setup. |

Everything is downloaded from its official source and checked against a fixed SHA-256 (see `scripts\common.ps1`).
Your normal Minecraft (`.minecraft`, the official launcher, other launchers) is never touched.

**Uninstall:** delete the extracted folder. (Granny Legacy keeps its own settings and saves in the usual place, as
it always does, shared with your Steam install.)

## Controls

| Key | Does |
|---|---|
| Left click | Minecraft attack / break blocks (fires Granny's gun if you hold it) |
| Right click | Minecraft use: place blocks, bow, crossbow, flint and steel on TNT. Also opens doors when Granny shows the door prompt. |
| E | Granny's interaction: open doors, pick up Granny's items |
| Q | drop the selected item (a Minecraft item, or Granny's item if that slot is selected) |
| 1-9, mouse wheel | Minecraft hotbar (there is no inventory screen, only the hotbar) |
| W A S D, Space, Ctrl | Minecraft movement: walk, jump, sprint |
| C | crouch (hide under tables and beds) |
| F5 | first person / third person behind / third person in front |
| F8 | passthrough on/off (off = plain Granny) |
| F10 | breakable house on/off |
| F7 / F9 | compositor debug view / status line |

## Settings

After the first start: `game\Granny Legacy\BepInEx\config\grannycraft.passthrough.cfg` (open it with Notepad).
It covers keys, block scale (`BlocksPerUnit`), creature health, knockdowns, lighting, the breakable house and its
regrow time (`HouseRegenSeconds`).

## Troubleshooting

- **Granny closes at the menu:** Steam is not running. Start Steam, then Play.bat.
- **The first Granny start is slow:** BepInEx prepares itself the first time (1-3 minutes). Later starts are fast.
- **No Minecraft in Granny:** Granny connects on its own once Minecraft has loaded its world (the Minecraft window
  stays behind Granny). Check that F8 hasn't turned the passthrough off. `Play.bat -Windowed` runs Granny in a window,
  which makes it easier to see what is happening.
- **Windows protected your PC / SmartScreen:** the .bat files are plain text scripts. Open them with Notepad to check
  them, then choose *More info, Run anyway*.
- Logs: `game\Granny Legacy\BepInEx\LogOutput.log` and `launcher\instances\GrannyCraft\minecraft\logs\latest.log`.

## Using another launcher

The Minecraft side is a normal Fabric mod. In any launcher, use a **separate profile or game directory** with
Minecraft **26.3**, **Fabric Loader 0.19.5**, [Fabric API](https://modrinth.com/mod/fabric-api) 0.161.0+26.3 and
`bin\grannycraft-0.1.0.jar` in its `mods` folder. The mod changes some video options and opens its own empty
"GrannyCraft" world automatically, so don't use it in your main profile. You still need Setup.bat for the Granny side.

## How it works

- **Granny side:** a BepInEx 6 plugin (C#, `granny-plugin/`) plus a small D3D11 compositor (C++, `granny-native/`).
  Every frame it reads Minecraft's colour and depth from shared memory and blends them into Granny's image against
  Granny's depth buffer. It also sends Granny's camera and house geometry to Minecraft, and turns Minecraft hits,
  arrows and explosions into damage on Granny's creatures.
- **Minecraft side:** a Fabric mod (Java, `minecraft-mod/`) that follows Granny's camera, rebuilds Granny's house as
  invisible collision blocks, exports its frames and talks to the plugin over a local WebSocket (127.0.0.1:25600).
- Nothing from either game is included in this project: the mod only reads the games while they run.

## Building from source

You only need this if you change the code. Requirements:

- JDK 25, with `JAVA_HOME` set
- .NET SDK 6 or newer
- Visual Studio Build Tools with the C++ workload
- Setup.bat and one Play.bat run first: the plugin compiles against files BepInEx generates from the game on its first start

```bat
powershell -ExecutionPolicy Bypass -File scripts\build.ps1 -Install
```

## Credits and license

MIT, see [LICENSE](LICENSE). The passthrough core is adapted from universal-modder's `minecraft-gta5-passthrough` (MIT).
See [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md) for everything else.

Not affiliated with, approved or endorsed by Mojang, Microsoft or the makers of Granny Legacy.
